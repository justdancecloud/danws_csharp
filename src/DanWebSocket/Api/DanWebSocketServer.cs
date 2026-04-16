using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DanWebSocket.Connection;
using DanWebSocket.Protocol;
using DanWebSocket.State;

namespace DanWebSocket.Api
{
    public enum Mode
    {
        Broadcast,
        Principal,
        SessionTopic,
        SessionPrincipalTopic,
    }

    public class Metrics
    {
        public int ActiveSessions { get; }
        public int PendingSessions { get; }
        public int PrincipalCount { get; }
        public long FramesIn { get; }
        public long FramesOut { get; }

        public Metrics(int activeSessions, int pendingSessions, int principalCount, long framesIn, long framesOut)
        {
            ActiveSessions = activeSessions;
            PendingSessions = pendingSessions;
            PrincipalCount = principalCount;
            FramesIn = framesIn;
            FramesOut = framesOut;
        }
    }

    /// <summary>
    /// DanProtocol v3.5 WebSocket server for real-time state synchronization.
    /// </summary>
    public class DanWebSocketServer : IDisposable
    {
        private const byte ProtocolMajor = 3;
        private const byte ProtocolMinor = 5;
        private const string BroadcastPrincipal = "__broadcast__";

        public Mode Mode { get; }
        public TopicNamespace Topic { get; }

        private readonly PrincipalManager _principals = new PrincipalManager();
        private readonly HttpListener _listener;
        private readonly string _path;
        private readonly long _ttl;
        private bool _authEnabled;
        private long _authTimeout = 5000;
        private readonly int? _flushIntervalMs;
        private readonly Dictionary<string, InternalSession> _sessions = new Dictionary<string, InternalSession>();
        private readonly Dictionary<string, InternalSession> _tmpSessions = new Dictionary<string, InternalSession>();
        private readonly Dictionary<string, HashSet<InternalSession>> _principalIndex = new Dictionary<string, HashSet<InternalSession>>();

        // Rate limits & metrics
        private int _maxConnections;
        private int _maxFramesPerSec;
        private long _framesIn;
        private long _framesOut;
        private readonly Dictionary<string, (int count, long windowStart)> _frameCounters = new Dictionary<string, (int, long)>();

        // Events
        private readonly List<Action<DanWebSocketSession>> _onConnection = new List<Action<DanWebSocketSession>>();
        private readonly List<Action<string, string>> _onAuthorize = new List<Action<string, string>>();
        private readonly List<Action<DanWebSocketSession>> _onSessionExpired = new List<Action<DanWebSocketSession>>();

        private readonly object _lock = new object();
        private CancellationTokenSource? _cts;

        public DanWebSocketServer(int port, string path = "/", Mode mode = Mode.Principal, long ttlMs = 600_000, int? flushIntervalMs = null)
        {
            Mode = mode;
            _path = path.StartsWith("/") ? path : "/" + path;
            _ttl = ttlMs;
            _flushIntervalMs = flushIntervalMs;
            Topic = new TopicNamespace();

            if (!IsTopicMode)
            {
                _principals.SetOnNewPrincipal(ptx => BindPrincipalTX(ptx));
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{port}{_path}");

            _cts = new CancellationTokenSource();

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException)
            {
                // Fallback to localhost-only binding
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}{_path}");
                _listener.Start();
            }

            _ = AcceptLoop(_cts.Token);
        }

        private bool IsTopicMode => Mode == Mode.SessionTopic || Mode == Mode.SessionPrincipalTopic;

        // ---- Broadcast mode API ----

        public void Set(string key, object? value)
        {
            AssertMode(Mode.Broadcast, "Set");
            lock (_lock) { _principals.Principal(BroadcastPrincipal).Set(key, value); }
        }

        public object? Get(string key)
        {
            AssertMode(Mode.Broadcast, "Get");
            lock (_lock) { return _principals.Principal(BroadcastPrincipal).Get(key); }
        }

        public List<string> Keys
        {
            get
            {
                if (Mode != Mode.Broadcast) return new List<string>();
                lock (_lock) { return _principals.Principal(BroadcastPrincipal).Keys; }
            }
        }

        public void Clear(string? key = null)
        {
            AssertMode(Mode.Broadcast, "Clear");
            lock (_lock) { _principals.Principal(BroadcastPrincipal).Clear(key); }
        }

        // ---- Principal mode API ----

        public PrincipalTX Principal(string name)
        {
            if (Mode != Mode.Principal && Mode != Mode.SessionPrincipalTopic)
                throw new DanWSException("INVALID_MODE", "server.Principal() is only available in principal/session_principal_topic mode.");
            lock (_lock) { return _principals.Principal(name); }
        }

        // ---- Common API ----

        public void SetMaxConnections(int max) { lock (_lock) { _maxConnections = max; } }
        public void SetMaxFramesPerSec(int max) { lock (_lock) { _maxFramesPerSec = max; } }

        public Metrics GetMetrics()
        {
            lock (_lock)
            {
                return new Metrics(
                    _sessions.Count,
                    _tmpSessions.Count,
                    _principals.Size,
                    _framesIn,
                    _framesOut
                );
            }
        }

        public void EnableAuthorization(bool enabled, long timeoutMs = 5000)
        {
            lock (_lock)
            {
                _authEnabled = enabled;
                _authTimeout = timeoutMs;
            }
        }

        public void Authorize(string clientUuid, string token, string principal)
        {
            if (principal == null)
                throw new ArgumentException("authorize(): principal must not be null. To deny a token, call Reject(clientUuid, reason) instead.");

            lock (_lock)
            {
                if (!_tmpSessions.TryGetValue(clientUuid, out var iSession)) return;

                _tmpSessions.Remove(clientUuid);
                iSession.ClearAuthTimer();
                iSession.AuthPending = false;
                iSession.Session.Authorize(principal);

                SendFrameDirect(iSession, new Frame(FrameType.AuthOk, 0, DataType.Null, null));

                _sessions[clientUuid] = iSession;
                ActivateSession(iSession, principal);
            }
        }

        public void Reject(string clientUuid, string? reason = null)
        {
            lock (_lock)
            {
                if (!_tmpSessions.TryGetValue(clientUuid, out var iSession)) return;

                _tmpSessions.Remove(clientUuid);
                iSession.ClearAuthTimer();
                SendFrameDirect(iSession, new Frame(FrameType.AuthFail, 0, DataType.String, reason ?? "Authorization rejected"));

                var ws = iSession.Ws;
                if (ws != null)
                {
                    // Delay close so frame can flush
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(50);
                        try
                        {
                            if (ws.State == WebSocketState.Open)
                                ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Rejected", CancellationToken.None).ConfigureAwait(false);
                        }
                        catch { /* ignore */ }
                    });
                }
            }
        }

        public DanWebSocketSession? GetSession(string uuid)
        {
            lock (_lock)
            {
                return _sessions.TryGetValue(uuid, out var s) ? s.Session : null;
            }
        }

        // Event registration

        public Action OnConnection(Action<DanWebSocketSession> cb)
        {
            lock (_lock) { _onConnection.Add(cb); }
            return () => { lock (_lock) { _onConnection.Remove(cb); } };
        }

        public Action OnAuthorize(Action<string, string> cb)
        {
            lock (_lock) { _onAuthorize.Add(cb); }
            return () => { lock (_lock) { _onAuthorize.Remove(cb); } };
        }

        public Action OnSessionExpired(Action<DanWebSocketSession> cb)
        {
            lock (_lock) { _onSessionExpired.Add(cb); }
            return () => { lock (_lock) { _onSessionExpired.Remove(cb); } };
        }

        // ---- Lifecycle ----

        public void Close()
        {
            lock (_lock)
            {
                _cts?.Cancel();

                foreach (var iSession in _sessions.Values)
                {
                    iSession.Session.DisposeAllTopicHandles();
                    iSession.Session.HandleDisconnect();
                    iSession.Heartbeat.Stop();
                    iSession.BulkQueue.Dispose();
                    iSession.TtlTimer?.Dispose();
                    CloseWs(iSession.Ws);
                }
                foreach (var iSession in _tmpSessions.Values)
                {
                    iSession.ClearAuthTimer();
                    CloseWs(iSession.Ws);
                }
                _sessions.Clear();
                _tmpSessions.Clear();
                _principalIndex.Clear();

                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            Close();
        }

        // ---- Internal: Accept loop ----

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;

                    if (context.Request.IsWebSocketRequest)
                    {
                        _ = HandleConnectionAsync(context, ct);
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch { /* continue */ }
            }
        }

        private async Task HandleConnectionAsync(HttpListenerContext context, CancellationToken ct)
        {
            WebSocket ws;
            try
            {
                var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                ws = wsContext.WebSocket;
            }
            catch { return; }

            var parser = new StreamParser();
            bool identified = false;
            string clientUuid = "";

            parser.OnHeartbeat += () =>
            {
                lock (_lock)
                {
                    InternalSession? iSession = null;
                    if (!_sessions.TryGetValue(clientUuid, out iSession))
                        _tmpSessions.TryGetValue(clientUuid, out iSession);
                    iSession?.Heartbeat.Received();
                }
            };

            parser.OnFrame += (frame) =>
            {
                lock (_lock)
                {
                    Interlocked.Increment(ref _framesIn);

                    if (_maxFramesPerSec > 0 && clientUuid.Length > 0)
                    {
                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (!_frameCounters.TryGetValue(clientUuid, out var rc))
                        {
                            rc = (0, now);
                        }
                        if (now - rc.windowStart >= 1000)
                        {
                            rc = (0, now);
                        }
                        rc = (rc.count + 1, rc.windowStart);
                        _frameCounters[clientUuid] = rc;
                        if (rc.count > _maxFramesPerSec)
                        {
                            CloseWs(ws);
                            return;
                        }
                    }

                    if (!identified)
                    {
                        if (frame.FrameType != FrameType.Identify) { CloseWs(ws); return; }
                        var payload = frame.Payload;
                        if (payload is byte[] bytes && (bytes.Length == 16 || bytes.Length == 18))
                        {
                            if (bytes.Length == 18)
                            {
                                byte clientMajor = bytes[16];
                                if (clientMajor != ProtocolMajor) { CloseWs(ws); return; }
                            }
                            clientUuid = BytesToUuid(bytes, 0, 16);
                        }
                        else { CloseWs(ws); return; }

                        identified = true;
                        HandleIdentified(ws, clientUuid);
                        return;
                    }

                    // Auth frame
                    if (frame.FrameType == FrameType.Auth)
                    {
                        if (_tmpSessions.TryGetValue(clientUuid, out var tmpSession) && _authEnabled)
                        {
                            string tokenStr = frame.Payload?.ToString() ?? "";
                            foreach (var cb in _onAuthorize)
                            {
                                try { cb(clientUuid, tokenStr); } catch { }
                            }
                        }
                        return;
                    }

                    // Client topic frames
                    if (IsTopicMode && _sessions.TryGetValue(clientUuid, out var topicSession))
                    {
                        if (frame.FrameType == FrameType.ClientReset ||
                            frame.FrameType == FrameType.ClientKeyRegistration ||
                            frame.FrameType == FrameType.ClientValue ||
                            frame.FrameType == FrameType.ClientSync)
                        {
                            HandleClientTopicFrame(topicSession, frame);
                            return;
                        }
                    }

                    // Route to session handler
                    if (_sessions.TryGetValue(clientUuid, out var session))
                    {
                        session.Session.HandleFrame(frame);
                    }
                }
            };

            parser.OnError += (_) => { /* ignore parse errors */ };

            // Receive loop
            var buffer = new byte[8192];
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        var chunk = new byte[result.Count];
                        Array.Copy(buffer, chunk, result.Count);
                        parser.Feed(chunk);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch { }

            if (clientUuid.Length > 0) HandleSessionDisconnect(clientUuid);
        }

        // ---- Internal: Identified ----

        private void HandleIdentified(WebSocket ws, string clientUuid)
        {
            int total = _sessions.Count + _tmpSessions.Count;
            if (_maxConnections > 0 && !_sessions.ContainsKey(clientUuid) && total >= _maxConnections)
            {
                CloseWs(ws);
                return;
            }

            if (_sessions.TryGetValue(clientUuid, out var existing))
            {
                // Reconnect
                CloseWs(existing.Ws);
                existing.TtlTimer?.Dispose();
                existing.TtlTimer = null;

                existing.Ws = ws;
                existing.Session.HandleReconnect();
                existing.Heartbeat.Start();
                SetupBulkFlush(existing);

                if (_authEnabled)
                {
                    _tmpSessions[clientUuid] = existing;
                    _sessions.Remove(clientUuid);
                    StartAuthTimeout(existing, clientUuid, ws);
                }
                else
                {
                    string principal = existing.Session.Principal ?? BroadcastPrincipal;
                    existing.Session.Authorize(principal);
                    ActivateSession(existing, principal);
                }
                return;
            }

            var session = new DanWebSocketSession(clientUuid);
            var bulkQueue = new BulkQueue(_flushIntervalMs, emitFlushEnd: true);
            var heartbeat = new HeartbeatManager();

            var iSession = new InternalSession(session, ws, bulkQueue, heartbeat);

            session.SetEnqueue(f => { lock (_lock) { bulkQueue.Enqueue(f); } });
            SetupBulkFlush(iSession);
            heartbeat.OnSend += data => SendRawToWs(iSession, data);
            heartbeat.OnTimeout += () => HandleSessionDisconnect(clientUuid);
            heartbeat.Start();

            if (_authEnabled)
            {
                _tmpSessions[clientUuid] = iSession;
                StartAuthTimeout(iSession, clientUuid, ws);
            }
            else
            {
                string defaultPrincipal = Mode == Mode.Broadcast ? BroadcastPrincipal : "default";
                session.Authorize(defaultPrincipal);
                _sessions[clientUuid] = iSession;
                ActivateSession(iSession, defaultPrincipal);
            }
        }

        private void SetupBulkFlush(InternalSession iSession)
        {
            iSession.BulkQueue.OnFlush += data =>
            {
                // Count frames in the batch (approximate by counting DLE STX pairs)
                Interlocked.Increment(ref _framesOut);
                SendRawToWs(iSession, data);
            };
        }

        private void StartAuthTimeout(InternalSession iSession, string clientUuid, WebSocket ws)
        {
            iSession.AuthPending = true;
            long timeout = _authTimeout;
            iSession.AuthTimer = new Timer(_ =>
            {
                lock (_lock)
                {
                    if (_tmpSessions.Remove(clientUuid))
                    {
                        CloseWs(ws);
                    }
                }
            }, null, timeout, Timeout.Infinite);
        }

        // ---- Internal: Activate session ----

        private void ActivateSession(InternalSession iSession, string principal)
        {
            if (IsTopicMode)
            {
                iSession.Session.BindSessionTX(f => { lock (_lock) { iSession.BulkQueue.Enqueue(f); } });
                foreach (var cb in _onConnection)
                {
                    try { cb(iSession.Session); } catch { }
                }
                iSession.BulkQueue.Enqueue(new Frame(FrameType.ServerSync, 0, DataType.Null, null));
            }
            else
            {
                string effectivePrincipal = Mode == Mode.Broadcast ? BroadcastPrincipal : principal;
                var ptx = _principals.Principal(effectivePrincipal);
                _principals.AddSession(effectivePrincipal);
                IndexAddSession(effectivePrincipal, iSession);

                iSession.Session.SetTxProviders(
                    () => ptx.BuildKeyFrames(),
                    () => ptx.BuildValueFrames()
                );

                foreach (var cb in _onConnection)
                {
                    try { cb(iSession.Session); } catch { }
                }
                iSession.Session.StartSync();
            }
        }

        // ---- Internal: PrincipalTX binding ----

        private void BindPrincipalTX(PrincipalTX ptx)
        {
            ptx.OnValue(frame =>
            {
                foreach (var iSession in GetSessionsForPrincipal(ptx.Name))
                {
                    if (iSession.Session.State == SessionState.Ready && iSession.Ws != null && iSession.Ws.State == WebSocketState.Open)
                    {
                        iSession.BulkQueue.Enqueue(frame);
                    }
                }
            });

            ptx.OnIncremental((keyFrame, syncFrame, valueFrame) =>
            {
                foreach (var iSession in GetSessionsForPrincipal(ptx.Name))
                {
                    if (iSession.Session.State == SessionState.Ready && iSession.Ws != null && iSession.Ws.State == WebSocketState.Open)
                    {
                        iSession.BulkQueue.Enqueue(keyFrame);
                        iSession.BulkQueue.Enqueue(syncFrame);
                        iSession.BulkQueue.Enqueue(valueFrame);
                    }
                }
            });

            ptx.OnResync(() =>
            {
                var keyFrames = ptx.BuildKeyFrames();
                foreach (var iSession in GetSessionsForPrincipal(ptx.Name))
                {
                    if (iSession.Session.Connected && iSession.Ws != null && iSession.Ws.State == WebSocketState.Open)
                    {
                        iSession.BulkQueue.Enqueue(new Frame(FrameType.ServerReset, 0, DataType.Null, null));
                        foreach (var f in keyFrames) iSession.BulkQueue.Enqueue(f);
                    }
                }
            });
        }

        // ---- Internal: Client topic frame handling ----

        private void HandleClientTopicFrame(InternalSession iSession, Frame frame)
        {
            switch (frame.FrameType)
            {
                case FrameType.ClientReset:
                    if (iSession.ClientRegistry != null) iSession.ClientRegistry.Clear();
                    else iSession.ClientRegistry = new KeyRegistry();
                    if (iSession.ClientValues != null) iSession.ClientValues.Clear();
                    else iSession.ClientValues = new Dictionary<uint, object?>();
                    break;

                case FrameType.ClientKeyRegistration:
                    if (iSession.ClientRegistry == null) iSession.ClientRegistry = new KeyRegistry();
                    iSession.ClientRegistry.RegisterOne(frame.KeyId, (string)frame.Payload!, frame.DataType);
                    break;

                case FrameType.ClientValue:
                    if (iSession.ClientValues == null) iSession.ClientValues = new Dictionary<uint, object?>();
                    iSession.ClientValues[frame.KeyId] = frame.Payload;
                    break;

                case FrameType.ClientSync:
                    ProcessTopicSync(iSession);
                    break;
            }
        }

        private void ProcessTopicSync(InternalSession iSession)
        {
            var session = iSession.Session;
            var newTopics = new Dictionary<string, Dictionary<string, object?>>();
            var nameToIndex = new Dictionary<string, int>();

            if (iSession.ClientRegistry != null && iSession.ClientValues != null)
            {
                var indexToName = new Dictionary<string, string>();

                foreach (var path in iSession.ClientRegistry.Paths)
                {
                    var entry = iSession.ClientRegistry.GetByPath(path);
                    if (entry == null) continue;

                    if (path.EndsWith(".name") && path.StartsWith("topic."))
                    {
                        string idxStr = path.Substring(6, path.Length - 6 - 5);
                        iSession.ClientValues.TryGetValue(entry.KeyId, out var topicNameObj);
                        string? topicName = topicNameObj as string;
                        if (topicName != null && topicName.Length <= 128 && !newTopics.ContainsKey(topicName) && newTopics.Count < 100)
                        {
                            indexToName[idxStr] = topicName;
                            if (int.TryParse(idxStr, out int idx))
                                nameToIndex[topicName] = idx;
                            newTopics[topicName] = new Dictionary<string, object?>();
                        }
                    }
                    else
                    {
                        int paramIdx = path.IndexOf(".param.");
                        if (paramIdx != -1 && path.StartsWith("topic."))
                        {
                            string idxStr = path.Substring(6, paramIdx - 6);
                            string paramKey = path.Substring(paramIdx + 7);
                            if (indexToName.TryGetValue(idxStr, out var tn) && newTopics.TryGetValue(tn, out var parms))
                            {
                                iSession.ClientValues.TryGetValue(entry.KeyId, out var val);
                                parms[paramKey] = val;
                            }
                        }
                    }
                }
            }

            // Diff: unsubscribed
            var oldTopics = new HashSet<string>(session.Topics);
            foreach (var oldName in oldTopics)
            {
                if (!newTopics.ContainsKey(oldName))
                {
                    var handle = session.GetTopicHandle(oldName);
                    if (handle != null)
                    {
                        foreach (var cb in Topic._onUnsubscribeCbs)
                        {
                            try { cb(session, handle); } catch { }
                        }
                    }
                    session.RemoveTopicHandle(oldName);
                    session.RemoveTopic(oldName);
                }
            }

            // Diff: new/changed
            foreach (var kvp in newTopics)
            {
                string name = kvp.Key;
                var parms = kvp.Value;
                var existingHandle = session.GetTopicHandle(name);

                if (existingHandle == null)
                {
                    nameToIndex.TryGetValue(name, out int clientIdx);
                    var handle = session.CreateTopicHandle(name, parms, clientIdx);
                    foreach (var cb in Topic._onSubscribeCbs)
                    {
                        try { cb(session, handle); } catch { }
                    }
                }
                else
                {
                    if (!ShallowEqual(existingHandle.Params, parms))
                    {
                        existingHandle.UpdateParams(parms);
                        session.UpdateTopicParams(name, parms);
                    }
                }
            }
        }

        // ---- Internal: Session disconnect ----

        private void HandleSessionDisconnect(string uuid)
        {
            lock (_lock)
            {
                _frameCounters.Remove(uuid);

                if (_tmpSessions.TryGetValue(uuid, out var tmp))
                {
                    _tmpSessions.Remove(uuid);
                    tmp.ClearAuthTimer();
                    tmp.Heartbeat.Stop();
                    return;
                }

                if (!_sessions.TryGetValue(uuid, out var iSession)) return;
                if (!iSession.Session.Connected) return;

                iSession.Session.DisposeAllTopicHandles();
                iSession.Session.HandleDisconnect();
                iSession.Heartbeat.Stop();
                iSession.BulkQueue.Clear();
                iSession.Ws = null;

                long ttl = _ttl;
                iSession.TtlTimer = new Timer(_ =>
                {
                    lock (_lock)
                    {
                        _sessions.Remove(uuid);
                        string? principal = iSession.Session.Principal;
                        if (principal != null && !IsTopicMode)
                        {
                            string effectivePrincipal = Mode == Mode.Broadcast ? BroadcastPrincipal : principal;
                            _principals.RemoveSession(effectivePrincipal);
                            IndexRemoveSession(effectivePrincipal, iSession);
                        }
                        foreach (var cb in _onSessionExpired)
                        {
                            try { cb(iSession.Session); } catch { }
                        }
                    }
                }, null, ttl, Timeout.Infinite);
            }
        }

        // ---- Internal: Principal index ----

        private void IndexAddSession(string principal, InternalSession iSession)
        {
            if (!_principalIndex.TryGetValue(principal, out var set))
            {
                set = new HashSet<InternalSession>();
                _principalIndex[principal] = set;
            }
            set.Add(iSession);
        }

        private void IndexRemoveSession(string principal, InternalSession iSession)
        {
            if (_principalIndex.TryGetValue(principal, out var set))
            {
                set.Remove(iSession);
                if (set.Count == 0) _principalIndex.Remove(principal);
            }
        }

        private IEnumerable<InternalSession> GetSessionsForPrincipal(string principalName)
        {
            if (_principalIndex.TryGetValue(principalName, out var set))
                return set;
            return Array.Empty<InternalSession>();
        }

        // ---- Internal: Send helpers ----

        private void SendFrameDirect(InternalSession iSession, Frame frame)
        {
            if (iSession.Ws != null && iSession.Ws.State == WebSocketState.Open)
            {
                Interlocked.Increment(ref _framesOut);
                var data = Codec.Encode(frame);
                try
                {
                    iSession.Ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { /* ignore */ }
            }
        }

        private void SendRawToWs(InternalSession iSession, byte[] data)
        {
            var ws = iSession.Ws;
            if (ws != null && ws.State == WebSocketState.Open)
            {
                try
                {
                    ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { /* ignore */ }
            }
        }

        private static void CloseWs(WebSocket? ws)
        {
            if (ws == null) return;
            try
            {
                if (ws.State == WebSocketState.Open)
                    ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        // ---- Helpers ----

        private void AssertMode(Mode expected, string method)
        {
            if (Mode != expected)
                throw new DanWSException("INVALID_MODE", $"server.{method}() is only available in {expected} mode.");
        }

        private static string BytesToUuid(byte[] bytes, int offset, int length)
        {
            var hex = new StringBuilder(36);
            for (int i = offset; i < offset + length; i++)
            {
                if (i == offset + 4 || i == offset + 6 || i == offset + 8 || i == offset + 10)
                    hex.Append('-');
                hex.AppendFormat("{0:x2}", bytes[i]);
            }
            return hex.ToString();
        }

        private static bool ShallowEqual(Dictionary<string, object?> a, Dictionary<string, object?> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kvp in a)
            {
                if (!b.TryGetValue(kvp.Key, out var bVal)) return false;
                if (!Equals(kvp.Value, bVal)) return false;
            }
            return true;
        }
    }
}
