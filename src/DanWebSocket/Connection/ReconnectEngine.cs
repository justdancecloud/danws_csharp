using System;
using System.Threading;

namespace DanWebSocket.Connection
{
    public class ReconnectOptions
    {
        public bool Enabled { get; set; } = true;
        public int MaxRetries { get; set; } = 10; // 0 = unlimited
        public int BaseDelay { get; set; } = 1000; // ms
        public int MaxDelay { get; set; } = 30000; // ms
        public double BackoffMultiplier { get; set; } = 2.0;
        public bool Jitter { get; set; } = true;
    }

    /// <summary>
    /// Exponential backoff reconnection engine.
    /// </summary>
    public class ReconnectEngine
    {
        private readonly ReconnectOptions _options;
        private int _attempt;
        private Timer? _timer;
        private bool _active;
        private readonly Random _random = new Random();

        public event Action<int, long>? OnReconnecting; // (attempt, delay)
        public event Action? OnAttempt;
        public event Action? OnExhausted;

        public ReconnectEngine(ReconnectOptions? options = null)
        {
            _options = options ?? new ReconnectOptions();
        }

        public int Attempt => _attempt;
        public bool IsActive => _active;

        public void Start()
        {
            if (!_options.Enabled || _active) return;
            _active = true;
            _attempt = 0;
            ScheduleNext();
        }

        public void Stop()
        {
            _active = false;
            _attempt = 0;
            _timer?.Dispose();
            _timer = null;
        }

        public long CalculateDelay(int attempt)
        {
            double raw = _options.BaseDelay * Math.Pow(_options.BackoffMultiplier, attempt - 1);
            double capped = Math.Min(raw, _options.MaxDelay);

            if (_options.Jitter)
            {
                return (long)(capped * (0.5 + _random.NextDouble()));
            }
            return (long)capped;
        }

        public void Retry()
        {
            if (_active) ScheduleNext();
        }

        private void ScheduleNext()
        {
            _attempt++;

            if (_options.MaxRetries > 0 && _attempt > _options.MaxRetries)
            {
                _active = false;
                OnExhausted?.Invoke();
                return;
            }

            long delay = CalculateDelay(_attempt);
            OnReconnecting?.Invoke(_attempt, delay);

            _timer?.Dispose();
            _timer = new Timer(_ =>
            {
                _timer = null;
                OnAttempt?.Invoke();
            }, null, delay, Timeout.Infinite);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
