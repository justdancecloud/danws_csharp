using System;
using System.Text;

namespace DanWebSocket.Protocol
{
    /// <summary>
    /// DLE-based streaming frame parser. Handles partial byte delivery and DLE escaping.
    /// </summary>
    public class StreamParser
    {
        private enum State
        {
            Idle,
            AfterDLE,        // saw DLE outside of a frame
            InFrame,
            InFrameAfterDLE, // saw DLE inside a frame
        }

        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        private State _state = State.Idle;
        private byte[] _buffer;
        private int _bufferLen;
        private readonly int _maxBufferSize;

        public event Action<Frame>? OnFrame;
        public event Action? OnHeartbeat;
        public event Action<Exception>? OnError;

        public StreamParser(int maxBufferSize = 1_048_576)
        {
            _maxBufferSize = maxBufferSize;
            _buffer = new byte[4096];
            _bufferLen = 0;
        }

        public void Feed(byte[] chunk)
        {
            Feed(chunk, 0, chunk.Length);
        }

        public void Feed(byte[] chunk, int offset, int length)
        {
            int end = offset + length;
            for (int i = offset; i < end; i++)
            {
                byte b = chunk[i];

                switch (_state)
                {
                    case State.Idle:
                        if (b == Codec.DLE)
                        {
                            _state = State.AfterDLE;
                        }
                        else
                        {
                            EmitError(new DanWSException("FRAME_PARSE_ERROR",
                                $"Unexpected byte 0x{b:X2} outside frame"));
                        }
                        break;

                    case State.AfterDLE:
                        if (b == Codec.STX)
                        {
                            _state = State.InFrame;
                            _bufferLen = 0;
                        }
                        else if (b == Codec.ENQ)
                        {
                            OnHeartbeat?.Invoke();
                            _state = State.Idle;
                        }
                        else
                        {
                            EmitError(new DanWSException("INVALID_DLE_SEQUENCE",
                                $"Invalid DLE sequence: 0x10 0x{b:X2}"));
                            _state = State.Idle;
                        }
                        break;

                    case State.InFrame:
                        if (b == Codec.DLE)
                        {
                            _state = State.InFrameAfterDLE;
                        }
                        else
                        {
                            if (_bufferLen >= _maxBufferSize)
                            {
                                EmitError(new DanWSException("FRAME_TOO_LARGE",
                                    $"Frame exceeds {_maxBufferSize} bytes"));
                                _bufferLen = 0;
                                _state = State.Idle;
                            }
                            else
                            {
                                BufferPush(b);
                            }
                        }
                        break;

                    case State.InFrameAfterDLE:
                        if (b == Codec.ETX)
                        {
                            // Frame complete
                            try
                            {
                                var body = new byte[_bufferLen];
                                Array.Copy(_buffer, body, _bufferLen);
                                var frame = ParseFrame(body);
                                OnFrame?.Invoke(frame);
                            }
                            catch (Exception err)
                            {
                                EmitError(err);
                            }
                            _bufferLen = 0;
                            _state = State.Idle;
                        }
                        else if (b == Codec.DLE)
                        {
                            // Escaped DLE
                            BufferPush(Codec.DLE);
                            _state = State.InFrame;
                        }
                        else
                        {
                            EmitError(new DanWSException("INVALID_DLE_SEQUENCE",
                                $"Invalid DLE sequence in frame: 0x10 0x{b:X2}"));
                            _bufferLen = 0;
                            _state = State.Idle;
                        }
                        break;
                }
            }
        }

        public void Reset()
        {
            _state = State.Idle;
            _bufferLen = 0;
        }

        private void BufferPush(byte b)
        {
            if (_bufferLen >= _buffer.Length)
            {
                int newCap = Math.Min((int)(_buffer.Length * 1.5), _maxBufferSize);
                if (newCap <= _buffer.Length) newCap = _buffer.Length + 1;
                var newBuf = new byte[newCap];
                Array.Copy(_buffer, newBuf, _bufferLen);
                _buffer = newBuf;
            }
            _buffer[_bufferLen++] = b;
        }

        private Frame ParseFrame(byte[] body)
        {
            if (body.Length < 6)
                throw new DanWSException("FRAME_PARSE_ERROR", $"Frame body too short: {body.Length} bytes");

            FrameType frameType = (FrameType)body[0];
            uint keyId = ((uint)body[1] << 24) | ((uint)body[2] << 16) | ((uint)body[3] << 8) | body[4];
            DataType dataType = (DataType)body[5];

            int payloadOffset = 6;
            int payloadLength = body.Length - 6;

            object? payload;
            if (Codec.IsKeyRegistrationFrame(frameType))
            {
                payload = Utf8.GetString(body, payloadOffset, payloadLength);
            }
            else if (Codec.IsSignalFrame(frameType))
            {
                payload = null;
            }
            else
            {
                payload = Serializer.Deserialize(dataType, body, payloadOffset, payloadLength);
            }

            return new Frame(frameType, keyId, dataType, payload);
        }

        private void EmitError(Exception err)
        {
            OnError?.Invoke(err);
        }
    }
}
