using System;
using System.Collections.Generic;
using DanWebSocket.Protocol;

namespace DanWebSocket.Api
{
    internal class FlatEntry
    {
        public uint KeyId { get; set; }
        public DataType Type { get; set; }
        public object? Value { get; set; }

        public FlatEntry(uint keyId, DataType type, object? value)
        {
            KeyId = keyId;
            Type = type;
            Value = value;
        }
    }

    /// <summary>
    /// Server-side flat state manager. Manages key-value entries and emits frames.
    /// Ported from TS FlatStateManager.
    /// </summary>
    internal class FlatStateManager
    {
        private readonly Dictionary<string, FlatEntry> _entries = new Dictionary<string, FlatEntry>();
        private readonly Dictionary<uint, (string key, FlatEntry entry)> _byKeyId = new Dictionary<uint, (string, FlatEntry)>();
        private readonly Func<uint> _allocateKeyId;
        private readonly Action<Frame> _enqueue;
        private readonly Action _onResync;
        private readonly Action<Frame, Frame, Frame>? _onIncrementalKey;
        private readonly Action? _onKeyStructureChange;
        private readonly string _wirePrefix;

        public FlatStateManager(
            Func<uint> allocateKeyId,
            Action<Frame> enqueue,
            Action onResync,
            string wirePrefix = "",
            Action<Frame, Frame, Frame>? onIncrementalKey = null,
            Action? onKeyStructureChange = null)
        {
            _allocateKeyId = allocateKeyId;
            _enqueue = enqueue;
            _onResync = onResync;
            _wirePrefix = wirePrefix;
            _onIncrementalKey = onIncrementalKey;
            _onKeyStructureChange = onKeyStructureChange;
        }

        public void Set(string key, object? value)
        {
            SetLeaf(key, value);
        }

        public object? Get(string key)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.Value : null;
        }

        public List<string> Keys
        {
            get { return new List<string>(_entries.Keys); }
        }

        public int Size => _entries.Count;

        public void Clear(string? key = null)
        {
            if (key != null)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    _enqueue(new Frame(FrameType.ServerKeyDelete, entry.KeyId, DataType.Null, null));
                    _byKeyId.Remove(entry.KeyId);
                    _entries.Remove(key);
                    _onKeyStructureChange?.Invoke();
                }
            }
            else
            {
                if (_entries.Count > 0)
                {
                    _entries.Clear();
                    _byKeyId.Clear();
                    _onKeyStructureChange?.Invoke();
                    _onResync();
                }
            }
        }

        public List<Frame> BuildKeyFrames()
        {
            var frames = new List<Frame>();
            foreach (var kvp in _entries)
            {
                string wirePath = _wirePrefix.Length > 0 ? _wirePrefix + kvp.Key : kvp.Key;
                frames.Add(new Frame(FrameType.ServerKeyRegistration, kvp.Value.KeyId, kvp.Value.Type, wirePath));
            }
            return frames;
        }

        public List<Frame> BuildValueFrames()
        {
            var frames = new List<Frame>();
            foreach (var entry in _entries.Values)
            {
                if (entry.Value != null)
                {
                    frames.Add(new Frame(FrameType.ServerValue, entry.KeyId, entry.Type, entry.Value));
                }
            }
            return frames;
        }

        public (List<Frame> keyFrames, List<Frame> valueFrames) BuildAllFrames()
        {
            var keyFrames = new List<Frame>();
            var valueFrames = new List<Frame>();
            foreach (var kvp in _entries)
            {
                string wirePath = _wirePrefix.Length > 0 ? _wirePrefix + kvp.Key : kvp.Key;
                keyFrames.Add(new Frame(FrameType.ServerKeyRegistration, kvp.Value.KeyId, kvp.Value.Type, wirePath));
                if (kvp.Value.Value != null)
                {
                    valueFrames.Add(new Frame(FrameType.ServerValue, kvp.Value.KeyId, kvp.Value.Type, kvp.Value.Value));
                }
            }
            return (keyFrames, valueFrames);
        }

        public (string key, FlatEntry entry)? GetByKeyId(uint keyId)
        {
            return _byKeyId.TryGetValue(keyId, out var result) ? result : ((string, FlatEntry)?)null;
        }

        private void SetLeaf(string key, object? value)
        {
            DataType newType = DetectDataType(value);

            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing.Type != newType)
                {
                    // Type changed - delete old, register new
                    _enqueue(new Frame(FrameType.ServerKeyDelete, existing.KeyId, DataType.Null, null));
                    _byKeyId.Remove(existing.KeyId);
                    _entries.Remove(key);
                    _onKeyStructureChange?.Invoke();

                    uint newKeyId = _allocateKeyId();
                    var newEntry = new FlatEntry(newKeyId, newType, value);
                    _entries[key] = newEntry;
                    _byKeyId[newKeyId] = (key, newEntry);

                    string wirePath = _wirePrefix.Length > 0 ? _wirePrefix + key : key;
                    _enqueue(new Frame(FrameType.ServerKeyRegistration, newKeyId, newType, wirePath));
                    _enqueue(new Frame(FrameType.ServerSync, 0, DataType.Null, null));
                    _enqueue(new Frame(FrameType.ServerValue, newKeyId, newType, value));
                    return;
                }

                existing.Value = value;
                _enqueue(new Frame(FrameType.ServerValue, existing.KeyId, existing.Type, value));
                return;
            }

            // New key
            uint keyId = _allocateKeyId();
            var entry = new FlatEntry(keyId, newType, value);
            _entries[key] = entry;
            _byKeyId[keyId] = (key, entry);
            _onKeyStructureChange?.Invoke();

            string wp = _wirePrefix.Length > 0 ? _wirePrefix + key : key;
            var keyFrame = new Frame(FrameType.ServerKeyRegistration, keyId, newType, wp);
            var syncFrame = new Frame(FrameType.ServerSync, 0, DataType.Null, null);
            var valueFrame = new Frame(FrameType.ServerValue, keyId, newType, value);

            if (_onIncrementalKey != null)
            {
                _onIncrementalKey(keyFrame, syncFrame, valueFrame);
            }
            else
            {
                _enqueue(keyFrame);
                _enqueue(syncFrame);
                _enqueue(valueFrame);
            }
        }

        internal static DataType DetectDataType(object? value)
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
            return DataType.String;
        }
    }
}
