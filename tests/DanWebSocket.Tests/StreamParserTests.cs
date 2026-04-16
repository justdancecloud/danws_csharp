using System;
using System.Collections.Generic;
using Xunit;
using DanWebSocket.Protocol;

namespace DanWebSocket.Tests
{
    public class StreamParserTests
    {
        [Fact]
        public void Parse_SingleFrame()
        {
            var parser = new StreamParser();
            var frames = new List<Frame>();
            parser.OnFrame += f => frames.Add(f);

            // Encode a simple frame and feed it
            var frame = new Frame(FrameType.ServerValue, 1, DataType.VarInteger, 42);
            var encoded = Codec.Encode(frame);
            parser.Feed(encoded);

            Assert.Single(frames);
            Assert.Equal(42, frames[0].Payload);
        }

        [Fact]
        public void Parse_PartialBytes()
        {
            var parser = new StreamParser();
            var frames = new List<Frame>();
            parser.OnFrame += f => frames.Add(f);

            var frame = new Frame(FrameType.ServerValue, 1, DataType.String, "hello");
            var encoded = Codec.Encode(frame);

            // Feed byte by byte
            for (int i = 0; i < encoded.Length; i++)
            {
                parser.Feed(new byte[] { encoded[i] });
            }

            Assert.Single(frames);
            Assert.Equal("hello", frames[0].Payload);
        }

        [Fact]
        public void Parse_MultipleFramesInOneChunk()
        {
            var parser = new StreamParser();
            var frames = new List<Frame>();
            parser.OnFrame += f => frames.Add(f);

            var frameList = new List<Frame>
            {
                new Frame(FrameType.ServerKeyRegistration, 1, DataType.Null, "key1"),
                new Frame(FrameType.ServerValue, 1, DataType.VarInteger, 100),
                new Frame(FrameType.ServerFlushEnd, 0, DataType.Null, null),
            };
            var batch = Codec.EncodeBatch(frameList);
            parser.Feed(batch);

            Assert.Equal(3, frames.Count);
            Assert.Equal("key1", frames[0].Payload);
            Assert.Equal(100, frames[1].Payload);
            Assert.Equal(FrameType.ServerFlushEnd, frames[2].FrameType);
        }

        [Fact]
        public void Parse_Heartbeat()
        {
            var parser = new StreamParser();
            int heartbeatCount = 0;
            parser.OnHeartbeat += () => heartbeatCount++;

            parser.Feed(Codec.EncodeHeartbeat());
            Assert.Equal(1, heartbeatCount);

            parser.Feed(Codec.EncodeHeartbeat());
            Assert.Equal(2, heartbeatCount);
        }

        [Fact]
        public void Parse_HeartbeatBetweenFrames()
        {
            var parser = new StreamParser();
            var frames = new List<Frame>();
            int heartbeats = 0;
            parser.OnFrame += f => frames.Add(f);
            parser.OnHeartbeat += () => heartbeats++;

            var f1 = Codec.Encode(new Frame(FrameType.ServerValue, 1, DataType.Bool, true));
            var hb = Codec.EncodeHeartbeat();
            var f2 = Codec.Encode(new Frame(FrameType.ServerValue, 2, DataType.Bool, false));

            var combined = new byte[f1.Length + hb.Length + f2.Length];
            Array.Copy(f1, combined, f1.Length);
            Array.Copy(hb, 0, combined, f1.Length, hb.Length);
            Array.Copy(f2, 0, combined, f1.Length + hb.Length, f2.Length);

            parser.Feed(combined);

            Assert.Equal(2, frames.Count);
            Assert.Equal(1, heartbeats);
            Assert.Equal(true, frames[0].Payload);
            Assert.Equal(false, frames[1].Payload);
        }

        [Fact]
        public void Parse_DLEEscaping()
        {
            var parser = new StreamParser();
            var frames = new List<Frame>();
            parser.OnFrame += f => frames.Add(f);

            // KeyID 0x00000010 contains DLE byte
            var frame = new Frame(FrameType.ServerValue, 0x00000010, DataType.Bool, true);
            var encoded = Codec.Encode(frame);
            parser.Feed(encoded);

            Assert.Single(frames);
            Assert.Equal((uint)0x00000010, frames[0].KeyId);
        }

        [Fact]
        public void Parse_Reset()
        {
            var parser = new StreamParser();
            var frames = new List<Frame>();
            parser.OnFrame += f => frames.Add(f);

            // Feed partial data
            var encoded = Codec.Encode(new Frame(FrameType.ServerValue, 1, DataType.Bool, true));
            parser.Feed(new byte[] { encoded[0], encoded[1], encoded[2] }); // partial

            parser.Reset();

            // Feed complete frame after reset
            parser.Feed(encoded);
            Assert.Single(frames);
        }

        [Fact]
        public void Parse_Error_UnexpectedByte()
        {
            var parser = new StreamParser();
            var errors = new List<Exception>();
            parser.OnError += e => errors.Add(e);

            parser.Feed(new byte[] { 0xFF }); // not DLE
            Assert.Single(errors);
        }

        [Fact]
        public void Parse_Error_InvalidDLESequence()
        {
            var parser = new StreamParser();
            var errors = new List<Exception>();
            parser.OnError += e => errors.Add(e);

            parser.Feed(new byte[] { 0x10, 0xFF }); // DLE followed by invalid byte
            Assert.Single(errors);
        }
    }
}
