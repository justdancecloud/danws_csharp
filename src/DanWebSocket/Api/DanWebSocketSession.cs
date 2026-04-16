using System;
using System.Collections.Generic;
using DanWebSocket.Protocol;

namespace DanWebSocket.Api
{
    public enum SessionState
    {
        Pending,
        Authorized,
        Synchronizing,
        Ready,
        Disconnected,
    }

    /// <summary>
    /// Per-connection session on the server side.
    /// </summary>
    public class DanWebSocketSession
    {
        public string Id { get; }
        private string? _principal;
        private bool _authorized;
        private bool _connected = true;
        private SessionState _state = SessionState.Pending;

        private Action<Frame>? _enqueueFrame;

        // TX providers (broadcast/principal modes)
        private Func<List<Frame>>? _txKeyFrameProvider;
        private Func<List<Frame>>? _txValueFrameProvider;
        private Dictionary<uint, Frame>? _txKeyFrameIndex;
        private Dictionary<uint, Frame>? _txValueFrameIndex;
        private bool _serverSyncSent;

        // Session-level flat TX store (topic modes)
        private uint _nextKeyId = 1;
        private Action<Frame>? _sessionEnqueue;
        private bool _sessionBound;
        private FlatStateManager? _flatState;

        // Topic handles
        private readonly Dictionary<string, TopicHandle> _topicHandles = new Dictionary<string, TopicHandle>();
        private int _topicIndex;
        private readonly Dictionary<string, TopicInfo> _topics = new Dictionary<string, TopicInfo>();

        // Events
        private readonly List<Action> _onReady = new List<Action>();
        private readonly List<Action> _onDisconnect = new List<Action>();

        public DanWebSocketSession(string clientUuid)
        {
            Id = clientUuid;
        }

        public string? Principal => _principal;
        public bool Authorized => _authorized;
        public bool Connected => _connected;
        public SessionState State => _state;

        // Event registration
        public Action OnReady(Action cb) { _onReady.Add(cb); return () => _onReady.Remove(cb); }
        public Action OnDisconnect(Action cb) { _onDisconnect.Add(cb); return () => _onDisconnect.Remove(cb); }

        // Session-level data API (topic modes)
        public void Set(string key, object? value)
        {
            if (!_sessionBound || _flatState == null)
                throw new DanWSException("INVALID_MODE", "session.Set() is only available in topic modes.");
            _flatState.Set(key, value);
        }

        public object? Get(string key)
        {
            return _flatState?.Get(key);
        }

        public List<string> Keys => _flatState?.Keys ?? new List<string>();

        // Topic API
        public List<string> Topics => new List<string>(_topics.Keys);

        public TopicHandle? GetTopicHandle(string name)
        {
            _topicHandles.TryGetValue(name, out var handle);
            return handle;
        }

        public Dictionary<string, TopicHandle> TopicHandles => _topicHandles;

        // ---- Internal methods ----

        internal void SetEnqueue(Action<Frame> fn) { _enqueueFrame = fn; }

        internal void SetTxProviders(Func<List<Frame>> keyFrames, Func<List<Frame>> valueFrames)
        {
            _txKeyFrameProvider = keyFrames;
            _txValueFrameProvider = valueFrames;
            _txKeyFrameIndex = null;
            _txValueFrameIndex = null;
        }

        internal void BindSessionTX(Action<Frame> enqueue)
        {
            _sessionEnqueue = enqueue;
            _sessionBound = true;
            _flatState = new FlatStateManager(
                allocateKeyId: () => _nextKeyId++,
                enqueue: enqueue,
                onResync: () => TriggerSessionResync(),
                wirePrefix: ""
            );
        }

        internal void Authorize(string principal)
        {
            _principal = principal;
            _authorized = true;
            _state = SessionState.Authorized;
        }

        internal void StartSync()
        {
            _state = SessionState.Synchronizing;
            _serverSyncSent = false;

            if (_txKeyFrameProvider != null && _enqueueFrame != null)
            {
                var frames = _txKeyFrameProvider();
                if (frames.Count > 0)
                {
                    foreach (var f in frames) _enqueueFrame(f);
                    _serverSyncSent = true;
                }
                else
                {
                    _enqueueFrame(new Frame(FrameType.ServerSync, 0, DataType.Null, null));
                    _serverSyncSent = true;
                }
            }
            else
            {
                _state = SessionState.Ready;
                EmitCallbacks(_onReady);
            }
        }

        internal void HandleFrame(Frame frame)
        {
            switch (frame.FrameType)
            {
                case FrameType.ClientReady:
                    if (_state == SessionState.Ready) return;
                    if (_txValueFrameProvider != null && _enqueueFrame != null)
                    {
                        foreach (var vf in _txValueFrameProvider()) _enqueueFrame(vf);
                    }
                    if (_serverSyncSent)
                    {
                        _state = SessionState.Ready;
                        EmitCallbacks(_onReady);
                    }
                    break;

                case FrameType.ClientResyncReq:
                    if (_txKeyFrameProvider != null && _enqueueFrame != null)
                    {
                        _txKeyFrameIndex = null;
                        _txValueFrameIndex = null;
                        _enqueueFrame(new Frame(FrameType.ServerReset, 0, DataType.Null, null));
                        foreach (var f in _txKeyFrameProvider()) _enqueueFrame(f);
                    }
                    break;

                case FrameType.ClientKeyRequest:
                    HandleKeyRequest(frame.KeyId);
                    break;

                case FrameType.Error:
                    break;
            }
        }

        internal void HandleDisconnect()
        {
            _connected = false;
            _state = SessionState.Disconnected;
            EmitCallbacks(_onDisconnect);
        }

        internal void HandleReconnect()
        {
            _connected = true;
            _state = SessionState.Authorized;
        }

        internal int NextTopicIndex => _topicIndex;

        internal TopicHandle CreateTopicHandle(string name, Dictionary<string, object?> parms, int? wireIndex = null)
        {
            int index = wireIndex ?? _topicIndex++;
            if (index >= _topicIndex) _topicIndex = index + 1;
            var payload = new TopicPayload(index, () => _nextKeyId++);
            if (_sessionEnqueue != null)
            {
                payload.Bind(_sessionEnqueue, () => TriggerSessionResync());
            }
            var handle = new TopicHandle(name, parms, payload, this);
            _topicHandles[name] = handle;
            _topics[name] = new TopicInfo(name, parms);
            return handle;
        }

        internal void RemoveTopicHandle(string name)
        {
            if (_topicHandles.TryGetValue(name, out var handle))
            {
                handle.Dispose();
                _topicHandles.Remove(name);
                _topics.Remove(name);
                TriggerSessionResync();
            }
        }

        internal void DisposeAllTopicHandles()
        {
            foreach (var handle in _topicHandles.Values)
                handle.Dispose();
            _topicHandles.Clear();
        }

        internal void RemoveTopic(string name) { _topics.Remove(name); }

        internal void UpdateTopicParams(string name, Dictionary<string, object?> parms)
        {
            if (_topics.TryGetValue(name, out var t))
                t.Params = parms;
        }

        // ---- Private ----

        private void TriggerSessionResync()
        {
            if (_sessionEnqueue == null) return;

            _txKeyFrameIndex = null;
            _txValueFrameIndex = null;

            _sessionEnqueue(new Frame(FrameType.ServerReset, 0, DataType.Null, null));

            List<Frame>? flatValueFrames = null;
            if (_flatState != null)
            {
                var (keyFrames, valueFrames) = _flatState.BuildAllFrames();
                foreach (var f in keyFrames) _sessionEnqueue(f);
                flatValueFrames = valueFrames;
            }

            foreach (var handle in _topicHandles.Values)
            {
                foreach (var f in handle.Payload.BuildKeyFrames())
                    _sessionEnqueue(f);
            }

            _sessionEnqueue(new Frame(FrameType.ServerSync, 0, DataType.Null, null));

            if (flatValueFrames != null)
            {
                foreach (var f in flatValueFrames) _sessionEnqueue(f);
            }

            foreach (var handle in _topicHandles.Values)
            {
                foreach (var f in handle.Payload.BuildValueFrames())
                    _sessionEnqueue(f);
            }
        }

        private void HandleKeyRequest(uint keyId)
        {
            if (_enqueueFrame == null) return;

            var syncFrame = new Frame(FrameType.ServerSync, 0, DataType.Null, null);

            // Search in TX providers (broadcast/principal mode)
            if (_txKeyFrameProvider != null && _txValueFrameProvider != null)
            {
                if (_txKeyFrameIndex == null)
                {
                    _txKeyFrameIndex = new Dictionary<uint, Frame>();
                    foreach (var f in _txKeyFrameProvider())
                    {
                        if (f.FrameType == FrameType.ServerKeyRegistration)
                            _txKeyFrameIndex[f.KeyId] = f;
                    }
                }
                if (_txKeyFrameIndex.TryGetValue(keyId, out var keyFrame))
                {
                    _enqueueFrame(keyFrame);
                    _enqueueFrame(syncFrame);
                    if (_txValueFrameIndex == null)
                    {
                        _txValueFrameIndex = new Dictionary<uint, Frame>();
                        foreach (var f in _txValueFrameProvider())
                            _txValueFrameIndex[f.KeyId] = f;
                    }
                    if (_txValueFrameIndex.TryGetValue(keyId, out var valueFrame))
                        _enqueueFrame(valueFrame);
                    return;
                }
            }

            // Search in session-level flat state (topic mode)
            if (_flatState != null)
            {
                var found = _flatState.GetByKeyId(keyId);
                if (found.HasValue)
                {
                    string wirePath = found.Value.key;
                    _enqueueFrame(new Frame(FrameType.ServerKeyRegistration, found.Value.entry.KeyId, found.Value.entry.Type, wirePath));
                    _enqueueFrame(syncFrame);
                    if (found.Value.entry.Value != null)
                    {
                        _enqueueFrame(new Frame(FrameType.ServerValue, found.Value.entry.KeyId, found.Value.entry.Type, found.Value.entry.Value));
                    }
                    return;
                }
            }

            // Search in topic payloads
            foreach (var handle in _topicHandles.Values)
            {
                foreach (var f in handle.Payload.BuildKeyFrames())
                {
                    if (f.KeyId == keyId)
                    {
                        _enqueueFrame(f);
                        _enqueueFrame(syncFrame);
                        foreach (var vf in handle.Payload.BuildValueFrames())
                        {
                            if (vf.KeyId == keyId) { _enqueueFrame(vf); break; }
                        }
                        return;
                    }
                }
            }
        }

        private static void EmitCallbacks(List<Action> callbacks)
        {
            foreach (var cb in callbacks)
            {
                try { cb(); } catch { /* ignore */ }
            }
        }
    }

    internal class TopicInfo
    {
        public string Name { get; }
        public Dictionary<string, object?> Params { get; set; }

        public TopicInfo(string name, Dictionary<string, object?> parms)
        {
            Name = name;
            Params = parms;
        }
    }
}
