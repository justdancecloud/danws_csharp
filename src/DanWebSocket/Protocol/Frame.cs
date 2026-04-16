namespace DanWebSocket.Protocol
{
    /// <summary>
    /// A single DanProtocol frame.
    /// </summary>
    public class Frame
    {
        public FrameType FrameType { get; set; }
        public uint KeyId { get; set; }
        public DataType DataType { get; set; }

        /// <summary>
        /// The deserialized payload value. Concrete type depends on FrameType + DataType.
        /// </summary>
        public object? Payload { get; set; }

        public Frame(FrameType frameType, uint keyId, DataType dataType, object? payload)
        {
            FrameType = frameType;
            KeyId = keyId;
            DataType = dataType;
            Payload = payload;
        }
    }
}
