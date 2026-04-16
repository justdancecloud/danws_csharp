using System;
using System.Collections.Generic;
using Xunit;
using DanWebSocket.Protocol;

namespace DanWebSocket.Tests
{
    public class CodecTests
    {
        // --- DLE Encoding/Decoding ---

        [Fact]
        public void DleEncode_NoDLE_ReturnsUnchanged()
        {
            var input = new byte[] { 0x01, 0x02, 0x03, 0xFF };
            var result = Codec.DleEncode(input);
            Assert.Equal(input, result);
        }

        [Fact]
        public void DleEncode_WithDLE_Doubles()
        {
            var input = new byte[] { 0x01, 0x10, 0x03 };
            var result = Codec.DleEncode(input);
            Assert.Equal(new byte[] { 0x01, 0x10, 0x10, 0x03 }, result);
        }

        [Fact]
        public void DleEncode_MultipleDLE()
        {
            var input = new byte[] { 0x10, 0x10 };
            var result = Codec.DleEncode(input);
            Assert.Equal(new byte[] { 0x10, 0x10, 0x10, 0x10 }, result);
        }

        [Fact]
        public void DleDecode_RoundTrip()
        {
            var original = new byte[] { 0x01, 0x10, 0x03, 0x10, 0x10, 0xFF };
            var encoded = Codec.DleEncode(original);
            var decoded = Codec.DleDecode(encoded, 0, encoded.Length);
            Assert.Equal(original, decoded);
        }

        // --- Frame Encode/Decode Roundtrip ---

        [Fact]
        public void Roundtrip_Null()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Null, null);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Null(frames[0].Payload);
            Assert.Equal(DataType.Null, frames[0].DataType);
        }

        [Fact]
        public void Roundtrip_Bool_True()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Bool, true);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(true, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_Bool_False()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Bool, false);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(false, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_Uint8()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Uint8, (byte)42);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(42, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_Uint16()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Uint16, 1234);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(1234, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_Uint32()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Uint32, 100000);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(100000, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_Int32()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Int32, -42);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(-42, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_Int64()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Int64, long.MinValue);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(long.MinValue, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_Uint64()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Uint64, (ulong)1234567890123456789);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            // Deserialized as long
            Assert.Equal((long)1234567890123456789, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_Float32()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Float32, 3.14f);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            // Float32 deserializes to double
            Assert.IsType<double>(frames[0].Payload);
            Assert.Equal((double)3.14f, (double)frames[0].Payload!, 4);
        }

        [Fact]
        public void Roundtrip_Float64()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Float64, 3.141592653589793);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(3.141592653589793, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_String()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.String, "Hello, World!");
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal("Hello, World!", frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_String_Unicode()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.String, "Hello, \u4e16\u754c!");
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal("Hello, \u4e16\u754c!", frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_Binary()
        {
            var data = new byte[] { 0x00, 0x10, 0xFF, 0x42 };
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Binary, data);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(data, (byte[])frames[0].Payload!);
        }

        [Fact]
        public void Roundtrip_Timestamp()
        {
            var now = DateTimeOffset.UtcNow;
            // Truncate to millisecond precision
            var expected = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Timestamp, now);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(expected, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_VarInteger_Positive()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.VarInteger, 42);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(42, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_VarInteger_Negative()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.VarInteger, -42);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(-42, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_VarInteger_Zero()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.VarInteger, 0);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(0, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_VarInteger_Large()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.VarInteger, 100000);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(100000, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_VarDouble_314()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.VarDouble, 3.14);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(3.14, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_VarDouble_Negative()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.VarDouble, -7.5);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(-7.5, frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_VarDouble_NaN()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.VarDouble, double.NaN);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.True(double.IsNaN((double)frames[0].Payload!));
        }

        [Fact]
        public void Roundtrip_VarDouble_Infinity()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.VarDouble, double.PositiveInfinity);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.True(double.IsPositiveInfinity((double)frames[0].Payload!));
        }

        [Fact]
        public void Roundtrip_VarFloat()
        {
            var frame = new Frame(FrameType.ServerValue, 1, DataType.VarFloat, 3.14f);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            // VarFloat deserializes as double
            Assert.Equal(3.14, (double)frames[0].Payload!, 1);
        }

        // --- Signal Frames ---

        [Fact]
        public void Roundtrip_SignalFrame_ServerSync()
        {
            var frame = new Frame(FrameType.ServerSync, 0, DataType.Null, null);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(FrameType.ServerSync, frames[0].FrameType);
            Assert.Null(frames[0].Payload);
        }

        [Fact]
        public void Roundtrip_SignalFrame_ServerFlushEnd()
        {
            var frame = new Frame(FrameType.ServerFlushEnd, 0, DataType.Null, null);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(FrameType.ServerFlushEnd, frames[0].FrameType);
        }

        // --- Key Registration Frames ---

        [Fact]
        public void Roundtrip_KeyRegistration()
        {
            var frame = new Frame(FrameType.ServerKeyRegistration, 5, DataType.Null, "sensor.temperature");
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal(FrameType.ServerKeyRegistration, frames[0].FrameType);
            Assert.Equal((uint)5, frames[0].KeyId);
            Assert.Equal("sensor.temperature", frames[0].Payload);
        }

        // --- DLE Escaping in KeyID ---

        [Fact]
        public void Encode_KeyId_ContainingDLE()
        {
            // KeyID 0x00000010 contains a DLE byte
            var frame = new Frame(FrameType.ServerValue, 0x00000010, DataType.Bool, true);
            var encoded = Codec.Encode(frame);
            var frames = Codec.Decode(encoded);
            Assert.Single(frames);
            Assert.Equal((uint)0x00000010, frames[0].KeyId);
            Assert.Equal(true, frames[0].Payload);
        }

        // --- Batch Encoding ---

        [Fact]
        public void Batch_MultipleFrames()
        {
            var frames = new List<Frame>
            {
                new Frame(FrameType.ServerKeyRegistration, 1, DataType.Null, "key1"),
                new Frame(FrameType.ServerValue, 1, DataType.VarInteger, 42),
                new Frame(FrameType.ServerFlushEnd, 0, DataType.Null, null),
            };
            var encoded = Codec.EncodeBatch(frames);
            var decoded = Codec.Decode(encoded);
            Assert.Equal(3, decoded.Count);
            Assert.Equal("key1", decoded[0].Payload);
            Assert.Equal(42, decoded[1].Payload);
            Assert.Equal(FrameType.ServerFlushEnd, decoded[2].FrameType);
        }

        // --- Heartbeat ---

        [Fact]
        public void Heartbeat_TwoBytes()
        {
            var hb = Codec.EncodeHeartbeat();
            Assert.Equal(new byte[] { 0x10, 0x05 }, hb);
        }

        // --- Wire Examples from spec ---

        [Fact]
        public void WireExample_Bool_True_KeyId1()
        {
            // From spec: 10 02 01 00 00 00 01 01 01 10 03
            var expected = new byte[] { 0x10, 0x02, 0x01, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x10, 0x03 };
            var frame = new Frame(FrameType.ServerValue, 1, DataType.Bool, true);
            var encoded = Codec.Encode(frame);
            Assert.Equal(expected, encoded);
        }

        [Fact]
        public void WireExample_ServerSync()
        {
            // From spec: 10 02 04 00 00 00 00 00 10 03
            var expected = new byte[] { 0x10, 0x02, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x03 };
            var frame = new Frame(FrameType.ServerSync, 0, DataType.Null, null);
            var encoded = Codec.Encode(frame);
            Assert.Equal(expected, encoded);
        }

        [Fact]
        public void WireExample_String_Alice()
        {
            // From spec: 10 02 01 00 00 00 02 0a 41 6c 69 63 65 10 03
            var expected = new byte[] { 0x10, 0x02, 0x01, 0x00, 0x00, 0x00, 0x02, 0x0A, 0x41, 0x6C, 0x69, 0x63, 0x65, 0x10, 0x03 };
            var frame = new Frame(FrameType.ServerValue, 2, DataType.String, "Alice");
            var encoded = Codec.Encode(frame);
            Assert.Equal(expected, encoded);
        }

        [Fact]
        public void WireExample_VarInteger_42()
        {
            // From spec: 10 02 01 00 00 00 03 0d 54 10 03
            // zigzag(42) = 84 = 0x54
            var expected = new byte[] { 0x10, 0x02, 0x01, 0x00, 0x00, 0x00, 0x03, 0x0D, 0x54, 0x10, 0x03 };
            var frame = new Frame(FrameType.ServerValue, 3, DataType.VarInteger, 42);
            var encoded = Codec.Encode(frame);
            Assert.Equal(expected, encoded);
        }

        [Fact]
        public void WireExample_VarDouble_3_14()
        {
            // From spec: 10 02 01 00 00 00 04 0e 02 ba 02 10 03
            // scale=2, mantissa=314 -> varint(314) = 0xBA 0x02
            var expected = new byte[] { 0x10, 0x02, 0x01, 0x00, 0x00, 0x00, 0x04, 0x0E, 0x02, 0xBA, 0x02, 0x10, 0x03 };
            var frame = new Frame(FrameType.ServerValue, 4, DataType.VarDouble, 3.14);
            var encoded = Codec.Encode(frame);
            Assert.Equal(expected, encoded);
        }

        // --- Frame type classification ---

        [Fact]
        public void IsSignalFrame_Correct()
        {
            Assert.True(Codec.IsSignalFrame(FrameType.ServerSync));
            Assert.True(Codec.IsSignalFrame(FrameType.ClientReady));
            Assert.True(Codec.IsSignalFrame(FrameType.ServerFlushEnd));
            Assert.True(Codec.IsSignalFrame(FrameType.ServerKeyDelete));
            Assert.True(Codec.IsSignalFrame(FrameType.ClientKeyRequest));
            Assert.False(Codec.IsSignalFrame(FrameType.ServerValue));
            Assert.False(Codec.IsSignalFrame(FrameType.Auth));
        }

        [Fact]
        public void IsKeyRegistrationFrame_Correct()
        {
            Assert.True(Codec.IsKeyRegistrationFrame(FrameType.ServerKeyRegistration));
            Assert.True(Codec.IsKeyRegistrationFrame(FrameType.ClientKeyRegistration));
            Assert.False(Codec.IsKeyRegistrationFrame(FrameType.ServerValue));
        }
    }
}
