using System;
using System.Text;

namespace DanWebSocket.Protocol
{
    /// <summary>
    /// Serializes and deserializes DanProtocol data values.
    /// </summary>
    public static class Serializer
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] Serialize(DataType dataType, object? value)
        {
            switch (dataType)
            {
                case DataType.Null:
                    return Array.Empty<byte>();

                case DataType.Bool:
                    if (value is bool b)
                        return new byte[] { b ? (byte)0x01 : (byte)0x00 };
                    throw new DanWSException("INVALID_VALUE_TYPE", $"Bool requires bool, got {value?.GetType().Name ?? "null"}");

                case DataType.Uint8:
                    if (value is byte u8)
                        return new byte[] { u8 };
                    if (value is int i8 && i8 >= 0 && i8 <= 255)
                        return new byte[] { (byte)i8 };
                    throw new DanWSException("INVALID_VALUE_TYPE", $"Uint8 requires byte 0-255, got {value}");

                case DataType.Uint16:
                {
                    ushort v;
                    if (value is ushort us) v = us;
                    else if (value is int iv && iv >= 0 && iv <= 0xFFFF) v = (ushort)iv;
                    else throw new DanWSException("INVALID_VALUE_TYPE", $"Uint16 requires ushort 0-65535, got {value}");
                    var buf = new byte[2];
                    buf[0] = (byte)(v >> 8);
                    buf[1] = (byte)(v & 0xFF);
                    return buf;
                }

                case DataType.Uint32:
                {
                    uint v;
                    if (value is uint uv) v = uv;
                    else if (value is int iv && iv >= 0) v = (uint)iv;
                    else throw new DanWSException("INVALID_VALUE_TYPE", $"Uint32 requires uint, got {value}");
                    var buf = new byte[4];
                    buf[0] = (byte)(v >> 24);
                    buf[1] = (byte)((v >> 16) & 0xFF);
                    buf[2] = (byte)((v >> 8) & 0xFF);
                    buf[3] = (byte)(v & 0xFF);
                    return buf;
                }

                case DataType.Uint64:
                {
                    ulong v;
                    if (value is ulong ulv) v = ulv;
                    else if (value is long lv && lv >= 0) v = (ulong)lv;
                    else throw new DanWSException("INVALID_VALUE_TYPE", $"Uint64 requires ulong, got {value}");
                    var buf = new byte[8];
                    for (int i = 0; i < 8; i++)
                        buf[i] = (byte)((v >> (56 - i * 8)) & 0xFF);
                    return buf;
                }

                case DataType.Int32:
                {
                    int v;
                    if (value is int iv) v = iv;
                    else throw new DanWSException("INVALID_VALUE_TYPE", $"Int32 requires int, got {value}");
                    var buf = new byte[4];
                    buf[0] = (byte)((uint)v >> 24);
                    buf[1] = (byte)(((uint)v >> 16) & 0xFF);
                    buf[2] = (byte)(((uint)v >> 8) & 0xFF);
                    buf[3] = (byte)((uint)v & 0xFF);
                    return buf;
                }

                case DataType.Int64:
                {
                    long v;
                    if (value is long lv) v = lv;
                    else if (value is int iv2) v = iv2;
                    else throw new DanWSException("INVALID_VALUE_TYPE", $"Int64 requires long, got {value}");
                    var buf = new byte[8];
                    for (int i = 0; i < 8; i++)
                        buf[i] = (byte)((ulong)v >> (56 - i * 8));
                    return buf;
                }

                case DataType.Float32:
                {
                    float v;
                    if (value is float fv) v = fv;
                    else if (value is double dv2) v = (float)dv2;
                    else throw new DanWSException("INVALID_VALUE_TYPE", $"Float32 requires float, got {value}");
                    var bytes = BitConverter.GetBytes(v);
                    if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                    return bytes;
                }

                case DataType.Float64:
                {
                    double v;
                    if (value is double dv) v = dv;
                    else if (value is float fv2) v = fv2;
                    else throw new DanWSException("INVALID_VALUE_TYPE", $"Float64 requires double, got {value}");
                    var bytes = BitConverter.GetBytes(v);
                    if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                    return bytes;
                }

                case DataType.String:
                {
                    if (value is string s)
                        return Utf8.GetBytes(s);
                    throw new DanWSException("INVALID_VALUE_TYPE", $"String requires string, got {value?.GetType().Name ?? "null"}");
                }

                case DataType.Binary:
                {
                    if (value is byte[] ba)
                        return ba;
                    throw new DanWSException("INVALID_VALUE_TYPE", $"Binary requires byte[], got {value?.GetType().Name ?? "null"}");
                }

                case DataType.Timestamp:
                {
                    long ms;
                    if (value is DateTimeOffset dto)
                        ms = dto.ToUnixTimeMilliseconds();
                    else if (value is DateTime dt)
                        ms = new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                    else if (value is long lv2)
                        ms = lv2;
                    else
                        throw new DanWSException("INVALID_VALUE_TYPE", $"Timestamp requires DateTime/DateTimeOffset/long, got {value?.GetType().Name ?? "null"}");
                    var buf = new byte[8];
                    ulong ums = (ulong)ms;
                    for (int i = 0; i < 8; i++)
                        buf[i] = (byte)(ums >> (56 - i * 8));
                    return buf;
                }

                case DataType.VarInteger:
                {
                    int v;
                    if (value is int iv3) v = iv3;
                    else if (value is long lv3 && lv3 >= int.MinValue && lv3 <= int.MaxValue) v = (int)lv3;
                    else throw new DanWSException("INVALID_VALUE_TYPE", $"VarInteger requires int, got {value}");
                    return SerializeVarInteger(v);
                }

                case DataType.VarDouble:
                {
                    double v;
                    if (value is double dv3) v = dv3;
                    else if (value is float fv3) v = fv3;
                    else if (value is int iv4) v = iv4;
                    else throw new DanWSException("INVALID_VALUE_TYPE", $"VarDouble requires double, got {value}");
                    return SerializeVarDouble(v);
                }

                case DataType.VarFloat:
                {
                    float fv;
                    if (value is float fv4) fv = fv4;
                    else if (value is double dv4) fv = (float)dv4;
                    else throw new DanWSException("INVALID_VALUE_TYPE", $"VarFloat requires float, got {value}");
                    return SerializeVarFloat(fv);
                }

                default:
                    throw new DanWSException("UNKNOWN_DATA_TYPE", $"Unknown data type: 0x{(byte)dataType:X2}");
            }
        }

        public static object? Deserialize(DataType dataType, byte[] payload)
        {
            return Deserialize(dataType, payload, 0, payload.Length);
        }

        public static object? Deserialize(DataType dataType, byte[] payload, int offset, int length)
        {
            switch (dataType)
            {
                case DataType.Null:
                    return null;

                case DataType.Bool:
                    if (length != 1)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", $"Bool expects 1 byte, got {length}");
                    if (payload[offset] == 0x01) return true;
                    if (payload[offset] == 0x00) return false;
                    throw new DanWSException("INVALID_VALUE_TYPE", $"Bool payload must be 0x00 or 0x01, got 0x{payload[offset]:X2}");

                case DataType.Uint8:
                    if (length != 1)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", $"Uint8 expects 1 byte, got {length}");
                    return (int)payload[offset];

                case DataType.Uint16:
                    if (length != 2)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", $"Uint16 expects 2 bytes, got {length}");
                    return (int)((payload[offset] << 8) | payload[offset + 1]);

                case DataType.Uint32:
                    if (length != 4)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", $"Uint32 expects 4 bytes, got {length}");
                    return (int)(((uint)payload[offset] << 24) | ((uint)payload[offset + 1] << 16)
                        | ((uint)payload[offset + 2] << 8) | payload[offset + 3]);

                case DataType.Uint64:
                {
                    if (length != 8)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", $"Uint64 expects 8 bytes, got {length}");
                    ulong v = 0;
                    for (int i = 0; i < 8; i++)
                        v = (v << 8) | payload[offset + i];
                    return (long)v;
                }

                case DataType.Int32:
                {
                    if (length != 4)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", $"Int32 expects 4 bytes, got {length}");
                    int v = (payload[offset] << 24) | (payload[offset + 1] << 16)
                        | (payload[offset + 2] << 8) | payload[offset + 3];
                    return v;
                }

                case DataType.Int64:
                {
                    if (length != 8)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", $"Int64 expects 8 bytes, got {length}");
                    long v = 0;
                    for (int i = 0; i < 8; i++)
                        v = (v << 8) | payload[offset + i];
                    return v;
                }

                case DataType.Float32:
                {
                    if (length != 4)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", $"Float32 expects 4 bytes, got {length}");
                    var buf = new byte[4];
                    Array.Copy(payload, offset, buf, 0, 4);
                    if (BitConverter.IsLittleEndian) Array.Reverse(buf);
                    return (double)BitConverter.ToSingle(buf, 0);
                }

                case DataType.Float64:
                {
                    if (length != 8)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", $"Float64 expects 8 bytes, got {length}");
                    var buf = new byte[8];
                    Array.Copy(payload, offset, buf, 0, 8);
                    if (BitConverter.IsLittleEndian) Array.Reverse(buf);
                    return BitConverter.ToDouble(buf, 0);
                }

                case DataType.String:
                    return Utf8.GetString(payload, offset, length);

                case DataType.Binary:
                {
                    var result = new byte[length];
                    Array.Copy(payload, offset, result, 0, length);
                    return result;
                }

                case DataType.Timestamp:
                {
                    if (length != 8)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", $"Timestamp expects 8 bytes, got {length}");
                    long ms = 0;
                    for (int i = 0; i < 8; i++)
                        ms = (ms << 8) | payload[offset + i];
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms);
                }

                case DataType.VarInteger:
                    if (length < 1)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", "VarInteger requires at least 1 byte");
                    return DeserializeVarInteger(payload, offset, length);

                case DataType.VarDouble:
                    if (length < 1)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", "VarDouble requires at least 1 byte");
                    return DeserializeVarDouble(payload, offset, length);

                case DataType.VarFloat:
                    if (length < 1)
                        throw new DanWSException("PAYLOAD_SIZE_MISMATCH", "VarFloat requires at least 1 byte");
                    return DeserializeVarFloat(payload, offset, length);

                default:
                    throw new DanWSException("UNKNOWN_DATA_TYPE", $"Unknown data type: 0x{(byte)dataType:X2}");
            }
        }

        // --- VarInt helpers ---

        internal static byte[] EncodeVarInt(long value)
        {
            if (value == 0) return new byte[] { 0 };
            var bytes = new byte[10]; // max 10 bytes for 64-bit
            int count = 0;
            while (value > 0)
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value > 0) b |= 0x80;
                bytes[count++] = b;
            }
            var result = new byte[count];
            Array.Copy(bytes, result, count);
            return result;
        }

        internal static long DecodeVarInt(byte[] payload, int offset, int length)
        {
            long value = 0;
            long multiplier = 1;
            int end = offset + length;
            for (int i = offset; i < end; i++)
            {
                byte b = payload[i];
                value += (b & 0x7F) * multiplier;
                multiplier *= 128;
                if ((b & 0x80) == 0) break;
            }
            return value;
        }

        // --- VarInteger ---

        internal static byte[] SerializeVarInteger(int value)
        {
            // Zigzag encode: must use long to handle int.MinValue without overflow
            long v = value;
            long zigzag = v >= 0 ? v * 2 : (-v) * 2 - 1;
            return EncodeVarInt(zigzag);
        }

        internal static int DeserializeVarInteger(byte[] payload, int offset, int length)
        {
            long zigzag = DecodeVarInt(payload, offset, length);
            // Zigzag decode
            if ((zigzag & 1) != 0)
                return (int)(-(zigzag / 2) - 1);
            return (int)(zigzag / 2);
        }

        // --- VarDouble ---

        internal static byte[] SerializeVarDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || IsNegativeZero(value))
                return FallbackFloat64(value);

            double abs = Math.Abs(value);
            string str = abs.ToString("R");

            if (str.IndexOf('E') >= 0 || str.IndexOf('e') >= 0)
                return FallbackFloat64(value);

            int dotIdx = str.IndexOf('.');
            int scale = 0;
            long mantissa;
            if (dotIdx >= 0)
            {
                scale = str.Length - dotIdx - 1;
                if (scale > 63) return FallbackFloat64(value);
                // Remove the dot to get mantissa
                string mantissaStr = str.Remove(dotIdx, 1);
                if (!long.TryParse(mantissaStr, out mantissa) || mantissa > 9007199254740991L) // MAX_SAFE_INTEGER
                    return FallbackFloat64(value);
            }
            else
            {
                if (!long.TryParse(str, out mantissa) || mantissa > 9007199254740991L)
                    return FallbackFloat64(value);
            }

            bool negative = value < 0;
            byte firstByte = (byte)(negative ? scale + 64 : scale);
            byte[] varint = EncodeVarInt(mantissa);
            byte[] result = new byte[1 + varint.Length];
            result[0] = firstByte;
            Array.Copy(varint, 0, result, 1, varint.Length);
            return result;
        }

        internal static double DeserializeVarDouble(byte[] payload, int offset, int length)
        {
            byte firstByte = payload[offset];
            if (firstByte == 0x80)
            {
                if (length < 9)
                    throw new DanWSException("PAYLOAD_SIZE_MISMATCH", "VarDouble fallback requires 9 bytes");
                var buf = new byte[8];
                Array.Copy(payload, offset + 1, buf, 0, 8);
                if (BitConverter.IsLittleEndian) Array.Reverse(buf);
                return BitConverter.ToDouble(buf, 0);
            }

            bool negative = firstByte >= 64;
            int scale = negative ? firstByte - 64 : firstByte;
            long mantissa = DecodeVarInt(payload, offset + 1, length - 1);
            double result = mantissa / Math.Pow(10, scale);
            if (negative) result = -result;
            return result;
        }

        // --- VarFloat ---

        internal static byte[] SerializeVarFloat(float value)
        {
            double dv = value;
            if (float.IsNaN(value) || float.IsInfinity(value) || IsNegativeZero(dv))
                return FallbackFloat32(value);

            double abs = Math.Abs(dv);
            string str = abs.ToString("R");

            if (str.IndexOf('E') >= 0 || str.IndexOf('e') >= 0)
                return FallbackFloat32(value);

            int dotIdx = str.IndexOf('.');
            int scale = 0;
            long mantissa;
            if (dotIdx >= 0)
            {
                scale = str.Length - dotIdx - 1;
                if (scale > 63) return FallbackFloat32(value);
                string mantissaStr = str.Remove(dotIdx, 1);
                if (!long.TryParse(mantissaStr, out mantissa) || mantissa > 9007199254740991L)
                    return FallbackFloat32(value);
            }
            else
            {
                if (!long.TryParse(str, out mantissa) || mantissa > 9007199254740991L)
                    return FallbackFloat32(value);
            }

            bool neg = value < 0;
            byte firstByte = (byte)(neg ? scale + 64 : scale);
            byte[] varint = EncodeVarInt(mantissa);
            byte[] result = new byte[1 + varint.Length];
            result[0] = firstByte;
            Array.Copy(varint, 0, result, 1, varint.Length);
            return result;
        }

        internal static double DeserializeVarFloat(byte[] payload, int offset, int length)
        {
            byte firstByte = payload[offset];
            if (firstByte == 0x80)
            {
                if (length < 5)
                    throw new DanWSException("PAYLOAD_SIZE_MISMATCH", "VarFloat fallback requires 5 bytes");
                var buf = new byte[4];
                Array.Copy(payload, offset + 1, buf, 0, 4);
                if (BitConverter.IsLittleEndian) Array.Reverse(buf);
                return (double)BitConverter.ToSingle(buf, 0);
            }

            bool negative = firstByte >= 64;
            int scale = negative ? firstByte - 64 : firstByte;
            long mantissa = DecodeVarInt(payload, offset + 1, length - 1);
            double result = mantissa / Math.Pow(10, scale);
            if (negative) result = -result;
            return result;
        }

        // --- Helpers ---

        private static byte[] FallbackFloat64(double value)
        {
            var result = new byte[9];
            result[0] = 0x80;
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            Array.Copy(bytes, 0, result, 1, 8);
            return result;
        }

        private static byte[] FallbackFloat32(float value)
        {
            var result = new byte[5];
            result[0] = 0x80;
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            Array.Copy(bytes, 0, result, 1, 4);
            return result;
        }

        private static bool IsNegativeZero(double value)
        {
            return value == 0.0 && double.IsNegativeInfinity(1.0 / value);
        }
    }
}
