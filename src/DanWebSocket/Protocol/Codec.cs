using System;
using System.Collections.Generic;
using System.Text;

namespace DanWebSocket.Protocol
{
    /// <summary>
    /// DLE-based frame encoding/decoding for DanProtocol v3.5.
    /// </summary>
    public static class Codec
    {
        public const byte DLE = 0x10;
        public const byte STX = 0x02;
        public const byte ETX = 0x03;
        public const byte ENQ = 0x05;

        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Encode a single Frame into bytes with DLE STX/ETX framing and DLE escaping.
        /// </summary>
        public static byte[] Encode(Frame frame)
        {
            byte[] rawPayload;

            if (IsKeyRegistrationFrame(frame.FrameType))
            {
                rawPayload = Utf8.GetBytes((string)frame.Payload!);
            }
            else if (IsSignalFrame(frame.FrameType))
            {
                rawPayload = Array.Empty<byte>();
            }
            else
            {
                rawPayload = Serializer.Serialize(frame.DataType, frame.Payload);
            }

            // Build raw body: [FrameType:1] [KeyID:4 BE] [DataType:1] [Payload:N]
            var rawBody = new byte[6 + rawPayload.Length];
            rawBody[0] = (byte)frame.FrameType;
            rawBody[1] = (byte)(frame.KeyId >> 24);
            rawBody[2] = (byte)((frame.KeyId >> 16) & 0xFF);
            rawBody[3] = (byte)((frame.KeyId >> 8) & 0xFF);
            rawBody[4] = (byte)(frame.KeyId & 0xFF);
            rawBody[5] = (byte)frame.DataType;
            Array.Copy(rawPayload, 0, rawBody, 6, rawPayload.Length);

            // DLE-escape the entire body
            byte[] escapedBody = DleEncode(rawBody);

            // Wrap with DLE STX ... DLE ETX
            var result = new byte[2 + escapedBody.Length + 2];
            result[0] = DLE;
            result[1] = STX;
            Array.Copy(escapedBody, 0, result, 2, escapedBody.Length);
            result[2 + escapedBody.Length] = DLE;
            result[3 + escapedBody.Length] = ETX;

            return result;
        }

        /// <summary>
        /// Encode multiple frames and concatenate into a single buffer.
        /// </summary>
        public static byte[] EncodeBatch(IList<Frame> frames)
        {
            int totalLength = 0;
            var encoded = new byte[frames.Count][];
            for (int i = 0; i < frames.Count; i++)
            {
                encoded[i] = Encode(frames[i]);
                totalLength += encoded[i].Length;
            }

            var result = new byte[totalLength];
            int offset = 0;
            for (int i = 0; i < encoded.Length; i++)
            {
                Array.Copy(encoded[i], 0, result, offset, encoded[i].Length);
                offset += encoded[i].Length;
            }
            return result;
        }

        /// <summary>
        /// Encode heartbeat: DLE ENQ (2 bytes).
        /// </summary>
        public static byte[] EncodeHeartbeat()
        {
            return new byte[] { DLE, ENQ };
        }

        /// <summary>
        /// Decode a byte buffer containing one or more frames.
        /// </summary>
        public static List<Frame> Decode(byte[] bytes)
        {
            return Decode(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Decode frames from a byte buffer with offset and length.
        /// </summary>
        public static List<Frame> Decode(byte[] bytes, int offset, int length)
        {
            var frames = new List<Frame>();
            int i = offset;
            int end = offset + length;

            while (i < end)
            {
                if (i + 1 >= end)
                    throw new DanWSException("FRAME_PARSE_ERROR", "Unexpected end of data");

                if (bytes[i] != DLE || bytes[i + 1] != STX)
                    throw new DanWSException("FRAME_PARSE_ERROR",
                        $"Expected DLE STX at offset {i}, got 0x{bytes[i]:X2} 0x{bytes[i + 1]:X2}");

                i += 2; // skip DLE STX

                int bodyStart = i;
                int bodyEnd = -1;

                while (i < end)
                {
                    if (bytes[i] == DLE)
                    {
                        if (i + 1 >= end)
                            throw new DanWSException("FRAME_PARSE_ERROR", "Unexpected end of data after DLE");

                        if (bytes[i + 1] == ETX)
                        {
                            bodyEnd = i;
                            i += 2; // skip DLE ETX
                            break;
                        }
                        else if (bytes[i + 1] == DLE)
                        {
                            i += 2; // escaped DLE
                        }
                        else
                        {
                            throw new DanWSException("INVALID_DLE_SEQUENCE",
                                $"Invalid DLE sequence: 0x10 0x{bytes[i + 1]:X2}");
                        }
                    }
                    else
                    {
                        i++;
                    }
                }

                if (bodyEnd == -1)
                    throw new DanWSException("FRAME_PARSE_ERROR", "Missing DLE ETX terminator");

                // DLE-decode the body
                byte[] decoded = DleDecode(bytes, bodyStart, bodyEnd - bodyStart);

                if (decoded.Length < 6)
                    throw new DanWSException("FRAME_PARSE_ERROR",
                        $"Frame body too short: {decoded.Length} bytes (minimum 6)");

                FrameType frameType = (FrameType)decoded[0];
                uint keyId = ((uint)decoded[1] << 24) | ((uint)decoded[2] << 16)
                    | ((uint)decoded[3] << 8) | decoded[4];
                DataType dataType = (DataType)decoded[5];

                // Deserialize payload
                object? payload;
                int payloadOffset = 6;
                int payloadLength = decoded.Length - 6;

                if (IsKeyRegistrationFrame(frameType))
                {
                    payload = Utf8.GetString(decoded, payloadOffset, payloadLength);
                }
                else if (IsSignalFrame(frameType))
                {
                    payload = null;
                }
                else
                {
                    payload = Serializer.Deserialize(dataType, decoded, payloadOffset, payloadLength);
                }

                frames.Add(new Frame(frameType, keyId, dataType, payload));
            }

            return frames;
        }

        // --- DLE encoding/decoding ---

        /// <summary>
        /// DLE-stuff a payload: every 0x10 byte becomes 0x10 0x10.
        /// </summary>
        public static byte[] DleEncode(byte[] payload)
        {
            int dleCount = 0;
            for (int i = 0; i < payload.Length; i++)
            {
                if (payload[i] == DLE) dleCount++;
            }

            if (dleCount == 0) return payload;

            var result = new byte[payload.Length + dleCount];
            int j = 0;
            for (int i = 0; i < payload.Length; i++)
            {
                result[j++] = payload[i];
                if (payload[i] == DLE)
                    result[j++] = DLE;
            }
            return result;
        }

        /// <summary>
        /// DLE-unstuff a payload: 0x10 0x10 becomes 0x10.
        /// </summary>
        public static byte[] DleDecode(byte[] data, int offset, int length)
        {
            int dleCount = 0;
            int end = offset + length;
            for (int i = offset; i < end; i++)
            {
                if (data[i] == DLE)
                {
                    i++; // skip next byte
                    dleCount++;
                }
            }

            if (dleCount == 0)
            {
                var copy = new byte[length];
                Array.Copy(data, offset, copy, 0, length);
                return copy;
            }

            var result = new byte[length - dleCount];
            int j = 0;
            for (int i = offset; i < end; i++)
            {
                if (data[i] == DLE)
                    i++; // skip the doubled DLE, output one
                result[j++] = data[i];
            }
            return result;
        }

        // --- Frame type classification ---

        public static bool IsSignalFrame(FrameType ft)
        {
            return ft == FrameType.ServerSync
                || ft == FrameType.ClientReady
                || ft == FrameType.ClientSync
                || ft == FrameType.ServerReady
                || ft == FrameType.ServerReset
                || ft == FrameType.ClientResyncReq
                || ft == FrameType.ClientReset
                || ft == FrameType.ServerResyncReq
                || ft == FrameType.AuthOk
                || ft == FrameType.ServerFlushEnd
                || ft == FrameType.ServerKeyDelete
                || ft == FrameType.ClientKeyRequest;
        }

        public static bool IsKeyRegistrationFrame(FrameType ft)
        {
            return ft == FrameType.ServerKeyRegistration || ft == FrameType.ClientKeyRegistration;
        }
    }
}
