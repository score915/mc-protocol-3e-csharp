using System.Collections.Generic;

namespace Mc3E;

/// <summary>PLC vendor conventions supported by this package.</summary>
public enum PlcVendor
{
    Mitsubishi,
    Keyence
}

/// <summary>CPU model name and numeric model code returned by command 0101.</summary>
public sealed class CpuModelInfo
{
    public CpuModelInfo(string name, ushort code) { Name = name; Code = code; }
    public string Name { get; }
    public ushort Code { get; }
}

/// <summary>Decoded CPU operating state.</summary>
public sealed class CpuStatusInfo
{
    public CpuStatusInfo(ushort raw, string state, string stopPauseCause)
    { Raw = raw; State = state; StopPauseCause = stopPauseCause; }
    public ushort Raw { get; }
    public string State { get; }
    public string StopPauseCause { get; }
}

/// <summary>Vendor-specific diagnostic values.</summary>
public sealed class CpuDiagnostics
{
    public bool? SM0 { get; internal set; }
    public bool? SM1 { get; internal set; }
    public ushort? SD0 { get; internal set; }
    public bool? CR2012 { get; internal set; }
    public bool? CR3500 { get; internal set; }
    public IReadOnlyList<ushort> CM5150To5176 { get; internal set; } = new ushort[0];
}

/// <summary>Vendor-specific system values and decoded status.</summary>
public sealed class CpuSystemSummary
{
    public IReadOnlyDictionary<string, bool> Bits { get; internal set; }
        = new Dictionary<string, bool>();
    public IReadOnlyDictionary<string, ushort> Words { get; internal set; }
        = new Dictionary<string, ushort>();
    public string Switch { get; internal set; } = "not available";
    public CpuStatusInfo Status { get; internal set; }
        = new CpuStatusInfo(0, "UNKNOWN", "not available");
}

/// <summary>Combined read-only CPU information.</summary>
public sealed class CpuInformation
{
    public CpuInformation(CpuModelInfo model, CpuDiagnostics diagnostics,
        CpuSystemSummary system)
    { Model = model; Diagnostics = diagnostics; System = system; }
    public CpuModelInfo Model { get; }
    public CpuDiagnostics Diagnostics { get; }
    public CpuSystemSummary System { get; }
}

/// <summary>Result of read-only PLC model and port detection.</summary>
public sealed class PlcDetectionResult
{
    public PlcDetectionResult(PlcVendor vendor, int port, CpuModelInfo model)
    { Vendor = vendor; Port = port; Model = model; }
    public PlcVendor Vendor { get; }
    public int Port { get; }
    public CpuModelInfo Model { get; }
}
