using System.Globalization;
using Mc3E;

internal static class Mc3EConsoleApp
{
    internal static void Run(string[] args, Action<string> print)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp(print);
            return;
        }

        var options = ParseArguments(args);
        if (string.IsNullOrWhiteSpace(options.IpAddress))
            throw new ArgumentException("--ip is required.");

        PlcDetectionResult? detected = null;
        var cpuAction = options.Action is "model" or "status" or "errors" or "summary" or "info";
        if (options.Vendor == null || options.Port == null || cpuAction)
        {
            detected = Mc3EClient.DetectPlcAt(options.IpAddress, options.Port,
                TimeSpan.FromSeconds(options.TimeoutSeconds));
            if (options.Vendor.HasValue && options.Vendor.Value != detected.Vendor)
                throw new InvalidOperationException(
                    $"Specified vendor {options.Vendor.Value} does not match detected vendor {detected.Vendor}.");
            options.Vendor = detected.Vendor;
            options.Port = detected.Port;
            print($"Detected PLC: vendor={VendorName(detected.Vendor)}, " +
                  $"model={detected.Model.Name} (0x{detected.Model.Code:X4}), port={detected.Port}");
        }

        var client = new Mc3EClient(options.IpAddress, options.Vendor!.Value,
            options.Port!.Value, TimeSpan.FromSeconds(options.TimeoutSeconds));
        var values = options.Values;

        switch (options.Action)
        {
            case "read":
                RequireCount(values, 3, "read DEVICE START LENGTH");
                var wordStart = ParseInt(values[1]);
                var words = client.ReadWords(values[0], wordStart, ParseInt(values[2]));
                for (var i = 0; i < words.Length; i++)
                    print($"{values[0].ToUpperInvariant()}{wordStart + i} = {words[i]} (0x{words[i]:X4})");
                break;
            case "write":
                RequireMinimum(values, 3, "write DEVICE START VALUE [VALUE ...]");
                var writeStart = ParseInt(values[1]);
                var writeWords = values.Skip(2).Select(ParseUInt16).ToArray();
                client.WriteWords(values[0], writeStart, writeWords);
                print($"Wrote {writeWords.Length} word(s) starting at {values[0].ToUpperInvariant()}{writeStart}");
                break;
            case "readbit":
                RequireCount(values, 3, "readbit DEVICE START LENGTH");
                var bitStart = ParseInt(values[1]);
                var bits = client.ReadBits(values[0], bitStart, ParseInt(values[2]));
                for (var i = 0; i < bits.Length; i++)
                {
                    var address = client.Vendor == PlcVendor.Keyence
                        ? KeyenceMrDisplayAddress(bitStart, i) : bitStart + i;
                    print($"{values[0].ToUpperInvariant()}{address} = {(bits[i] ? "ON" : "OFF")}");
                }
                break;
            case "writebit":
                RequireMinimum(values, 3, "writebit DEVICE START 0|1 [0|1 ...]");
                var bitWriteStart = ParseInt(values[1]);
                var writeBits = values.Skip(2).Select(ParseBit).ToArray();
                client.WriteBits(values[0], bitWriteStart, writeBits);
                print($"Wrote {writeBits.Length} bit(s) starting at {values[0].ToUpperInvariant()}{bitWriteStart}");
                break;
            case "model":
                var model = detected?.Model ?? client.ReadModel();
                print($"CPU model: {model.Name} (code 0x{model.Code:X4})");
                break;
            case "status":
            case "info":
                PrintCpuInformation(client, print);
                break;
            case "errors":
                PrintDiagnostics(client, client.ReadDiagnostics(), print);
                break;
            case "summary":
                PrintSystem(client, client.ReadSystemSummary(), print);
                break;
            default:
                throw new ArgumentException("Unknown command: " + options.Action);
        }
    }

    private static Options ParseArguments(string[] args)
    {
        var result = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ip": result.IpAddress = Next(args, ref i, "--ip"); break;
                case "--port": result.Port = ParseInt(Next(args, ref i, "--port")); break;
                case "--timeout":
                    result.TimeoutSeconds = double.Parse(Next(args, ref i, "--timeout"),
                        CultureInfo.InvariantCulture);
                    if (result.TimeoutSeconds <= 0) throw new ArgumentOutOfRangeException("--timeout");
                    break;
                case "--vendor":
                    var vendor = Next(args, ref i, "--vendor").ToLowerInvariant();
                    result.Vendor = vendor switch
                    {
                        "auto" => null,
                        "keyence" => PlcVendor.Keyence,
                        "mitsubishi" => PlcVendor.Mitsubishi,
                        _ => throw new ArgumentException("--vendor must be auto, keyence, or mitsubishi.")
                    };
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("Unknown option: " + args[i]);
                    if (string.IsNullOrEmpty(result.Action)) result.Action = args[i].ToLowerInvariant();
                    else result.Values.Add(args[i]);
                    break;
            }
        }
        if (string.IsNullOrEmpty(result.Action)) throw new ArgumentException("A command is required.");
        return result;
    }

    private static void PrintCpuInformation(Mc3EClient client, Action<string> print)
    {
        var information = client.ReadCpuInformation();
        print($"CPU model: {information.Model.Name} (code 0x{information.Model.Code:X4})");
        PrintSystem(client, information.System, print);
        PrintDiagnostics(client, information.Diagnostics, print);
    }

    private static void PrintDiagnostics(Mc3EClient client, CpuDiagnostics value,
        Action<string> print)
    {
        if (client.Vendor == PlcVendor.Keyence)
        {
            print($"CR2012 arithmetic error = {OnOff(value.CR2012!.Value)}; " +
                  $"CR3500 alarm = {OnOff(value.CR3500!.Value)}");
            print("CM5150..CM5176 = " + string.Join(" ",
                value.CM5150To5176.Select(item => $"0x{item:X4}")));
        }
        else
        {
            print($"SM0 = {OnOff(value.SM0!.Value)}; SM1 = {OnOff(value.SM1!.Value)}; " +
                  $"SD0 error code = 0x{value.SD0!.Value:X4}");
        }
    }

    private static void PrintSystem(Mc3EClient client, CpuSystemSummary value,
        Action<string> print)
    {
        if (client.Vendor == PlcVendor.Keyence)
        {
            print($"CPU mode = {value.Status.State} (CR2007={OnOff(value.Bits["CR2007"])})");
            print("CR2002..CR2007 = " + string.Join(" ",
                Enumerable.Range(2002, 6).Select(number => OnOff(value.Bits[$"CR{number}"]))));
        }
        else
        {
            print($"CPU switch = {value.Switch}; CPU state = {value.Status.State}; " +
                  $"cause = {value.Status.StopPauseCause}");
            print("SD200..SD203 = " + string.Join(" ",
                Enumerable.Range(200, 4).Select(number => $"0x{value.Words[$"SD{number}"]:X4}")));
        }
    }

    private static void PrintHelp(Action<string> print)
    {
        print("MC 3E binary client for Mitsubishi and KEYENCE PLCs");
        print("Usage: Mc3E.Console COMMAND [arguments] --ip ADDRESS [options]");
        print("Commands: read, write, readbit, writebit, model, status, errors, summary, info");
        print("Options: --vendor auto|keyence|mitsubishi --port PORT --timeout SECONDS");
    }

    private static string Next(string[] args, ref int index, string option)
    {
        if (++index >= args.Length) throw new ArgumentException(option + " requires a value.");
        return args[index];
    }

    private static int ParseInt(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt32(value.Substring(2), 16) : int.Parse(value, CultureInfo.InvariantCulture);

    private static ushort ParseUInt16(string value)
    {
        var parsed = ParseInt(value);
        if (parsed < ushort.MinValue || parsed > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Word value must be 0 through 65535.");
        return (ushort)parsed;
    }

    private static bool ParseBit(string value) => value switch
    {
        "0" => false,
        "1" => true,
        _ => throw new ArgumentException("Bit values must be 0 or 1.")
    };

    private static int KeyenceMrDisplayAddress(int start, int offset)
    {
        var wire = (start / 100) * 16 + start % 100 + offset;
        return (wire / 16) * 100 + wire % 16;
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";
    private static string VendorName(PlcVendor value) => value.ToString().ToLowerInvariant();

    private static void RequireCount(IReadOnlyCollection<string> values, int count, string usage)
    {
        if (values.Count != count) throw new ArgumentException("Usage: " + usage);
    }

    private static void RequireMinimum(IReadOnlyCollection<string> values, int count, string usage)
    {
        if (values.Count < count) throw new ArgumentException("Usage: " + usage);
    }

    private sealed class Options
    {
        public string Action { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public PlcVendor? Vendor { get; set; }
        public int? Port { get; set; }
        public double TimeoutSeconds { get; set; } = 3;
        public List<string> Values { get; } = new();
    }
}
