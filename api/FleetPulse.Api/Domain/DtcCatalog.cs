namespace FleetPulse.Api.Domain;

public enum TruckSystem { Cooling, Fuel, Lubrication, Electrical, Other }

/// <summary>Descriptions, systems and workshop actions for the SPNs/FMIs FleetPulse reports (SAE J1939-71/-73).</summary>
public static class DtcCatalog
{
    private static readonly Dictionary<uint, (string Name, TruckSystem System, bool Major)> Spns = new()
    {
        [84] = ("Wheel-based vehicle speed", TruckSystem.Other, false),
        [94] = ("Engine fuel delivery pressure", TruckSystem.Fuel, true),
        [100] = ("Engine oil pressure", TruckSystem.Lubrication, true),
        [110] = ("Engine coolant temperature", TruckSystem.Cooling, true),
        [111] = ("Engine coolant level", TruckSystem.Cooling, true),
        [168] = ("Battery potential / power input", TruckSystem.Electrical, false),
        [183] = ("Engine fuel rate", TruckSystem.Fuel, false),
        [190] = ("Engine speed", TruckSystem.Other, false),
    };

    private static readonly string[] Fmis =
    {
        "Data valid but above normal operating range - most severe",       // 0
        "Data valid but below normal operating range - most severe",       // 1
        "Data erratic, intermittent or incorrect",                         // 2
        "Voltage above normal, or shorted to high source",                 // 3
        "Voltage below normal, or shorted to low source",                  // 4
        "Current below normal or open circuit",                            // 5
        "Current above normal or grounded circuit",                        // 6
        "Mechanical system not responding or out of adjustment",           // 7
        "Abnormal frequency or pulse width or period",                     // 8
        "Abnormal update rate",                                            // 9
        "Abnormal rate of change",                                         // 10
        "Root cause not known",                                            // 11
        "Bad intelligent device or component",                             // 12
        "Out of calibration",                                              // 13
        "Special instructions",                                            // 14
        "Data valid but above normal operating range - least severe",      // 15
        "Data valid but above normal operating range - moderately severe", // 16
        "Data valid but below normal operating range - least severe",      // 17
        "Data valid but below normal operating range - moderately severe", // 18
        "Received network data in error",                                  // 19
    };

    public static string SpnName(uint spn) => Spns.TryGetValue(spn, out var s) ? s.Name : $"SPN {spn}";

    public static string FmiText(byte fmi) =>
        fmi < Fmis.Length ? Fmis[fmi] : fmi == 31 ? "Condition exists" : "Reserved";

    public static TruckSystem SystemOf(uint spn) => Spns.TryGetValue(spn, out var s) ? s.System : TruckSystem.Other;

    /// <summary>Major DTCs take a truck off the road (used as the maintenance label).</summary>
    public static bool IsMajor(uint spn) => Spns.TryGetValue(spn, out var s) && s.Major;

    public static string Action(TruckSystem system) => system switch
    {
        TruckSystem.Cooling => "Inspect fan clutch, radiator and coolant level; pressure-test the cooling system",
        TruckSystem.Fuel => "Replace fuel filters; check lift pump and fuel delivery pressure",
        TruckSystem.Lubrication => "Check oil level and pressure sender; inspect oil pump",
        TruckSystem.Electrical => "Test batteries, alternator output and ground connections",
        _ => "Run full diagnostics at next service",
    };

    public static TruckSystem ParseSystem(string driver) =>
        Enum.TryParse<TruckSystem>(driver, ignoreCase: true, out var s) ? s : TruckSystem.Other;
}
