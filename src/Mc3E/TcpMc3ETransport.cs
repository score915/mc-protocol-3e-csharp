using System;
using System.IO;
using System.Net.Sockets;

namespace Mc3E;

/// <summary>TCP transport for QnA-compatible MC 3E binary frames.</summary>
public sealed class TcpMc3ETransport : IMc3ETransport
{
    public byte[] Exchange(string ipAddress, int port, TimeSpan timeout, byte[] request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        Mc3EClient.ValidateConnectionParameters(ipAddress, port, timeout);

        try
        {
            using (var client = new TcpClient())
            {
                var connect = client.ConnectAsync(ipAddress, port);
                if (!connect.Wait(timeout))
                    throw new McConnectionException(
                        $"MC connection timed out to {ipAddress}:{port}");
                connect.GetAwaiter().GetResult();

                var milliseconds = (int)Math.Min(int.MaxValue,
                    Math.Max(1, timeout.TotalMilliseconds));
                client.ReceiveTimeout = milliseconds;
                client.SendTimeout = milliseconds;

                using (var stream = client.GetStream())
                {
                    stream.Write(request, 0, request.Length);
                    var header = ReadExactly(stream, 9);
                    if (header[0] != 0xD0 || header[1] != 0x00)
                        throw new McProtocolException(
                            $"Expected a 3E binary response (D0 00), got {header[0]:X2} {header[1]:X2}");

                    var bodyLength = header[7] | (header[8] << 8);
                    if (bodyLength < 2 || bodyLength > 4096)
                        throw new McProtocolException($"Invalid response length: {bodyLength}");

                    var body = ReadExactly(stream, bodyLength);
                    var endCode = (ushort)(body[0] | (body[1] << 8));
                    if (endCode != 0)
                        throw new McProtocolException($"PLC end code: 0x{endCode:X4}");

                    var result = new byte[body.Length - 2];
                    Buffer.BlockCopy(body, 2, result, 0, result.Length);
                    return result;
                }
            }
        }
        catch (McProtocolException) { throw; }
        catch (McConnectionException) { throw; }
        catch (Exception ex) when (ex is SocketException || ex is IOException ||
                                   ex is AggregateException)
        {
            throw new McConnectionException(
                $"Cannot communicate with {ipAddress}:{port}: {ex.Message}", ex);
        }
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(result, offset, count - offset);
            if (read == 0)
                throw new McProtocolException(
                    "Connection closed before the full response arrived.");
            offset += read;
        }
        return result;
    }
}
