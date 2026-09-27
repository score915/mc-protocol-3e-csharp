using System;

namespace Mc3E;

/// <summary>Represents an invalid MC response or a nonzero PLC end code.</summary>
public sealed class McProtocolException : Exception
{
    public McProtocolException(string message) : base(message) { }
    public McProtocolException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Represents a TCP connection or I/O failure.</summary>
public sealed class McConnectionException : Exception
{
    public McConnectionException(string message) : base(message) { }
    public McConnectionException(string message, Exception innerException)
        : base(message, innerException) { }
}
