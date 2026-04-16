using System;
using System.Collections.Generic;
using DanWebSocket.State;

namespace DanWebSocket.Api
{
    /// <summary>
    /// Handle for a subscribed topic. Provides scoped data access.
    /// </summary>
    public class TopicClientHandle
    {
        public string Name { get; }
        private int _index;
        private readonly KeyRegistry _registry;
        private readonly Func<uint, object?> _storeGet;

        private readonly List<Action<string, object?>> _onReceive = new List<Action<string, object?>>();
        private readonly List<Action> _onUpdate = new List<Action>();
        private bool _dirty;

        internal TopicClientHandle(string name, int index, KeyRegistry registry, Func<uint, object?> storeGet)
        {
            Name = name;
            _index = index;
            _registry = registry;
            _storeGet = storeGet;
        }

        public object? Get(string key)
        {
            string wirePath = $"t.{_index}.{key}";
            var entry = _registry.GetByPath(wirePath);
            if (entry == null) return null;
            return _storeGet(entry.KeyId);
        }

        public List<string> Keys
        {
            get
            {
                string prefix = $"t.{_index}.";
                var result = new List<string>();
                foreach (var path in _registry.Paths)
                {
                    if (path.StartsWith(prefix))
                        result.Add(path.Substring(prefix.Length));
                }
                return result;
            }
        }

        public Action OnReceive(Action<string, object?> cb)
        {
            _onReceive.Add(cb);
            return () => _onReceive.Remove(cb);
        }

        public Action OnUpdate(Action cb)
        {
            _onUpdate.Add(cb);
            return () => _onUpdate.Remove(cb);
        }

        internal void Notify(string userKey, object? value)
        {
            foreach (var cb in _onReceive)
            {
                try { cb(userKey, value); } catch { /* ignore */ }
            }
            _dirty = true;
        }

        internal void FlushUpdate()
        {
            if (!_dirty || _onUpdate.Count == 0) return;
            _dirty = false;
            foreach (var cb in _onUpdate)
            {
                try { cb(); } catch { /* ignore */ }
            }
        }

        internal void SetIndex(int index) { _index = index; }
        internal int Index => _index;
    }
}
