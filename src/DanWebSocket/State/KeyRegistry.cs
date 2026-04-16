using System.Collections.Generic;
using System.Text.RegularExpressions;
using DanWebSocket.Protocol;

namespace DanWebSocket.State
{
    /// <summary>
    /// Maps keyId to path and vice versa.
    /// </summary>
    public class KeyRegistry
    {
        private static readonly Regex KeyPathRegex = new Regex(@"^[a-zA-Z0-9_]+(\.[a-zA-Z0-9_]+)*$", RegexOptions.Compiled);
        private const int MaxKeyPathBytes = 200;
        private const int DefaultMaxKeys = 10_000;

        private readonly Dictionary<uint, KeyEntry> _byId = new Dictionary<uint, KeyEntry>();
        private readonly Dictionary<string, KeyEntry> _byPath = new Dictionary<string, KeyEntry>();
        private uint _nextId = 1;
        private List<string>? _cachedPaths;
        private readonly int _maxKeys;

        public KeyRegistry(int maxKeys = DefaultMaxKeys)
        {
            _maxKeys = maxKeys;
        }

        /// <summary>
        /// Register a single key with a specific keyId (used for receiving remote registrations).
        /// </summary>
        public void RegisterOne(uint keyId, string path, DataType type)
        {
            ValidateKeyPath(path);
            if (!_byPath.ContainsKey(path) && _byId.Count >= _maxKeys)
                throw new DanWSException("KEY_LIMIT_EXCEEDED", $"Key registry limit reached ({_maxKeys}).");

            var entry = new KeyEntry(path, type, keyId);
            _byId[keyId] = entry;
            _byPath[path] = entry;
            if (keyId >= _nextId)
                _nextId = keyId + 1;
            _cachedPaths = null;
        }

        public KeyEntry? GetByKeyId(uint keyId)
        {
            _byId.TryGetValue(keyId, out var entry);
            return entry;
        }

        public KeyEntry? GetByPath(string path)
        {
            _byPath.TryGetValue(path, out var entry);
            return entry;
        }

        public bool HasKeyId(uint keyId) => _byId.ContainsKey(keyId);

        public bool HasPath(string path) => _byPath.ContainsKey(path);

        public bool RemoveByKeyId(uint keyId)
        {
            if (!_byId.TryGetValue(keyId, out var entry)) return false;
            _byId.Remove(keyId);
            _byPath.Remove(entry.Path);
            _cachedPaths = null;
            return true;
        }

        public int Size => _byId.Count;

        public List<string> Paths
        {
            get
            {
                if (_cachedPaths == null)
                    _cachedPaths = new List<string>(_byPath.Keys);
                return _cachedPaths;
            }
        }

        public void Clear()
        {
            _byId.Clear();
            _byPath.Clear();
            _nextId = 1;
            _cachedPaths = null;
        }

        private static void ValidateKeyPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new DanWSException("INVALID_KEY_PATH", "Key path must not be empty");
            if (!KeyPathRegex.IsMatch(path))
                throw new DanWSException("INVALID_KEY_PATH", $"Invalid key path: \"{path}\"");
            if (System.Text.Encoding.UTF8.GetByteCount(path) > MaxKeyPathBytes)
                throw new DanWSException("INVALID_KEY_PATH", $"Key path exceeds 200 bytes: \"{path}\"");
        }
    }

    public class KeyEntry
    {
        public string Path { get; }
        public DataType Type { get; }
        public uint KeyId { get; }

        public KeyEntry(string path, DataType type, uint keyId)
        {
            Path = path;
            Type = type;
            KeyId = keyId;
        }
    }
}
