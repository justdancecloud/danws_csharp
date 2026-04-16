using System;
using System.Collections.Generic;
using DanWebSocket.State;

namespace DanWebSocket.Api
{
    /// <summary>
    /// Read-only view of an array stored in flattened state.
    /// </summary>
    public class ArrayView
    {
        private readonly string _prefix;
        private readonly KeyRegistry _registry;
        private readonly Func<uint, object?> _storeGet;

        public ArrayView(string prefix, KeyRegistry registry, Func<uint, object?> storeGet)
        {
            _prefix = prefix;
            _registry = registry;
            _storeGet = storeGet;
        }

        /// <summary>
        /// Get the length of the array.
        /// </summary>
        public int Length
        {
            get
            {
                var entry = _registry.GetByPath($"{_prefix}.length");
                if (entry == null) return 0;
                var val = _storeGet(entry.KeyId);
                if (val is int i) return i;
                if (val is long l) return (int)l;
                return 0;
            }
        }

        /// <summary>
        /// Get element at the specified index.
        /// </summary>
        public object? this[int index]
        {
            get
            {
                var entry = _registry.GetByPath($"{_prefix}.{index}");
                if (entry == null) return null;
                return _storeGet(entry.KeyId);
            }
        }

        /// <summary>
        /// Convert to a list of values.
        /// </summary>
        public List<object?> ToList()
        {
            int len = Length;
            var result = new List<object?>(len);
            for (int i = 0; i < len; i++)
                result.Add(this[i]);
            return result;
        }
    }
}
