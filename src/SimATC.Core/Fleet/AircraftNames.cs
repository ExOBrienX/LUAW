using SimATC.Core.Comms;

namespace SimATC.Core.Fleet;

/// <summary>
/// Cómo se nombra un avión por radio al describirlo como tráfico: el fabricante y la familia del tipo OACI (designadores
/// de OACI Doc 8643): "give way to the Boeing 787", "follow the Airbus A320" (prueba de Marco, 2026-11-01: "B 787" no se
/// entendía). Si el tipo no está en la tabla, el designador tal cual.
/// </summary>
public static class AircraftNames
{
    private static readonly Dictionary<string, string> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A318"] = "Airbus A318",
        ["A319"] = "Airbus A319", ["A19N"] = "Airbus A319",
        ["A320"] = "Airbus A320", ["A20N"] = "Airbus A320",
        ["A321"] = "Airbus A321", ["A21N"] = "Airbus A321",
        ["A332"] = "Airbus A330", ["A333"] = "Airbus A330", ["A338"] = "Airbus A330", ["A339"] = "Airbus A330",
        ["A342"] = "Airbus A340", ["A343"] = "Airbus A340", ["A345"] = "Airbus A340", ["A346"] = "Airbus A340",
        ["A359"] = "Airbus A350", ["A35K"] = "Airbus A350",
        ["A388"] = "Airbus A380",
        ["B736"] = "Boeing 737", ["B737"] = "Boeing 737", ["B738"] = "Boeing 737", ["B739"] = "Boeing 737", ["B37M"] = "Boeing 737", ["B38M"] = "Boeing 737",
        ["B39M"] = "Boeing 737", ["B3XM"] = "Boeing 737",
        ["B744"] = "Boeing 747", ["B748"] = "Boeing 747",
        ["B752"] = "Boeing 757", ["B753"] = "Boeing 757",
        ["B762"] = "Boeing 767", ["B763"] = "Boeing 767", ["B764"] = "Boeing 767",
        ["B772"] = "Boeing 777", ["B773"] = "Boeing 777", ["B77L"] = "Boeing 777", ["B77W"] = "Boeing 777", ["B778"] = "Boeing 777", ["B779"] = "Boeing 777",
        ["B788"] = "Boeing 787", ["B789"] = "Boeing 787", ["B78X"] = "Boeing 787",
        ["E170"] = "Embraer 170", ["E75L"] = "Embraer 175", ["E75S"] = "Embraer 175", ["E190"] = "Embraer 190", ["E195"] = "Embraer 195", ["E290"] = "Embraer 190",
        ["E295"] = "Embraer 195",
        ["AT43"] = "ATR 42", ["AT45"] = "ATR 42", ["AT46"] = "ATR 42", ["AT72"] = "ATR 72", ["AT75"] = "ATR 72", ["AT76"] = "ATR 72",
        ["CRJ7"] = "Bombardier CRJ", ["CRJ9"] = "Bombardier CRJ", ["CRJX"] = "Bombardier CRJ",
    };

    /// <summary>"A20N" → "Airbus A320"; "B38M" → "Boeing 737"; desconocido → el designador; vacío → "aircraft" / "aeronave".</summary>
    public static string Spoken(string? designator, AtcLanguage lang = AtcLanguage.English) =>
        string.IsNullOrWhiteSpace(designator) ? lang == AtcLanguage.Spanish ? "aeronave" : "aircraft"
        : Families.TryGetValue(designator.Trim(), out var family) ? family
        : designator.Trim().ToUpperInvariant();
}
