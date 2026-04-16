using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using DanWebSocket.Connection;
using DanWebSocket.Protocol;
using DanWebSocket.State;

namespace DanWebSocket.Api
{
    /// <summary>
    /// Internal state wrapper for a connected session.
    /// </summary>
    internal class InternalSession
    {
        public DanWebSocketSession Session { get; }
        public WebSocket? Ws { get; set; }
        public BulkQueue BulkQueue { get; }
        public HeartbeatManager Heartbeat { get; }
        public Timer? TtlTimer { get; set; }
        public KeyRegistry? ClientRegistry { get; set; }
        public Dictionary<uint, object?>? ClientValues { get; set; }

        // Auth state
        public bool AuthPending { get; set; }
        public Timer? AuthTimer { get; set; }

        public InternalSession(DanWebSocketSession session, WebSocket? ws, BulkQueue bulkQueue, HeartbeatManager heartbeat)
        {
            Session = session;
            Ws = ws;
            BulkQueue = bulkQueue;
            Heartbeat = heartbeat;
        }

        public void ClearAuthTimer()
        {
            AuthTimer?.Dispose();
            AuthTimer = null;
        }
    }
}
