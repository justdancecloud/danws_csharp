using System;
using Xunit;
using DanWebSocket.Protocol;

namespace DanWebSocket.Tests
{
    public class SerializerTests
    {
        // --- VarInteger ---

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(-1)]
        [InlineData(42)]
        [InlineData(-42)]
        [InlineData(63)]
        [InlineData(64)]
        [InlineData(-64)]
        [InlineData(300)]
        [InlineData(100000)]
        [InlineData(-100000)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        public void VarInteger_Roundtrip(int value)
        {
            var serialized = Serializer.SerializeVarInteger(value);
            var deserialized = Serializer.DeserializeVarInteger(serialized, 0, serialized.Length);
            Assert.Equal(value, deserialized);
        }

        [Fact]
        public void VarInteger_42_ZigzagEncoding()
        {
            // zigzag(42) = 84 = 0x54
            var result = Serializer.SerializeVarInteger(42);
            Assert.Single(result);
            Assert.Equal(0x54, result[0]);
        }

        [Fact]
        public void VarInteger_Neg1_ZigzagEncoding()
        {
            // zigzag(-1) = 1 = 0x01
            var result = Serializer.SerializeVarInteger(-1);
            Assert.Single(result);
            Assert.Equal(0x01, result[0]);
        }

        [Fact]
        public void VarInteger_300_TwoBytes()
        {
            // zigzag(300) = 600 = 0xD8 0x04
            var result = Serializer.SerializeVarInteger(300);
            Assert.Equal(2, result.Length);
            Assert.Equal(0xD8, result[0]);
            Assert.Equal(0x04, result[1]);
        }

        // --- VarDouble ---

        [Theory]
        [InlineData(3.14)]
        [InlineData(-7.5)]
        [InlineData(0.001)]
        [InlineData(99.99)]
        [InlineData(0.5)]
        [InlineData(1.5)]
        [InlineData(0.0)]
        public void VarDouble_Roundtrip(double value)
        {
            var serialized = Serializer.SerializeVarDouble(value);
            var deserialized = Serializer.DeserializeVarDouble(serialized, 0, serialized.Length);
            Assert.Equal(value, deserialized, 10);
        }

        [Fact]
        public void VarDouble_NaN_Roundtrip()
        {
            var serialized = Serializer.SerializeVarDouble(double.NaN);
            var deserialized = Serializer.DeserializeVarDouble(serialized, 0, serialized.Length);
            Assert.True(double.IsNaN(deserialized));
        }

        [Fact]
        public void VarDouble_PosInfinity_Roundtrip()
        {
            var serialized = Serializer.SerializeVarDouble(double.PositiveInfinity);
            var deserialized = Serializer.DeserializeVarDouble(serialized, 0, serialized.Length);
            Assert.True(double.IsPositiveInfinity(deserialized));
        }

        [Fact]
        public void VarDouble_NegInfinity_Roundtrip()
        {
            var serialized = Serializer.SerializeVarDouble(double.NegativeInfinity);
            var deserialized = Serializer.DeserializeVarDouble(serialized, 0, serialized.Length);
            Assert.True(double.IsNegativeInfinity(deserialized));
        }

        [Fact]
        public void VarDouble_314_WireBytes()
        {
            // From spec: scale=2, mantissa=314 -> [0x02, 0xBA, 0x02] (3 bytes)
            var result = Serializer.SerializeVarDouble(3.14);
            Assert.Equal(3, result.Length);
            Assert.Equal(0x02, result[0]); // scale=2, positive
            Assert.Equal(0xBA, result[1]); // varint(314) low byte
            Assert.Equal(0x02, result[2]); // varint(314) high byte
        }

        [Fact]
        public void VarDouble_Neg75_WireBytes()
        {
            // From spec: scale=1, negative -> firstByte=64+1=65=0x41, mantissa=75 -> [0x41, 0x4B]
            var result = Serializer.SerializeVarDouble(-7.5);
            Assert.Equal(2, result.Length);
            Assert.Equal(0x41, result[0]); // scale=1, negative
            Assert.Equal(0x4B, result[1]); // varint(75)
        }

        // --- VarFloat ---

        [Theory]
        [InlineData(3.14f)]
        [InlineData(-7.5f)]
        [InlineData(0.5f)]
        public void VarFloat_Roundtrip(float value)
        {
            var serialized = Serializer.SerializeVarFloat(value);
            var deserialized = Serializer.DeserializeVarFloat(serialized, 0, serialized.Length);
            Assert.Equal((double)value, deserialized, 1);
        }

        [Fact]
        public void VarFloat_NaN_Roundtrip()
        {
            var serialized = Serializer.SerializeVarFloat(float.NaN);
            var deserialized = Serializer.DeserializeVarFloat(serialized, 0, serialized.Length);
            Assert.True(double.IsNaN(deserialized));
        }

        // --- Full Serializer Roundtrip ---

        [Fact]
        public void Serialize_Deserialize_AllTypes()
        {
            // Null
            AssertSerializeRoundtrip(DataType.Null, null, null);

            // Bool
            AssertSerializeRoundtrip(DataType.Bool, true, true);
            AssertSerializeRoundtrip(DataType.Bool, false, false);

            // String
            AssertSerializeRoundtrip(DataType.String, "test", "test");

            // Int32
            AssertSerializeRoundtrip(DataType.Int32, -42, -42);

            // VarInteger
            AssertSerializeRoundtrip(DataType.VarInteger, 42, 42);
        }

        private void AssertSerializeRoundtrip(DataType dt, object? input, object? expected)
        {
            var bytes = Serializer.Serialize(dt, input);
            var result = Serializer.Deserialize(dt, bytes);
            Assert.Equal(expected, result);
        }
    }
}
