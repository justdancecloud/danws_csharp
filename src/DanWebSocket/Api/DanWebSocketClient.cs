using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DanWebSocket.Connection;
using DanWebSocket.Protocol;
using DanWebSocket.State;

namespace DanWebSocket.Api
{
    public enum ClientState
    {
        Disconnected,
        Connecting,
        Identifying,
        Authorizing,
        Synchronizing,
        Ready,
        Reconnecting,
    }

    public class ClientOptions
    {
        public ReconnectOptions? Reconnect { get; set; }
        public bool Debug { get; set; }
    }

    /// <summary>
    /// DanProtocol v3.5 WebSocket client for real-time state synchronization.
    /// </summary>
    public class DanWebSocketClient : IDisposable
    {
        // Protocol version
        private const byte ProtocolMajor = 3;
        private const byte ProtocolMinor = 5;

        public string Id { get; }
        private ClientState _state = ClientState.Disconnected;
        public ClientState State => _state;

        private readonly string _url;
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _receiveCts;
        private bool _intentionalDisconnect;
        private bool _debug;

        // Server->Client key registry and state
        private readonly KeyRegistry _registry = new KeyRegistry();
        private readonly Dictionary<uint, object?> _store = new Dictionary<uint, object?>();
        private readonly Dictionary<uint, Frame> _pendingValues = new Dictionary<uint, Frame>();
        private bool _readyDeferred;

        // Topic state (client->server)
        private readonly Dictionary<string, Dictionary<string, object?>?> _subscriptions = new Dictionary<string, Dictionary<string, object?>?>();
        private bool _topicDirty;
        private readonly Dictionary<string, TopicClientHandle> _topicClientHandles = new Dictionary<string, TopicClientHandle>();
        private readonly Dictionary<string, int> _topicIndexMap = new Dictionary<string, int>();
        private readonly Dictionary<int, string> _indexToTopic = new Dictionary<int, string>();
        private readonly Dictionary<uint, TopicInfo?> _topicKeyCache = new Dictionary<uint, TopicInfo?>();

        // Connection layers
        private readonly BulkQueue _bulkQueue;
        private readonly HeartbeatManager _heartbeat = new HeartbeatManager();
        private readonly ReconnectEngine _reconnectEngine;
        private readonly StreamParser _parser = new StreamParser();

        // Events
        public event Action? OnConnect;
        public event Action? OnDisconnect;
        public event Action? OnReady;
        public event Action<string, object?>? OnReceive;
        public event Action? OnUpdate;
        public event Action<DanWSException>? OnError;
        public event Action<int, long>? OnReconnecting;
        public event Action? OnReconnect;
        public event Action? OnReconnectFailed;

        // Synchronization context for thread safety
        private readonly object _lock = new object();

        public DanWebSocketClient(string url, ClientOptions? options = null)
        {
            _url = url;
            Id = GenerateUUIDv7();
            _debug = options?.Debug ?? false;
            _reconnectEngine = new ReconnectEngine(options?.Reconnect);
            _bulkQueue = new BulkQueue(emitFlushEnd: false);

            _parser.OnFrame += HandleFrame;
            _parser.OnHeartbeat += () => _heartbeat.Received();
            _parser.OnError += (err) =>
            {
                Log("Stream parser error", err);
                if (err is DanWSException dex) EmitError(dex);
            };

            SetupInternals();
        }

        /// <summary>
        /// Get the current value for a server-registered key.
        /// </summary>
        public object? Get(string key)
        {
            lock (_lock)
            {
                var entry = _registry.GetByPath(key);
                if (entry == null) return null;
                _store.TryGetValue(entry.KeyId, out var val);
                return val;
            }
        }

        /// <summary>
        /// List all server-registered key paths.
        /// </summary>
        public List<string> Keys
        {
            get
            {
                lock (_lock)
                {
                    return _registry.Paths;
                }
            }
        }

        /// <summary>
        /// Get an ArrayView for a flattened array key.
        /// </summary>
        public ArrayView Array(string key)
        {
            return new ArrayView(key, _registry, id =>
            {
                lock (_lock)
                {
                    _store.TryGetValue(id, out var val);
                    return val;
                }
            });
        }

        /// <summary>
        /// Connect to the server.
        /// </summary>
        public void Connect()
        {
            lock (_lock)
            {
                if (_state != ClientState.Disconnected && _state != ClientState.Reconnecting) return;
                _intentionalDisconnect = false;
                _state = ClientState.Connecting;
            }

            _ = ConnectAsync();
        }

        /// <summary>
        /// Disconnect from the server.
        /// </summary>
        public void Disconnect()
        {
            lock (_lock)
            {
                _intentionalDisconnect = true;
                _reconnectEngine.Stop();
                Cleanup();
                _state = ClientState.Disconnected;
            }
            OnDisconnect?.Invoke();
        }

        /// <summary>
        /// Send an authorization token.
        /// </summary>
        public void Authorize(string token)
        {
            lock (_lock)
            {
                if (_ws == null || _ws.State != WebSocketState.Open) return;
                var frame = new Frame(FrameType.Auth, 0, DataType.String, token);
                SendFrame(frame);
                _state = ClientState.Authorizing;
            }
        }

        /// <summary>
        /// Subscribe to a topic.
        /// </summary>
        public void Subscribe(string topicName, Dictionary<string, object?>? parms = null)
        {
            lock (_lock)
            {
                _subscriptions[topicName] = parms;
            }
            SendTopicSync();
        }

        /// <summary>
        /// Unsubscribe from a topic.
        /// </summary>
        public void Unsubscribe(string topicName)
        {
            bool removed;
            lock (_lock)
            {
                removed = _subscriptions.Remove(topicName);
                _topicClientHandles.Remove(topicName);
            }
            if (removed) SendTopicSync();
        }

        /// <summary>
        /// Get a topic client handle for scoped data access.
        /// </summary>
        public TopicClientHandle Topic(string name)
        {
            lock (_lock)
            {
                if (_topicClientHandles.TryGetValue(name, out var handle))
                    return handle;

                _topicIndexMap.TryGetValue(name, out int idx);
                handle = new TopicClientHandle(name, idx, _registry, id =>
                {
                    _store.TryGetValue(id, out var val);
                    return val;
                });
                _topicClientHandles[name] = handle;
                return handle;
            }
        }

        // ---- Internal Implementation ----

        private void SetupInternals()
        {
            _bulkQueue.OnFlush += data => SendRaw(data);

            _heartbeat.OnSend += data => SendRaw(data);
            _heartbeat.OnTimeout += () =>
            {
                EmitError(new DanWSException("HEARTBEAT_TIMEOUT", "No heartbeat received within 15 seconds"));
                HandleClose();
            };

            _reconnectEngine.OnReconnecting += (attempt, delay) =>
            {
                OnReconnecting?.Invoke(attempt, delay);
            };
            _reconnectEngine.OnAttempt += () => Connect();
            _reconnectEngine.OnExhausted += () =>
            {
                lock (_lock) { _state = ClientState.Disconnected; }
                EmitError(new DanWSException("RECONNECT_EXHAUSTED", "All reconnection attempts exhausted"));
                OnReconnectFailed?.Invoke();
            };
        }

        private async Task ConnectAsync()
        {
            try
            {
                var ws = new ClientWebSocket();
                lock (_lock)
                {
                    _ws = ws;
                }

                _receiveCts = new CancellationTokenSource();
                await ws.ConnectAsync(new Uri(_url), _receiveCts.Token).ConfigureAwait(false);

                HandleOpen();

                // Start receive loop
                _ = ReceiveLoopAsync(ws, _receiveCts.Token);
            }
            catch (Exception ex)
            {
                Log("Connect failed", ex);
                HandleClose();
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[8192];
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        HandleClose();
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        var chunk = new byte[result.Count];
                        System.Array.Copy(buffer, chunk, result.Count);
                        _heartbeat.Received();

                        lock (_lock)
                        {
                            _parser.Feed(chunk);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on disconnect */ }
            catch (WebSocketException)
            {
                HandleClose();
            }
            catch (Exception ex)
            {
                Log("Receive loop error", ex);
                HandleClose();
            }
        }

        private void HandleOpen()
        {
            lock (_lock)
            {
                _state = ClientState.Identifying;
                _heartbeat.Start();

                // Send IDENTIFY frame: 16-byte UUIDv7 + 2-byte protocol version
                var identifyPayload = BuildIdentifyPayload();
                var frame = new Frame(FrameType.Identify, 0, DataType.Binary, identifyPayload);
                SendFrame(frame);
            }

            OnConnect?.Invoke();

            lock (_lock)
            {
                if (_topicDirty && _subscriptions.Count > 0)
                    SendTopicSync();
            }
        }

        private void HandleClose()
        {
            bool wasIntentional;
            lock (_lock)
            {
                _heartbeat.Stop();
                _bulkQueue.Clear();

                if (_ws != null)
                {
                    try { _receiveCts?.Cancel(); } catch { }
                    try { _ws.Dispose(); } catch { }
                    _ws = null;
                }

                wasIntentional = _intentionalDisconnect;
                if (wasIntentional) return;
            }

            OnDisconnect?.Invoke();

            lock (_lock)
            {
                if (_reconnectEngine.IsActive)
                {
                    _state = ClientState.Reconnecting;
                    _reconnectEngine.Retry();
                }
                else
                {
                    _state = ClientState.Reconnecting;
                    _reconnectEngine.Start();
                }
            }
        }

        private void HandleFrame(Frame frame)
        {
            // Called under _lock from parser.Feed in ReceiveLoopAsync
            switch (frame.FrameType)
            {
                case FrameType.AuthOk:
                    _state = ClientState.Synchronizing;
                    break;

                case FrameType.AuthFail:
                    _intentionalDisconnect = true;
                    var reason = frame.Payload?.ToString() ?? "";
                    EmitError(new DanWSException("AUTH_REJECTED", reason));
                    Cleanup();
                    _state = ClientState.Disconnected;
                    ThreadPool.QueueUserWorkItem(_ => OnDisconnect?.Invoke());
                    break;

                case FrameType.ServerKeyRegistration:
                {
                    if (_state == ClientState.Identifying)
                        _state = ClientState.Synchronizing;

                    string keyPath = (string)frame.Payload!;
                    _registry.RegisterOne(frame.KeyId, keyPath, frame.DataType);

                    if (_pendingValues.TryGetValue(frame.KeyId, out var pending))
                    {
                        _pendingValues.Remove(frame.KeyId);
                        _store[frame.KeyId] = pending.Payload;

                        var topicInfo = GetTopicInfo(frame.KeyId, keyPath);
                        if (topicInfo != null)
                        {
                            NotifyTopic(topicInfo, pending.Payload);
                        }
                        else
                        {
                            ThreadPool.QueueUserWorkItem(_ => OnReceive?.Invoke(keyPath, pending.Payload));
                        }
                    }
                    break;
                }

                case FrameType.ServerSync:
                {
                    if (_state == ClientState.Identifying)
                        _state = ClientState.Synchronizing;

                    if (_state != ClientState.Ready)
                    {
                        _bulkQueue.Enqueue(new Frame(FrameType.ClientReady, 0, DataType.Null, null));
                    }

                    if (_registry.Size == 0)
                    {
                        _state = ClientState.Ready;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            OnReady?.Invoke();
                            if (_reconnectEngine.IsActive)
                            {
                                _reconnectEngine.Stop();
                                OnReconnect?.Invoke();
                            }
                        });
                        if (_subscriptions.Count > 0) SendTopicSync();
                    }
                    break;
                }

                case FrameType.ServerValue:
                {
                    if (!_registry.HasKeyId(frame.KeyId))
                    {
                        _bulkQueue.Enqueue(new Frame(FrameType.ClientKeyRequest, frame.KeyId, DataType.Null, null));
                        _pendingValues[frame.KeyId] = frame;
                        break;
                    }

                    _store[frame.KeyId] = frame.Payload;

                    var entry = _registry.GetByKeyId(frame.KeyId);
                    if (entry != null)
                    {
                        var topicInfo = GetTopicInfo(frame.KeyId, entry.Path);
                        if (topicInfo != null)
                        {
                            NotifyTopic(topicInfo, frame.Payload);
                        }
                        else
                        {
                            var path = entry.Path;
                            var val = frame.Payload;
                            ThreadPool.QueueUserWorkItem(_ => OnReceive?.Invoke(path, val));
                        }
                    }

                    if (_state == ClientState.Synchronizing && !_readyDeferred)
                    {
                        _readyDeferred = true;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            lock (_lock)
                            {
                                _readyDeferred = false;
                                if (_state != ClientState.Synchronizing) return;
                                _state = ClientState.Ready;
                            }
                            OnReady?.Invoke();
                            if (_reconnectEngine.IsActive)
                            {
                                _reconnectEngine.Stop();
                                OnReconnect?.Invoke();
                            }
                            lock (_lock)
                            {
                                if (_subscriptions.Count > 0) SendTopicSync();
                            }
                        });
                    }
                    break;
                }

                case FrameType.ArrayShiftLeft:
                    HandleArrayShift(frame, isLeft: true);
                    break;

                case FrameType.ArrayShiftRight:
                    HandleArrayShift(frame, isLeft: false);
                    break;

                case FrameType.ServerFlushEnd:
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        OnUpdate?.Invoke();
                        lock (_lock)
                        {
                            foreach (var handle in _topicClientHandles.Values)
                                handle.FlushUpdate();
                        }
                    });
                    break;

                case FrameType.ServerReady:
                    break;

                case FrameType.ServerKeyDelete:
                {
                    var deletedEntry = _registry.GetByKeyId(frame.KeyId);
                    var deletedTopicInfo = deletedEntry != null ? GetTopicInfo(frame.KeyId, deletedEntry.Path) : null;
                    _registry.RemoveByKeyId(frame.KeyId);
                    _store.Remove(frame.KeyId);
                    _topicKeyCache.Remove(frame.KeyId);
                    if (deletedEntry != null)
                    {
                        if (deletedTopicInfo != null)
                        {
                            NotifyTopic(deletedTopicInfo, null);
                        }
                        else
                        {
                            var path = deletedEntry.Path;
                            ThreadPool.QueueUserWorkItem(_ => OnReceive?.Invoke(path, null));
                        }
                    }
                    break;
                }

                case FrameType.ServerReset:
                    _registry.Clear();
                    _store.Clear();
                    _pendingValues.Clear();
                    _topicKeyCache.Clear();
                    _readyDeferred = false;
                    _state = ClientState.Synchronizing;
                    break;

                case FrameType.Error:
                    EmitError(new DanWSException("REMOTE_ERROR", frame.Payload?.ToString() ?? ""));
                    break;

                case FrameType.ServerResyncReq:
                    // Server requests re-send of subscriptions
                    if (_subscriptions.Count > 0) SendTopicSync();
                    break;
            }
        }

        private void HandleArrayShift(Frame frame, bool isLeft)
        {
            var lengthEntry = _registry.GetByKeyId(frame.KeyId);
            if (lengthEntry == null) return;

            string lengthPath = lengthEntry.Path;
            var topicInfo = GetTopicInfo(frame.KeyId, lengthPath);
            string prefix;
            string? userPrefix = null;

            if (topicInfo != null)
            {
                string userKey = topicInfo.UserKey;
                userPrefix = userKey.Substring(0, userKey.Length - ".length".Length);
                prefix = lengthPath.Substring(0, lengthPath.Length - ".length".Length);
            }
            else
            {
                prefix = lengthPath.Substring(0, lengthPath.Length - ".length".Length);
            }

            int rawShift = frame.Payload is int rv ? rv : 0;
            _store.TryGetValue(frame.KeyId, out var currentLenObj);
            int currentLength = currentLenObj is int cl ? cl : 0;
            int shiftCount = Math.Max(0, Math.Min(rawShift, currentLength));

            if (isLeft)
            {
                for (int i = 0; i < currentLength - shiftCount; i++)
                {
                    var src = _registry.GetByPath($"{prefix}.{i + shiftCount}");
                    var dst = _registry.GetByPath($"{prefix}.{i}");
                    if (src != null && dst != null)
                    {
                        _store.TryGetValue(src.KeyId, out var val);
                        _store[dst.KeyId] = val;
                    }
                }
                int newLength = currentLength - shiftCount;
                _store[frame.KeyId] = newLength;

                if (topicInfo != null)
                {
                    NotifyTopic(topicInfo, newLength);
                }
                else
                {
                    var p = prefix + ".length";
                    var v = newLength;
                    ThreadPool.QueueUserWorkItem(_ => OnReceive?.Invoke(p, v));
                }
            }
            else
            {
                for (int i = currentLength - 1; i >= 0; i--)
                {
                    var src = _registry.GetByPath($"{prefix}.{i}");
                    var dst = _registry.GetByPath($"{prefix}.{i + shiftCount}");
                    if (src != null && dst != null)
                    {
                        _store.TryGetValue(src.KeyId, out var val);
                        _store[dst.KeyId] = val;
                    }
                }

                if (topicInfo != null)
                {
                    NotifyTopic(topicInfo, currentLength);
                }
                else
                {
                    var p = prefix + ".length";
                    var v = currentLength;
                    ThreadPool.QueueUserWorkItem(_ => OnReceive?.Invoke(p, v));
                }
            }
        }

        private void SendTopicSync()
        {
            lock (_lock)
            {
                if (_ws == null || _ws.State != WebSocketState.Open)
                {
                    _topicDirty = true;
                    return;
                }

                var entries = new List<(string path, object? value)>();
                _topicIndexMap.Clear();
                _indexToTopic.Clear();
                int idx = 0;

                foreach (var kvp in _subscriptions)
                {
                    string topicName = kvp.Key;
                    var parms = kvp.Value;

                    _topicIndexMap[topicName] = idx;
                    _indexToTopic[idx] = topicName;

                    if (!_topicClientHandles.TryGetValue(topicName, out var handle))
                    {
                        handle = new TopicClientHandle(topicName, idx, _registry, id =>
                        {
                            _store.TryGetValue(id, out var val);
                            return val;
                        });
                        _topicClientHandles[topicName] = handle;
                    }
                    else
                    {
                        handle.SetIndex(idx);
                    }

                    entries.Add(($"topic.{idx}.name", topicName));
                    if (parms != null)
                    {
                        foreach (var p in parms)
                            entries.Add(($"topic.{idx}.param.{p.Key}", p.Value));
                    }
                    idx++;
                }

                // Send ClientReset
                SendFrame(new Frame(FrameType.ClientReset, 0, DataType.Null, null));

                // Send ClientKeyRegistration + ClientValue for each entry
                uint keyId = 1;
                var keyIds = new List<(uint id, object? value, DataType dataType)>();
                foreach (var entry in entries)
                {
                    DataType dt = DetectDataType(entry.value);
                    SendFrame(new Frame(FrameType.ClientKeyRegistration, keyId, dt, entry.path));
                    keyIds.Add((keyId, entry.value, dt));
                    keyId++;
                }

                foreach (var e in keyIds)
                    SendFrame(new Frame(FrameType.ClientValue, e.id, e.dataType, e.value));

                // Send ClientSync
                SendFrame(new Frame(FrameType.ClientSync, 0, DataType.Null, null));

                _topicDirty = false;
            }
        }

        private void SendFrame(Frame frame)
        {
            SendRaw(Codec.Encode(frame));
        }

        private void SendRaw(byte[] data)
        {
            ClientWebSocket? ws;
            lock (_lock)
            {
                ws = _ws;
            }
            if (ws != null && ws.State == WebSocketState.Open)
            {
                try
                {
                    ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { /* ignore send errors */ }
            }
        }

        private void Cleanup()
        {
            _heartbeat.Stop();
            _bulkQueue.Clear();
            if (_ws != null)
            {
                try { _receiveCts?.Cancel(); } catch { }
                try { _ws.Dispose(); } catch { }
                _ws = null;
            }
        }

        private byte[] BuildIdentifyPayload()
        {
            byte[] uuid = ParseUUIDBytes(Id);
            var payload = new byte[18]; // 16 UUID + 2 version
            System.Array.Copy(uuid, payload, 16);
            payload[16] = ProtocolMajor;
            payload[17] = ProtocolMinor;
            return payload;
        }

        private TopicInfo? GetTopicInfo(uint keyId, string path)
        {
            if (_topicKeyCache.TryGetValue(keyId, out var cached))
                return cached;

            TopicInfo? info = null;
            if (path.Length > 2 && path[0] == 't' && path[1] == '.')
            {
                int secondDot = path.IndexOf('.', 2);
                if (secondDot >= 0 && int.TryParse(path.Substring(2, secondDot - 2), out int idx))
                {
                    info = new TopicInfo(idx, path.Substring(secondDot + 1));
                }
            }

            _topicKeyCache[keyId] = info;
            return info;
        }

        private void NotifyTopic(TopicInfo topicInfo, object? value)
        {
            if (_indexToTopic.TryGetValue(topicInfo.TopicIdx, out var topicName)
                && _topicClientHandles.TryGetValue(topicName, out var handle))
            {
                handle.Notify(topicInfo.UserKey, value);
            }
        }

        private void EmitError(DanWSException err)
        {
            if (OnError != null)
            {
                ThreadPool.QueueUserWorkItem(_ => OnError?.Invoke(err));
            }
            else
            {
                Log($"Unhandled DanWSError: {err.Code} {err.Message}", err);
            }
        }

        private void Log(string msg, Exception? err = null)
        {
            if (_debug)
            {
                Console.Error.WriteLine($"[dan-ws client] {msg} {err?.Message ?? ""}");
            }
        }

        private static DataType DetectDataType(object? value)
        {
            if (value == null) return DataType.Null;
            if (value is bool) return DataType.Bool;
            if (value is int) return DataType.VarInteger;
            if (value is long l)
            {
                return l >= 0 ? DataType.Uint64 : DataType.Int64;
            }
            if (value is float) return DataType.VarFloat;
            if (value is double) return DataType.VarDouble;
            if (value is string) return DataType.String;
            if (value is byte[]) return DataType.Binary;
            if (value is DateTime || value is DateTimeOffset) return DataType.Timestamp;
            return DataType.String; // fallback: serialize as string
        }

        private static string GenerateUUIDv7()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var bytes = new byte[16];

            bytes[0] = (byte)((now >> 40) & 0xFF);
            bytes[1] = (byte)((now >> 32) & 0xFF);
            bytes[2] = (byte)((now >> 24) & 0xFF);
            bytes[3] = (byte)((now >> 16) & 0xFF);
            bytes[4] = (byte)((now >> 8) & 0xFF);
            bytes[5] = (byte)(now & 0xFF);

            var random = new byte[10];
            var rng = new Random();
            rng.NextBytes(random);
            System.Array.Copy(random, 0, bytes, 6, 10);

            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant 10

            var hex = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            return $"{hex.Substring(0, 8)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}-{hex.Substring(16, 4)}-{hex.Substring(20, 12)}";
        }

        private static byte[] ParseUUIDBytes(string uuid)
        {
            string hex = uuid.Replace("-", "");
            var bytes = new byte[16];
            for (int i = 0; i < 16; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        public void Dispose()
        {
            Disconnect();
            _reconnectEngine.Dispose();
            _heartbeat.Dispose();
            _bulkQueue.Dispose();
        }

        private class TopicInfo
        {
            public int TopicIdx { get; }
            public string UserKey { get; }

            public TopicInfo(int topicIdx, string userKey)
            {
                TopicIdx = topicIdx;
                UserKey = userKey;
            }
        }
    }
}
