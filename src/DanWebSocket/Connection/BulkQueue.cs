using System;
using System.Collections.Generic;
using System.Threading;
using DanWebSocket.Protocol;

namespace DanWebSocket.Connection
{
    /// <summary>
    /// Client-side frame batching queue. Batches frames and flushes periodically.
    /// </summary>
    public class BulkQueue
    {
        private const int DefaultFlushInterval = 100; // ms
        private const int DefaultMaxQueueSize = 50_000;

        private readonly List<Frame> _queue = new List<Frame>();
        private readonly Dictionary<uint, Frame> _valueFrames = new Dictionary<uint, Frame>();
        private Timer? _timer;
        private readonly int _flushInterval;
        private readonly bool _emitFlushEnd;
        private readonly int _maxQueueSize;

        public event Action<byte[]>? OnFlush;

        public BulkQueue(int? flushIntervalMs = null, bool emitFlushEnd = false, int? maxQueueSize = null)
        {
            _flushInterval = flushIntervalMs ?? DefaultFlushInterval;
            _emitFlushEnd = emitFlushEnd;
            _maxQueueSize = maxQueueSize ?? DefaultMaxQueueSize;
        }

        public void Enqueue(Frame frame)
        {
            int totalPending = _queue.Count + _valueFrames.Count;
            if (totalPending >= _maxQueueSize) return;

            if (IsValueFrame(frame.FrameType))
            {
                _valueFrames[frame.KeyId] = frame;
            }
            else
            {
                _queue.Add(frame);
            }

            StartTimer();
        }

        public void Flush()
        {
            StopTimer();

            var frames = new List<Frame>(_queue);
            frames.AddRange(_valueFrames.Values);
            _queue.Clear();
            _valueFrames.Clear();

            if (frames.Count == 0) return;

            if (_emitFlushEnd)
            {
                frames.Add(new Frame(FrameType.ServerFlushEnd, 0, DataType.Null, null));
            }

            OnFlush?.Invoke(Codec.EncodeBatch(frames));
        }

        public void Clear()
        {
            StopTimer();
            _queue.Clear();
            _valueFrames.Clear();
        }

        public int Pending => _queue.Count + _valueFrames.Count;

        private void StartTimer()
        {
            if (_timer == null)
            {
                _timer = new Timer(_ =>
                {
                    _timer = null;
                    Flush();
                }, null, _flushInterval, Timeout.Infinite);
            }
        }

        private void StopTimer()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private static bool IsValueFrame(FrameType ft)
        {
            return ft == FrameType.ServerValue || ft == FrameType.ClientValue;
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
