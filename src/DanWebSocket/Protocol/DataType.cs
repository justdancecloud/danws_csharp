namespace DanWebSocket.Protocol
{
    /// <summary>
    /// Wire data types for DanProtocol v3.5.
    /// </summary>
    public enum DataType : byte
    {
        Null = 0x00,
        Bool = 0x01,
        Uint8 = 0x02,
        Uint16 = 0x03,
        Uint32 = 0x04,
        Uint64 = 0x05,
        Int32 = 0x06,
        Int64 = 0x07,
        Float32 = 0x08,
        Float64 = 0x09,
        String = 0x0A,
        Binary = 0x0B,
        Timestamp = 0x0C,
        VarInteger = 0x0D,
        VarDouble = 0x0E,
        VarFloat = 0x0F,
    }
}
