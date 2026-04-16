namespace DanWebSocket.Protocol
{
    /// <summary>
    /// Wire frame types for DanProtocol v3.5.
    /// </summary>
    public enum FrameType : byte
    {
        ServerKeyRegistration = 0x00,
        ServerValue = 0x01,
        ClientKeyRegistration = 0x02,
        ClientValue = 0x03,
        ServerSync = 0x04,
        ClientReady = 0x05,
        ClientSync = 0x06,
        ServerReady = 0x07,
        Error = 0x08,
        ServerReset = 0x09,
        ClientResyncReq = 0x0A,
        ClientReset = 0x0B,
        ServerResyncReq = 0x0C,
        Identify = 0x0D,
        Auth = 0x0E,
        AuthOk = 0x0F,
        // 0x10 is reserved (DLE control character)
        AuthFail = 0x11,
        ArrayShiftLeft = 0x20,
        ArrayShiftRight = 0x21,
        ServerKeyDelete = 0x22,
        ClientKeyRequest = 0x23,
        ServerFlushEnd = 0xFF,
    }
}
