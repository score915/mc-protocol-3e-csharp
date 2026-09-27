using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Mc3E;

/// <summary>
/// Mitsubishi and KEYENCE client for QnA-compatible MC 3E binary frames over TCP.
/// </summary>
public sealed class Mc3EClient
{
    /// <summary>Conservative per-operation limit used by this package.</summary>
    public const int MaxPoints = 256;
    private const int MaxWireAddress = 0xFFFFFF;
    private static readonly int[] DetectionPorts = { 5000, 5002, 1025, 1026, 4999, 5010 };

    private readonly IMc3ETransport transport;

    public Mc3EClient(string ipAddress, PlcVendor vendor, int port,
        TimeSpan? timeout = null, IMc3ETransport? transport = null)
    {
        var actualTimeout = timeout ?? TimeSpan.FromSeconds(3);
        ValidateConnectionParameters(ipAddress, port, actualTimeout);
        IpAddress = ipAddress;
        Vendor = vendor;
        Port = port;
        Timeout = actualTimeout;
        this.transport = transport ?? new TcpMc3ETransport();
    }

    public string IpAddress { get; }
    public PlcVendor Vendor { get; }
    public int Port { get; }
    public TimeSpan Timeout { get; }

    internal static void ValidateConnectionParameters(string ipAddress, int port,
        TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            throw new ArgumentException("IP address or host name is required.", nameof(ipAddress));
        if (port < 1 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be 1 through 65535.");
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
    }

    public ushort[] ReadWords(string device, int startAddress, int length)
    {
        var address = GetWordAddress(Vendor, device, startAddress, length);
        var data = Exchange(BuildDeviceRequest(0x0401, 0, 0xA8, address, length, null));
        if (data.Length != length * 2)
            throw new McProtocolException(
                $"Expected {length * 2} word-data bytes, got {data.Length}.");
        var values = new ushort[length];
        for (var i = 0; i < length; i++)
            values[i] = ReadUInt16(data, i * 2);
        return values;
    }

    public void WriteWords(string device, int startAddress, IEnumerable<ushort> values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        var words = values.ToArray();
        var address = GetWordAddress(Vendor, device, startAddress, words.Length);
        var payload = new byte[words.Length * 2];
        for (var i = 0; i < words.Length; i++)
            WriteUInt16(payload, i * 2, words[i]);
        EnsureEmptyWriteResponse(Exchange(
            BuildDeviceRequest(0x1401, 0, 0xA8, address, words.Length, payload)));
    }

    public bool[] ReadBits(string device, int startAddress, int length)
    {
        var address = GetBitAddress(Vendor, device, startAddress, length);
        var data = Exchange(BuildDeviceRequest(0x0401, 1, 0x90, address, length, null));
        var expected = (length + 1) / 2;
        if (data.Length != expected)
            throw new McProtocolException(
                $"Expected {expected} bit-data bytes, got {data.Length}.");
        var result = new bool[length];
        for (var i = 0; i < length; i++)
        {
            var value = (data[i / 2] >> (i % 2 == 0 ? 4 : 0)) & 0x0F;
            if (value != 0 && value != 1)
                throw new McProtocolException("Invalid packed bit value in response.");
            result[i] = value == 1;
        }
        return result;
    }

    public void WriteBits(string device, int startAddress, IEnumerable<bool> values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        var bits = values.ToArray();
        var address = GetBitAddress(Vendor, device, startAddress, bits.Length);
        var payload = new byte[(bits.Length + 1) / 2];
        for (var i = 0; i < bits.Length; i++)
            if (bits[i]) payload[i / 2] |= (byte)(1 << (i % 2 == 0 ? 4 : 0));
        EnsureEmptyWriteResponse(Exchange(
            BuildDeviceRequest(0x1401, 1, 0x90, address, bits.Length, payload)));
    }

    public CpuModelInfo ReadModel()
    {
        return DecodeModelResponse(Exchange(BuildCommandRequest(0x0101, 0)));
    }

    public bool[] ReadKeyenceControlRelays(int startAddress, int length)
    {
        RequireVendor(PlcVendor.Keyence, "CR");
        var address = GetKeyenceCrAddress(startAddress, length);
        return DecodePackedBits(Exchange(
            BuildDeviceRequest(0x0401, 1, 0x91, address, length, null)), length, "CR");
    }

    public ushort[] ReadKeyenceControlMemory(int startAddress, int length)
    {
        RequireVendor(PlcVendor.Keyence, "CM");
        ValidateCount(length);
        if (startAddress < 0 || startAddress + length - 1 > 5999)
            throw new ArgumentOutOfRangeException(nameof(startAddress),
                "KEYENCE CM range must fit CM0000 through CM5999.");
        return DecodeWords(Exchange(
            BuildDeviceRequest(0x0401, 0, 0xA9, startAddress, length, null)), length, "CM");
    }

    public ushort[] ReadMitsubishiSpecialWords(int startAddress, int length)
    {
        RequireVendor(PlcVendor.Mitsubishi, "SD");
        ValidateLinearRange(startAddress, length, MaxWireAddress, nameof(startAddress));
        return DecodeWords(Exchange(
            BuildDeviceRequest(0x0401, 0, 0xA9, startAddress, length, null)), length, "SD");
    }

    public bool[] ReadMitsubishiSpecialBits(int startAddress, int length)
    {
        RequireVendor(PlcVendor.Mitsubishi, "SM");
        ValidateLinearRange(startAddress, length, MaxWireAddress, nameof(startAddress));
        return DecodePackedBits(Exchange(
            BuildDeviceRequest(0x0401, 1, 0x91, startAddress, length, null)), length, "SM");
    }

    public CpuStatusInfo ReadStatus()
    {
        if (Vendor == PlcVendor.Keyence)
        {
            var running = ReadKeyenceControlRelays(2007, 1)[0];
            return new CpuStatusInfo((ushort)(running ? 1 : 0),
                running ? "RUN" : "PROGRAM", "not available");
        }
        return DecodeQStatus(ReadMitsubishiSpecialWords(203, 1)[0]);
    }

    public CpuDiagnostics ReadDiagnostics()
    {
        if (Vendor == PlcVendor.Keyence)
        {
            return new CpuDiagnostics
            {
                CR2012 = ReadKeyenceControlRelays(2012, 1)[0],
                CR3500 = ReadKeyenceControlRelays(3500, 1)[0],
                CM5150To5176 = ReadKeyenceControlMemory(5150, 27)
            };
        }
        var flags = ReadMitsubishiSpecialBits(0, 2);
        return new CpuDiagnostics
        {
            SM0 = flags[0],
            SM1 = flags[1],
            SD0 = ReadMitsubishiSpecialWords(0, 1)[0]
        };
    }

    public CpuSystemSummary ReadSystemSummary()
    {
        if (Vendor == PlcVendor.Keyence)
        {
            var values = ReadKeyenceControlRelays(2002, 6);
            var bits = new Dictionary<string, bool>();
            for (var i = 0; i < values.Length; i++) bits[$"CR{2002 + i}"] = values[i];
            return new CpuSystemSummary
            {
                Bits = bits,
                Status = new CpuStatusInfo((ushort)(values[5] ? 1 : 0),
                    values[5] ? "RUN" : "PROGRAM", "not available")
            };
        }

        var registers = ReadMitsubishiSpecialWords(200, 4);
        var words = new Dictionary<string, ushort>();
        for (var i = 0; i < registers.Length; i++) words[$"SD{200 + i}"] = registers[i];
        return new CpuSystemSummary
        {
            Words = words,
            Switch = DecodeQSwitch(registers[0]),
            Status = DecodeQStatus(registers[3])
        };
    }

    public CpuInformation ReadCpuInformation()
    {
        return new CpuInformation(ReadModel(), ReadDiagnostics(), ReadSystemSummary());
    }

    public static CpuStatusInfo DecodeQStatus(ushort sd203)
    {
        string state;
        switch (sd203 & 0x0F)
        {
            case 0: state = "RUN"; break;
            case 1: state = "STEP-RUN"; break;
            case 2: state = "STOP"; break;
            case 3: state = "PAUSE"; break;
            default: state = "UNKNOWN"; break;
        }
        string cause;
        switch ((sd203 >> 4) & 0x0F)
        {
            case 0: cause = "switch"; break;
            case 1: cause = "remote contact"; break;
            case 2: cause = "remote operation"; break;
            case 3: cause = "program instruction"; break;
            case 4: cause = "error"; break;
            default: cause = "unknown"; break;
        }
        return new CpuStatusInfo(sd203, state, cause);
    }

    public static string DecodeQSwitch(ushort sd200)
    {
        switch (sd200 & 0x0F)
        {
            case 0: return "RUN";
            case 1: return "STOP";
            case 2: return "RESET/L.CLR";
            default: return "UNKNOWN";
        }
    }

    public static PlcDetectionResult DetectPlcAt(string ipAddress, int? port = null,
        TimeSpan? timeout = null, IMc3ETransport? transport = null)
    {
        var actualTimeout = timeout ?? TimeSpan.FromSeconds(3);
        var actualTransport = transport ?? new TcpMc3ETransport();
        var candidates = port.HasValue ? new[] { port.Value } : DetectionPorts;
        var failures = new List<string>();

        foreach (var candidate in candidates)
        {
            try
            {
                var attemptTimeout = actualTimeout < TimeSpan.FromSeconds(1)
                    ? actualTimeout : TimeSpan.FromSeconds(1);
                ValidateConnectionParameters(ipAddress, candidate, attemptTimeout);
                var data = actualTransport.Exchange(ipAddress, candidate, attemptTimeout,
                    BuildCommandRequest(0x0101, 0));
                var model = DecodeModelResponse(data);
                var upper = model.Name.ToUpperInvariant();
                PlcVendor vendor;
                if (upper.StartsWith("V", StringComparison.Ordinal) ||
                    upper.StartsWith("KV", StringComparison.Ordinal))
                    vendor = PlcVendor.Keyence;
                else if (upper.StartsWith("Q", StringComparison.Ordinal) ||
                         upper.StartsWith("L", StringComparison.Ordinal) ||
                         upper.StartsWith("R", StringComparison.Ordinal) ||
                         upper.StartsWith("FX", StringComparison.Ordinal) ||
                         upper.StartsWith("A", StringComparison.Ordinal))
                    vendor = PlcVendor.Mitsubishi;
                else
                    throw new McProtocolException(
                        $"Unknown CPU model returned by 0101: {model.Name}");
                return new PlcDetectionResult(vendor, candidate, model);
            }
            catch (Exception ex) when (ex is McConnectionException ||
                                       ex is McProtocolException)
            {
                failures.Add($"{candidate}: {ex.Message}");
            }
        }
        throw new McConnectionException(
            $"PLC auto-detection failed for {ipAddress} ({string.Join("; ", failures)})");
    }

    public static ushort[] ReadWordsAt(string ipAddress, PlcVendor vendor, int port,
        string device, int startAddress, int length, TimeSpan? timeout = null,
        IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, vendor, port, timeout, transport)
            .ReadWords(device, startAddress, length);

    public static void WriteWordsAt(string ipAddress, PlcVendor vendor, int port,
        string device, int startAddress, IEnumerable<ushort> values,
        TimeSpan? timeout = null, IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, vendor, port, timeout, transport)
            .WriteWords(device, startAddress, values);

    public static bool[] ReadBitsAt(string ipAddress, PlcVendor vendor, int port,
        string device, int startAddress, int length, TimeSpan? timeout = null,
        IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, vendor, port, timeout, transport)
            .ReadBits(device, startAddress, length);

    public static void WriteBitsAt(string ipAddress, PlcVendor vendor, int port,
        string device, int startAddress, IEnumerable<bool> values,
        TimeSpan? timeout = null, IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, vendor, port, timeout, transport)
            .WriteBits(device, startAddress, values);

    public static CpuModelInfo ReadModelAt(string ipAddress, PlcVendor vendor,
        int port, TimeSpan? timeout = null, IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, vendor, port, timeout, transport).ReadModel();

    public static bool[] ReadKeyenceControlRelaysAt(string ipAddress, int port,
        int startAddress, int length, TimeSpan? timeout = null,
        IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, PlcVendor.Keyence, port, timeout, transport)
            .ReadKeyenceControlRelays(startAddress, length);

    public static ushort[] ReadKeyenceControlMemoryAt(string ipAddress, int port,
        int startAddress, int length, TimeSpan? timeout = null,
        IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, PlcVendor.Keyence, port, timeout, transport)
            .ReadKeyenceControlMemory(startAddress, length);

    public static bool[] ReadMitsubishiSpecialBitsAt(string ipAddress, int port,
        int startAddress, int length, TimeSpan? timeout = null,
        IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, PlcVendor.Mitsubishi, port, timeout, transport)
            .ReadMitsubishiSpecialBits(startAddress, length);

    public static ushort[] ReadMitsubishiSpecialWordsAt(string ipAddress, int port,
        int startAddress, int length, TimeSpan? timeout = null,
        IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, PlcVendor.Mitsubishi, port, timeout, transport)
            .ReadMitsubishiSpecialWords(startAddress, length);

    public static CpuStatusInfo ReadStatusAt(string ipAddress, PlcVendor vendor,
        int port, TimeSpan? timeout = null, IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, vendor, port, timeout, transport).ReadStatus();

    public static CpuDiagnostics ReadDiagnosticsAt(string ipAddress,
        PlcVendor vendor, int port, TimeSpan? timeout = null,
        IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, vendor, port, timeout, transport).ReadDiagnostics();

    public static CpuSystemSummary ReadSystemSummaryAt(string ipAddress,
        PlcVendor vendor, int port, TimeSpan? timeout = null,
        IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, vendor, port, timeout, transport).ReadSystemSummary();

    public static CpuInformation ReadCpuInformationAt(string ipAddress,
        PlcVendor vendor, int port, TimeSpan? timeout = null,
        IMc3ETransport? transport = null) =>
        new Mc3EClient(ipAddress, vendor, port, timeout, transport).ReadCpuInformation();

    private byte[] Exchange(byte[] request) =>
        transport.Exchange(IpAddress, Port, Timeout, request);

    private void RequireVendor(PlcVendor expected, string device)
    {
        if (Vendor != expected)
            throw new InvalidOperationException(
                $"{device} reading is enabled only for {expected}.");
    }

    private static int GetWordAddress(PlcVendor vendor, string device,
        int address, int length)
    {
        ValidateCount(length);
        var normalized = (device ?? string.Empty).ToUpperInvariant();
        var valid = vendor == PlcVendor.Keyence
            ? normalized == "DM" || normalized == "D"
            : normalized == "D";
        if (!valid)
            throw new ArgumentException(
                "Word device must be DM/D for KEYENCE or D for Mitsubishi.", nameof(device));
        var maximum = vendor == PlcVendor.Keyence ? 65534 : MaxWireAddress;
        ValidateLinearRange(address, length, maximum, nameof(address));
        return address;
    }

    private static int GetBitAddress(PlcVendor vendor, string device,
        int address, int length)
    {
        ValidateCount(length);
        var expected = vendor == PlcVendor.Keyence ? "MR" : "M";
        if (!string.Equals(device, expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Bit device must be MR for KEYENCE or M for Mitsubishi.", nameof(device));
        if (address < 0) throw new ArgumentOutOfRangeException(nameof(address));
        if (vendor == PlcVendor.Keyence)
        {
            var word = address / 100;
            var bit = address % 100;
            if (word > 3999 || bit > 15)
                throw new ArgumentOutOfRangeException(nameof(address),
                    "KEYENCE MR must use word 0..3999 and bit 00..15.");
            var wireAddress = word * 16 + bit;
            ValidateLinearRange(wireAddress, length, 63999, nameof(address));
            return wireAddress;
        }
        ValidateLinearRange(address, length, MaxWireAddress, nameof(address));
        return address;
    }

    private static int GetKeyenceCrAddress(int address, int length)
    {
        ValidateCount(length);
        if (address < 0) throw new ArgumentOutOfRangeException(nameof(address));
        var word = address / 100;
        var bit = address % 100;
        if (word > 79 || bit > 15 || word * 16 + bit + length - 1 > 1279)
            throw new ArgumentOutOfRangeException(nameof(address),
                "KEYENCE CR must fit CR0000 through CR7915.");
        return word * 16 + bit;
    }

    private static void ValidateCount(int length)
    {
        if (length < 1 || length > MaxPoints)
            throw new ArgumentOutOfRangeException(nameof(length),
                $"Length must be 1 through {MaxPoints}.");
    }

    private static void ValidateLinearRange(int address, int length, int maximum,
        string parameterName)
    {
        ValidateCount(length);
        if (address < 0 || (long)address + length - 1 > maximum)
            throw new ArgumentOutOfRangeException(parameterName,
                "Device address range is outside this client's supported range.");
    }

    private static byte[] BuildDeviceRequest(ushort command, ushort subcommand,
        byte deviceCode, int wireAddress, int length, byte[]? payload)
    {
        payload = payload ?? new byte[0];
        var bodyLength = 12 + payload.Length;
        var result = new byte[9 + bodyLength];
        WriteUInt16(result, 0, 0x0050);
        result[2] = 0;
        result[3] = 0xFF;
        WriteUInt16(result, 4, 0x03FF);
        result[6] = 0;
        WriteUInt16(result, 7, (ushort)bodyLength);
        WriteUInt16(result, 9, 0x0010);
        WriteUInt16(result, 11, command);
        WriteUInt16(result, 13, subcommand);
        result[15] = (byte)(wireAddress & 0xFF);
        result[16] = (byte)((wireAddress >> 8) & 0xFF);
        result[17] = (byte)((wireAddress >> 16) & 0xFF);
        result[18] = deviceCode;
        WriteUInt16(result, 19, (ushort)length);
        Buffer.BlockCopy(payload, 0, result, 21, payload.Length);
        return result;
    }

    private static byte[] BuildCommandRequest(ushort command, ushort subcommand)
    {
        var result = new byte[15];
        WriteUInt16(result, 0, 0x0050);
        result[2] = 0;
        result[3] = 0xFF;
        WriteUInt16(result, 4, 0x03FF);
        result[6] = 0;
        WriteUInt16(result, 7, 6);
        WriteUInt16(result, 9, 0x0010);
        WriteUInt16(result, 11, command);
        WriteUInt16(result, 13, subcommand);
        return result;
    }

    private static CpuModelInfo DecodeModelResponse(byte[] data)
    {
        if (data.Length != 18)
            throw new McProtocolException($"Expected 18 model bytes, got {data.Length}.");
        var name = Encoding.ASCII.GetString(data, 0, 16).TrimEnd(' ', '\0');
        if (name.Any(character => character > 0x7F))
            throw new McProtocolException("CPU model name is not ASCII.");
        return new CpuModelInfo(name, ReadUInt16(data, 16));
    }

    private static ushort[] DecodeWords(byte[] data, int length, string device)
    {
        if (data.Length != length * 2)
            throw new McProtocolException(
                $"Expected {length * 2} {device} bytes, got {data.Length}.");
        var result = new ushort[length];
        for (var i = 0; i < length; i++) result[i] = ReadUInt16(data, i * 2);
        return result;
    }

    private static bool[] DecodePackedBits(byte[] data, int length, string device)
    {
        var expected = (length + 1) / 2;
        if (data.Length != expected)
            throw new McProtocolException(
                $"Expected {expected} {device} bytes, got {data.Length}.");
        var result = new bool[length];
        for (var i = 0; i < length; i++)
        {
            var value = (data[i / 2] >> (i % 2 == 0 ? 4 : 0)) & 0x0F;
            if (value != 0 && value != 1)
                throw new McProtocolException($"Invalid {device} bit value in response.");
            result[i] = value == 1;
        }
        return result;
    }

    private static void EnsureEmptyWriteResponse(byte[] data)
    {
        if (data.Length != 0)
            throw new McProtocolException(
                $"Unexpected write response data: {BitConverter.ToString(data).Replace("-", string.Empty)}");
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)(value >> 8);
    }
}
