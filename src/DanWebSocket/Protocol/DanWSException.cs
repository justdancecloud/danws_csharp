using System;

namespace DanWebSocket.Protocol
{
    /// <summary>
    /// Exception specific to DanProtocol errors.
    /// </summary>
    public class DanWSException : Exception
    {
        public string Code { get; }

        public DanWSException(string code, string message) : base(message)
        {
            Code = code;
        }

        public DanWSException(string code, string message, Exception innerException)
            : base(message, innerException)
        {
            Code = code;
        }
    }
}
