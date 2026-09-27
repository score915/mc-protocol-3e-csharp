using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Mc3E;
using Xunit;

namespace Mc3E.Tests;

public sealed class Mc3EClientTests
{
    [Fact]
    public void KeyenceWordReadBuildsExpectedFrameAndDecodesValue()
    {
        var transport = new FakeTransport(Hex("39 30"));
        var client = new Mc3EClient("192.168.0.10", PlcVendor.Keyence, 5000, transport: transport);

        var values = client.ReadWords("DM", 60002, 1);

        Assert.Equal(new ushort[] { 12345 }, values);
        Assert.Equal("500000FFFF03000C0010000104000062EA00A80100", Hex(transport.Requests.Single()));
    }

    [Fact]
    public void KeyenceMrAddressIsConvertedAndRead()
    {
        var transport = new FakeTransport(Hex("10"));
        var client = new Mc3EClient("192.168.0.10", PlcVendor.Keyence, 5000, transport: transport);

        var values = client.ReadBits("MR", 60000, 1);

        Assert.Equal(new[] { true }, values);
        Assert.Equal("500000FFFF03000C00100001040100802500900100", Hex(transport.Requests.Single()));
    }

    [Fact]
    public void MitsubishiWordReadBuildsExpectedFrame()
    {
        var transport = new FakeTransport(Hex("34 12"));
        var client = new Mc3EClient("192.168.0.20", PlcVendor.Mitsubishi, 5002, transport: transport);

        var values = client.ReadWords("D", 100, 1);

        Assert.Equal(new ushort[] { 0x1234 }, values);
        Assert.Equal("500000FFFF03000C00100001040000640000A80100", Hex(transport.Requests.Single()));
    }

    [Fact]
    public void WordAndBitWritesBuildExpectedFrames()
    {
        var wordTransport = new FakeTransport(Array.Empty<byte>());
        var keyence = new Mc3EClient("192.168.0.10", PlcVendor.Keyence, 5000,
            transport: wordTransport);
        keyence.WriteWords("DM", 60002, new ushort[] { 12345 });
        Assert.Equal("500000FFFF03000E0010000114000062EA00A801003930",
            Hex(wordTransport.Requests.Single()));

        var bitTransport = new FakeTransport(Array.Empty<byte>());
        var mitsubishi = new Mc3EClient("192.168.0.20", PlcVendor.Mitsubishi, 5002,
            transport: bitTransport);
        mitsubishi.WriteBits("M", 6000, new[] { true, false, true });
        Assert.Equal("500000FFFF03000E001000011401007017009003001010",
            Hex(bitTransport.Requests.Single()));
    }

    [Fact]
    public void CpuModelIsDecodedAndVendorIsDetected()
    {
        var model = PadModel("V7500", 0x0037);
        var transport = new FakeTransport(model);

        var detected = Mc3EClient.DetectPlcAt(
            "192.168.0.10", 5000, transport: transport);

        Assert.Equal(PlcVendor.Keyence, detected.Vendor);
        Assert.Equal(5000, detected.Port);
        Assert.Equal("V7500", detected.Model.Name);
        Assert.Equal((ushort)0x0037, detected.Model.Code);
        Assert.Equal("500000FFFF03000600100001010000", Hex(transport.Requests.Single()));
    }

    [Fact]
    public void CpuModelCanBeReadThroughStaticApi()
    {
        var transport = new FakeTransport(PadModel("Q00UJCPU", 0x0260));

        var model = Mc3EClient.ReadModelAt(
            "192.168.0.20", PlcVendor.Mitsubishi, 5002, transport: transport);

        Assert.Equal("Q00UJCPU", model.Name);
        Assert.Equal((ushort)0x0260, model.Code);
    }

    [Fact]
    public void KeyenceCpuInformationCombinesModelDiagnosticsAndSystemValues()
    {
        var transport = new FakeTransport(
            PadModel("V7500", 0x0037),
            Hex("00"),
            Hex("00"),
            new byte[54],
            Hex("10 00 01"));
        var client = new Mc3EClient("192.168.0.10", PlcVendor.Keyence, 5000,
            transport: transport);

        var information = client.ReadCpuInformation();

        Assert.Equal("V7500", information.Model.Name);
        Assert.False(information.Diagnostics.CR2012);
        Assert.False(information.Diagnostics.CR3500);
        Assert.Equal(27, information.Diagnostics.CM5150To5176.Count);
        Assert.True(information.System.Bits["CR2002"]);
        Assert.True(information.System.Bits["CR2007"]);
        Assert.Equal("RUN", information.System.Status.State);
    }

    [Fact]
    public void MitsubishiQStatusAndSwitchAreDecoded()
    {
        var status = Mc3EClient.DecodeQStatus(0x0002);

        Assert.Equal("STOP", status.State);
        Assert.Equal("switch", status.StopPauseCause);
        Assert.Equal("RESET/L.CLR", Mc3EClient.DecodeQSwitch(0x0002));
    }

    [Fact]
    public void InvalidDevicesAndAddressesAreRejected()
    {
        var transport = new FakeTransport();
        var keyence = new Mc3EClient("192.168.0.10", PlcVendor.Keyence, 5000,
            transport: transport);
        var mitsubishi = new Mc3EClient("192.168.0.20", PlcVendor.Mitsubishi, 5002,
            transport: transport);

        Assert.Throws<ArgumentException>(() => keyence.ReadWords("X", 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => keyence.ReadBits("MR", 60016, 1));
        Assert.Throws<ArgumentException>(() => mitsubishi.ReadWords("DM", 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => mitsubishi.ReadBits("M", -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => mitsubishi.ReadWords("D", 0, 257));
    }

    [Fact]
    public void InvalidPackedBitValueIsRejected()
    {
        var transport = new FakeTransport(Hex("20"));
        var client = new Mc3EClient("192.168.0.20", PlcVendor.Mitsubishi, 5002,
            transport: transport);

        Assert.Throws<McProtocolException>(() => client.ReadBits("M", 0, 1));
    }

    [Fact]
    public async Task TcpTransportReadsAComplete3EResponse()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var stream = socket.GetStream();
            var request = new byte[64];
            Assert.True(await stream.ReadAsync(request) > 0);
            var response = Hex("D0 00 00 FF FF 03 00 04 00 00 00 39 30");
            await stream.WriteAsync(response);
        });

        try
        {
            var data = new TcpMc3ETransport().Exchange(
                "127.0.0.1", port, TimeSpan.FromSeconds(2), Hex("50 00"));
            Assert.Equal(Hex("39 30"), data);
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static byte[] PadModel(string name, ushort code)
    {
        var result = Enumerable.Repeat((byte)' ', 18).ToArray();
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, result, nameBytes.Length);
        result[16] = (byte)(code & 0xFF);
        result[17] = (byte)(code >> 8);
        return result;
    }

    private static byte[] Hex(string value) =>
        Convert.FromHexString(value.Replace(" ", string.Empty));

    private static string Hex(byte[] value) => Convert.ToHexString(value);

    private sealed class FakeTransport : IMc3ETransport
    {
        private readonly Queue<byte[]> responses;

        public FakeTransport(params byte[][] responses)
        {
            this.responses = new Queue<byte[]>(responses);
        }

        public List<byte[]> Requests { get; } = new();

        public byte[] Exchange(string ipAddress, int port, TimeSpan timeout, byte[] request)
        {
            Requests.Add(request);
            if (responses.Count == 0)
                throw new InvalidOperationException("No fake response was queued.");
            return responses.Dequeue();
        }
    }
}
