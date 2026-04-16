using System;
using System.Collections.Generic;
using System.Threading;

namespace DanWebSocket.Api
{
    public enum TopicEventType
    {
        Subscribe,
        ChangedParams,
        DelayedTask,
    }

    /// <summary>
    /// Server-side topic handle with payload, callback, and delayed task support.
    /// </summary>
    public class TopicHandle
    {
        public string Name { get; }
        public TopicPayload Payload { get; }

        private Dictionary<string, object?> _params;
        private Action<TopicEventType, TopicHandle, DanWebSocketSession>? _callback;
        private readonly DanWebSocketSession _session;
        private Timer? _timer;
        private int? _delayMs;

        internal TopicHandle(string name, Dictionary<string, object?> parms, TopicPayload payload, DanWebSocketSession session)
        {
            Name = name;
            _params = parms;
            Payload = payload;
            _session = session;
        }

        public Dictionary<string, object?> Params => _params;

        public void SetCallback(Action<TopicEventType, TopicHandle, DanWebSocketSession> fn)
        {
            _callback = fn;
            try { fn(TopicEventType.Subscribe, this, _session); }
            catch { /* ignore */ }
        }

        public void SetDelayedTask(int ms)
        {
            ClearDelayedTask();
            _delayMs = ms;
            _timer = new Timer(_ =>
            {
                try { _callback?.Invoke(TopicEventType.DelayedTask, this, _session); }
                catch { /* ignore */ }
            }, null, ms, ms);
        }

        public void ClearDelayedTask()
        {
            _timer?.Dispose();
            _timer = null;
        }

        internal void UpdateParams(Dictionary<string, object?> newParams)
        {
            _params = newParams;
            bool hadTask = _timer != null;
            int? savedMs = _delayMs;

            ClearDelayedTask();

            try { _callback?.Invoke(TopicEventType.ChangedParams, this, _session); }
            catch { /* ignore */ }

            if (hadTask && savedMs.HasValue)
            {
                SetDelayedTask(savedMs.Value);
            }
        }

        internal void Dispose()
        {
            ClearDelayedTask();
            _callback = null;
            _delayMs = null;
        }
    }
}
