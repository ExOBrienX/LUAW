namespace SimATC.Core.Airports;

/// <summary>
/// Cómo se llama un puesto en la interfaz y por radio. Muchos aeropuertos (RKSI: 86 de 189, prueba de Marco 2026-11-09)
/// traen el nombre NONE y solo un número: se nombran por su tipo ("Gate 225", "Parking 412"). La letra de sufijo
/// (GATE_A..GATE_Z usados como sufijo) va detrás del número ("Gate 12L").
/// </summary>
public static class StandNames
{
    /// <summary>"Gate 14", "Gate A12", "N Parking 3", "Parking 412", "Cargo 7", "Gate 12L".</summary>
    public static string Display(Parking p)
    {
        var number = Number(p);
        var name = p.Name.ToString();
        if (Letter(p.Name) is { } letter)
            return $"Gate {letter}{number}"; // GateA..GateZ
        return p.Name switch
        {
            ParkingName.None => $"{ByType(p.Type)} {number}",
            ParkingName.NParking => $"N Parking {number}",
            ParkingName.SParking => $"S Parking {number}",
            ParkingName.EParking => $"E Parking {number}",
            ParkingName.WParking => $"W Parking {number}",
            ParkingName.NeParking => $"NE Parking {number}",
            ParkingName.NwParking => $"NW Parking {number}",
            ParkingName.SeParking => $"SE Parking {number}",
            ParkingName.SwParking => $"SW Parking {number}",
            _ => $"{name} {number}",
        };
    }

    /// <summary>Como se dice por radio: "14", "A12", "12L" (sin la palabra; la plantilla dice "stand"/"puesto").</summary>
    public static string Spoken(Parking p) =>
        Letter(p.Name) is { } letter ? $"{letter}{Number(p)}" : Number(p);

    /// <summary>El número con la letra de sufijo, si tiene ("12", "12L").</summary>
    private static string Number(Parking p) =>
        p.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + (Letter(p.Suffix) is { } s ? s.ToString() : "");

    /// <summary>La letra de GATE_A..GATE_Z, o null.</summary>
    private static char? Letter(ParkingName n) => n is >= ParkingName.GateA and <= ParkingName.GateZ ? (char)('A' + (n - ParkingName.GateA)) : null;

    /// <summary>Nombre por tipo cuando el puesto no trae uno.</summary>
    private static string ByType(ParkingType t) => t switch
    {
        ParkingType.GateSmall or ParkingType.GateMedium or ParkingType.GateHeavy or ParkingType.GateExtra => "Gate",
        ParkingType.RampCargo => "Cargo",
        ParkingType.RampMilCargo or ParkingType.RampMilCombat => "Military",
        ParkingType.DockGa => "Dock",
        ParkingType.Fuel => "Fuel",
        ParkingType.Vehicle => "Vehicle",
        _ => "Parking",
    };
}
