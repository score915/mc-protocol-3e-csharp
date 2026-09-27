using System;

namespace Mc3E;

/// <summary>Exchanges one MC 3E request and returns response data after the end code.</summary>
public interface IMc3ETransport
{
    /// <summary>Sends one complete request frame.</summary>
    byte[] Exchange(string ipAddress, int port, TimeSpan timeout, byte[] request);
}
