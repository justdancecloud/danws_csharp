using System;
using System.Diagnostics;
using System.Threading;
using DanWebSocket.Protocol;

namespace DanWebSocket.Connection
{
    /// <summary>
    /// Manages periodic heartbeat sending and timeout detection.
    /// </summary>
    public class HeartbeatManager
    {
        private const int SendInterval = 10_000; // 10 seconds
        private const int TimeoutThreshold = 15_000; // 15 seconds
        private const int CheckInterval = 5_000; // 5 seconds

        private Timer? _sendTimer;
        private Timer? _timeoutTimer;
        private long _lastReceived;

        private static long GetTimestamp() => Stopwatch.GetTimestamp();
        private static long ElapsedMs(long start) =>
            (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency;

        public event Action<byte[]>? OnSend;
        public event Action? OnTimeout;

        public bool IsRunning => _sendTimer != null;

        public void Start()
        {
            Stop();
            _lastReceived = GetTimestamp();

            _sendTimer = new Timer(_ =>
            {
                try { OnSend?.Invoke(Codec.EncodeHeartbeat()); }
                catch { /* ignore send errors */ }
            }, null, SendInterval, SendInterval);

            _timeoutTimer = new Timer(_ =>
            {
                try
                {
                    if (ElapsedMs(_lastReceived) > TimeoutThreshold)
                    {
                        Stop();
                        OnTimeout?.Invoke();
                    }
                }
                catch { /* ignore */ }
            }, null, CheckInterval, CheckInterval);
        }

        public void Received()
        {
            _lastReceived = GetTimestamp();
        }

        public void Stop()
        {
            _sendTimer?.Dispose();
            _sendTimer = null;
            _timeoutTimer?.Dispose();
            _timeoutTimer = null;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
