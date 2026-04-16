using System;
using Xunit;
using DanWebSocket.Protocol;
using DanWebSocket.State;

namespace DanWebSocket.Tests
{
    public class KeyRegistryTests
    {
        [Fact]
        public void RegisterOne_And_GetByKeyId()
        {
            var registry = new KeyRegistry();
            registry.RegisterOne(1, "sensor.temperature", DataType.VarDouble);

            var entry = registry.GetByKeyId(1);
            Assert.NotNull(entry);
            Assert.Equal("sensor.temperature", entry!.Path);
            Assert.Equal(DataType.VarDouble, entry.Type);
            Assert.Equal((uint)1, entry.KeyId);
        }

        [Fact]
        public void RegisterOne_And_GetByPath()
        {
            var registry = new KeyRegistry();
            registry.RegisterOne(1, "sensor.temperature", DataType.VarDouble);

            var entry = registry.GetByPath("sensor.temperature");
            Assert.NotNull(entry);
            Assert.Equal((uint)1, entry!.KeyId);
        }

        [Fact]
        public void HasKeyId_And_HasPath()
        {
            var registry = new KeyRegistry();
            registry.RegisterOne(1, "key1", DataType.String);

            Assert.True(registry.HasKeyId(1));
            Assert.True(registry.HasPath("key1"));
            Assert.False(registry.HasKeyId(2));
            Assert.False(registry.HasPath("key2"));
        }

        [Fact]
        public void RemoveByKeyId()
        {
            var registry = new KeyRegistry();
            registry.RegisterOne(1, "key1", DataType.String);

            Assert.True(registry.RemoveByKeyId(1));
            Assert.False(registry.HasKeyId(1));
            Assert.False(registry.HasPath("key1"));
            Assert.Equal(0, registry.Size);
        }

        [Fact]
        public void Clear()
        {
            var registry = new KeyRegistry();
            registry.RegisterOne(1, "key1", DataType.String);
            registry.RegisterOne(2, "key2", DataType.Bool);

            registry.Clear();
            Assert.Equal(0, registry.Size);
            Assert.False(registry.HasKeyId(1));
        }

        [Fact]
        public void Paths_ReturnsAllPaths()
        {
            var registry = new KeyRegistry();
            registry.RegisterOne(1, "a", DataType.String);
            registry.RegisterOne(2, "b", DataType.String);
            registry.RegisterOne(3, "c", DataType.String);

            var paths = registry.Paths;
            Assert.Equal(3, paths.Count);
            Assert.Contains("a", paths);
            Assert.Contains("b", paths);
            Assert.Contains("c", paths);
        }

        [Fact]
        public void Size()
        {
            var registry = new KeyRegistry();
            Assert.Equal(0, registry.Size);

            registry.RegisterOne(1, "key1", DataType.String);
            Assert.Equal(1, registry.Size);

            registry.RegisterOne(2, "key2", DataType.String);
            Assert.Equal(2, registry.Size);
        }

        [Fact]
        public void InvalidKeyPath_Empty_Throws()
        {
            var registry = new KeyRegistry();
            Assert.Throws<DanWSException>(() => registry.RegisterOne(1, "", DataType.String));
        }

        [Fact]
        public void InvalidKeyPath_SpecialChars_Throws()
        {
            var registry = new KeyRegistry();
            Assert.Throws<DanWSException>(() => registry.RegisterOne(1, "key path", DataType.String));
            Assert.Throws<DanWSException>(() => registry.RegisterOne(1, "key..path", DataType.String));
            Assert.Throws<DanWSException>(() => registry.RegisterOne(1, ".key", DataType.String));
            Assert.Throws<DanWSException>(() => registry.RegisterOne(1, "key.", DataType.String));
        }

        [Fact]
        public void ValidKeyPaths()
        {
            var registry = new KeyRegistry();
            // Should not throw
            registry.RegisterOne(1, "sensor.temperature", DataType.String);
            registry.RegisterOne(2, "scores.0", DataType.String);
            registry.RegisterOne(3, "t.0.items.3.title", DataType.String);
            registry.RegisterOne(4, "simple", DataType.String);
            Assert.Equal(4, registry.Size);
        }

        [Fact]
        public void OverwriteExistingPath()
        {
            var registry = new KeyRegistry();
            registry.RegisterOne(1, "key1", DataType.String);
            registry.RegisterOne(2, "key1", DataType.Bool); // overwrite

            Assert.Equal(2, registry.Size); // both keyId 1 and 2 exist
            var entry = registry.GetByPath("key1");
            Assert.NotNull(entry);
            Assert.Equal((uint)2, entry!.KeyId);
            Assert.Equal(DataType.Bool, entry.Type);
        }

        [Fact]
        public void KeyLimit_Throws()
        {
            var registry = new KeyRegistry(maxKeys: 2);
            registry.RegisterOne(1, "a", DataType.Null);
            registry.RegisterOne(2, "b", DataType.Null);
            Assert.Throws<DanWSException>(() => registry.RegisterOne(3, "c", DataType.Null));
        }
    }
}
