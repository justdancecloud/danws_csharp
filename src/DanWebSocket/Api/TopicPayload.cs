using System;
using System.Collections.Generic;
using DanWebSocket.Protocol;

namespace DanWebSocket.Api
{
    /// <summary>
    /// Topic data store. Per-topic flat state with wire prefix t.{index}.
    /// </summary>
    public class TopicPayload
    {
        private readonly int _index;
        private readonly Func<uint> _allocateKeyId;
        private FlatStateManager _flatState;

        internal TopicPayload(int index, Func<uint> allocateKeyId)
        {
            _index = index;
            _allocateKeyId = allocateKeyId;
            _flatState = new FlatStateManager(
                allocateKeyId: allocateKeyId,
                enqueue: (_) => { },
                onResync: () => { },
                wirePrefix: $"t.{index}."
            );
        }

        internal void Bind(Action<Frame> enqueue, Action onResync)
        {
            _flatState = new FlatStateManager(
                allocateKeyId: _allocateKeyId,
                enqueue: enqueue,
                onResync: onResync,
                wirePrefix: $"t.{_index}."
            );
        }

        public void Set(string key, object? value) => _flatState.Set(key, value);
        public object? Get(string key) => _flatState.Get(key);
        public List<string> Keys => _flatState.Keys;

        public void Clear(string? key = null)
        {
            if (key != null)
                _flatState.Clear(key);
            else
                _flatState.Clear();
        }

        internal List<Frame> BuildKeyFrames() => _flatState.BuildKeyFrames();
        internal List<Frame> BuildValueFrames() => _flatState.BuildValueFrames();
        internal int Size => _flatState.Size;
        internal int Index => _index;
    }
}
