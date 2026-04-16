using System;
using System.Collections.Generic;
using DanWebSocket.Protocol;

namespace DanWebSocket.Api
{
    /// <summary>
    /// Shared TX state for one principal. All sessions of the same principal share this state.
    /// </summary>
    public class PrincipalTX
    {
        public string Name { get; }
        private uint _nextKeyId = 1;
        private Action<Frame>? _onValueSet;
        private Action? _onKeysChanged;
        private Action<Frame, Frame, Frame>? _onIncrementalKey;
        private List<Frame>? _cachedKeyFrames;
        private readonly FlatStateManager _flatState;

        internal PrincipalTX(string name)
        {
            Name = name;
            _flatState = new FlatStateManager(
                allocateKeyId: () => _nextKeyId++,
                enqueue: (frame) => _onValueSet?.Invoke(frame),
                onResync: () => TriggerResync(),
                wirePrefix: "",
                onIncrementalKey: (kf, sf, vf) =>
                {
                    if (_onIncrementalKey != null)
                        _onIncrementalKey(kf, sf, vf);
                    else
                        TriggerResync();
                },
                onKeyStructureChange: () => _cachedKeyFrames = null
            );
        }

        internal void OnValue(Action<Frame> fn) { _onValueSet = fn; }
        internal void OnResync(Action fn) { _onKeysChanged = fn; }
        internal void OnIncremental(Action<Frame, Frame, Frame> fn) { _onIncrementalKey = fn; }

        public void Set(string key, object? value)
        {
            _flatState.Set(key, value);
        }

        public object? Get(string key)
        {
            return _flatState.Get(key);
        }

        public List<string> Keys => _flatState.Keys;

        public void Clear(string? key = null)
        {
            if (key != null)
            {
                _flatState.Clear(key);
            }
            else
            {
                _flatState.Clear();
                _nextKeyId = 1;
            }
        }

        internal List<Frame> BuildKeyFrames()
        {
            if (_cachedKeyFrames != null) return _cachedKeyFrames;

            var frames = _flatState.BuildKeyFrames();
            frames.Add(new Frame(FrameType.ServerSync, 0, DataType.Null, null));
            _cachedKeyFrames = frames;
            return frames;
        }

        internal List<Frame> BuildValueFrames()
        {
            return _flatState.BuildValueFrames();
        }

        private void TriggerResync()
        {
            _cachedKeyFrames = null;
            _onKeysChanged?.Invoke();
        }
    }
}
