# Mc3E.Client

[日本語版](README.ja.md)

A dependency-free .NET library for Mitsubishi MELSEC and KEYENCE KV PLCs that
support QnA-compatible MC protocol 3E binary frames over TCP.

> [!WARNING]
> MC protocol traffic is not encrypted or authenticated. Use this library only
> on a trusted, isolated control network or a properly secured VPN. Never expose
> a PLC port directly to the Internet. Writes can affect connected equipment.

## Features

- Mitsubishi `D` word and `M` bit batch read/write
- KEYENCE `DM` word and `MR` bit batch read/write
- CPU model, status, selected diagnostics, and system summary
- Read-only vendor and port detection using CPU model command `0101/0000`
- Instance and static APIs
- Injectable transport for testing
- `netstandard2.0` and `net8.0` targets
- No runtime NuGet dependencies

## Install

After the package ID and version are published:

```console
dotnet add package Mc3E.Client --version 1.0.0
```

## Read and write

```csharp
using Mc3E;

var kv = new Mc3EClient("192.168.0.10", PlcVendor.Keyence, 5000);

ushort[] words = kv.ReadWords("DM", 60000, 11);
bool[] bits = kv.ReadBits("MR", 60000, 2);

kv.WriteWords("DM", 60002, new ushort[] { 12345 });
kv.WriteBits("MR", 60000, new[] { true, false });

var q = new Mc3EClient("192.168.0.20", PlcVendor.Mitsubishi, 5002);
ushort[] d = q.ReadWords("D", 100, 3);
bool[] m = q.ReadBits("M", 100, 8);
```

Word values are unsigned 16-bit integers. The client intentionally limits one
operation to 256 points as a conservative safety cap; this is not the maximum
defined by MC protocol or every PLC.

KEYENCE `MR` uses word-and-bit notation. Its final two digits must be `00`
through `15`: `MR60015` is followed by `MR60100`, and `MR60016` is invalid.
Mitsubishi `M` uses a linear bit address.

## CPU information and detection

```csharp
var detected = Mc3EClient.DetectPlcAt("192.168.0.10");

var client = new Mc3EClient(
    "192.168.0.10", detected.Vendor, detected.Port);

CpuModelInfo model = client.ReadModel();
CpuStatusInfo status = client.ReadStatus();
CpuDiagnostics diagnostics = client.ReadDiagnostics();
CpuSystemSummary summary = client.ReadSystemSummary();
CpuInformation all = client.ReadCpuInformation();
```

When no port is supplied, detection tries `5000`, `5002`, `1025`, `1026`,
`4999`, and `5010`. This is a convenience list based on common and tested
project configurations, not a list of ports assigned by the MC specification.

For KEYENCE, CR and CM are accessed using the MC special-relay (`SM`) and
special-register (`SD`) device codes. For Mitsubishi, the diagnostic/status
implementation reads Q-series-compatible SM/SD areas. Verify special-device
meanings against the manual for the target CPU family.

## Static API

```csharp
ushort[] words = Mc3EClient.ReadWordsAt(
    "192.168.0.10", PlcVendor.Keyence, 5000, "DM", 60000, 11);

Mc3EClient.WriteWordsAt(
    "192.168.0.10", PlcVendor.Keyence, 5000, "DM", 60002,
    new ushort[] { 12345 });

bool[] bits = Mc3EClient.ReadBitsAt(
    "192.168.0.20", PlcVendor.Mitsubishi, 5002, "M", 100, 8);

CpuInformation cpu = Mc3EClient.ReadCpuInformationAt(
    "192.168.0.20", PlcVendor.Mitsubishi, 5002);
```

Static methods are also provided for KEYENCE CR/CM and Mitsubishi SM/SD reads.

## Console sample

The repository contains a console application with the same commands as the
Python version:

```console
dotnet run --project samples/Mc3E.Console -- status --ip 192.168.0.10
dotnet run --project samples/Mc3E.Console -- read DM 60000 11 --vendor keyence --ip 192.168.0.10 --port 5000
dotnet run --project samples/Mc3E.Console -- readbit MR 60000 2 --vendor keyence --ip 192.168.0.10 --port 5000
```

The sample supports `read`, `write`, `readbit`, `writebit`, `model`, `status`,
`errors`, `summary`, and `info`. It appends English execution records to
`output/yyyyMMdd_log.txt` under the current directory.

## Build, test, and pack

```console
dotnet restore Mc3E.sln
dotnet test Mc3E.sln -c Release
dotnet pack src/Mc3E/Mc3E.csproj -c Release -o artifacts
```

The package output is `artifacts/Mc3E.Client.1.0.0.nupkg`.

## Supported scope

- QnA-compatible 3E binary frames over TCP
- Direct route: network `0`, PC `FF`, I/O `03FF`, station `0`
- Mitsubishi `D`/`M` and KEYENCE `DM`/`MR` normal devices
- Selected KEYENCE CR/CM and Mitsubishi SM/SD read-only information
- Strict binary 3E response subheader validation (`D0 00`)

ASCII, UDP, 1E/4E frames, routed stations, and other normal device types are
not implemented.

## Hardware verification

- KEYENCE KV-7500: model/status/diagnostic and DM/MR operations
- Mitsubishi Q00UJCPU: model/status/diagnostic and D/M operations

Compatibility with another CPU depends on its MC protocol support, device map,
and Ethernet settings.
