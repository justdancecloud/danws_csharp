using System;
using System.Collections.Generic;

namespace DanWebSocket.Api
{
    /// <summary>
    /// Manages all principals and their session counts.
    /// </summary>
    internal class PrincipalManager
    {
        private readonly Dictionary<string, PrincipalTX> _principals = new Dictionary<string, PrincipalTX>();
        private readonly Dictionary<string, int> _sessionCounts = new Dictionary<string, int>();
        private Action<PrincipalTX>? _onNewPrincipal;

        internal void SetOnNewPrincipal(Action<PrincipalTX> fn) { _onNewPrincipal = fn; }

        public int Size => _principals.Count;

        public PrincipalTX Principal(string name)
        {
            if (_principals.TryGetValue(name, out var ptx))
                return ptx;

            ptx = new PrincipalTX(name);
            _principals[name] = ptx;
            _onNewPrincipal?.Invoke(ptx);
            return ptx;
        }

        public List<string> Principals
        {
            get
            {
                var result = new List<string>();
                foreach (var kvp in _sessionCounts)
                {
                    if (kvp.Value > 0) result.Add(kvp.Key);
                }
                return result;
            }
        }

        public bool Has(string name) => _principals.ContainsKey(name);

        public void Delete(string name)
        {
            _principals.Remove(name);
            _sessionCounts.Remove(name);
        }

        public void Clear()
        {
            _principals.Clear();
            _sessionCounts.Clear();
        }

        internal void AddSession(string principal)
        {
            _sessionCounts.TryGetValue(principal, out int count);
            _sessionCounts[principal] = count + 1;
        }

        /// <summary>
        /// Returns true when session count reaches 0.
        /// </summary>
        internal bool RemoveSession(string principal)
        {
            _sessionCounts.TryGetValue(principal, out int count);
            int newCount = count - 1;
            if (newCount <= 0)
            {
                _sessionCounts.Remove(principal);
                return true;
            }
            _sessionCounts[principal] = newCount;
            return false;
        }

        internal bool HasActiveSessions(string principal)
        {
            _sessionCounts.TryGetValue(principal, out int count);
            return count > 0;
        }
    }
}
