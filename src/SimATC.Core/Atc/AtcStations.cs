using System.Globalization;
using SimATC.Core.Airports;
using SimATC.Core.Comms;

namespace SimATC.Core.Atc;

/// <summary>Una dependencia con la que se habla: su rol, su distintivo en inglés OACI y su frecuencia principal.</summary>
public sealed record AtcStation(AtcRole Role, string Callsign, int Khz)
{
    public string FrequencyText => RadioFormat.Frequency(Khz);
}

/// <summary>
/// Las dependencias de un aeropuerto (RF-22). Primero el archivo ATC (<c>data/atc</c>, del AIP); si no hay, las
/// frecuencias que trae el simulador (nombre + sufijo OACI: DELIVERY, GROUND, TOWER, APPROACH, DEPARTURE, CONTROL).
/// Si falta una dependencia, la cubre la superior: Delivery → Ground → Tower; Departure → Approach → Center.
/// Nunca se usan las frecuencias marcadas sin verificar.
/// </summary>
public sealed class AtcStations
{
    private readonly Dictionary<AtcRole, AtcStation> _own = [];

    /// <param name="language">Con español (§8.6), el distintivo local del AIP ("Santiago Torre"); sin él, el nombre con el sufijo en español.</param>
    public AtcStations(AirportData airport, AirportAtcData? atc, AtcLanguage language = AtcLanguage.English)
    {
        var spanish = language == AtcLanguage.Spanish;
        if (atc is not null)
        {
            foreach (var p in atc.Positions)
            {
                if (p.FrequenciesKhz.Count > 0)
                    _own[p.Role] = new AtcStation(p.Role, spanish ? p.CallsignLocal ?? $"{FirstWord(p.CallsignEn)} {SuffixEs(p.Role)}" : p.CallsignEn,
                        p.FrequenciesKhz[0]);
            }
        }
        foreach (var f in airport.Frequencies)
        {
            if (RoleOf(f.Type) is not { } role || _own.ContainsKey(role))
                continue;
            _own[role] = new AtcStation(role, $"{Title(BaseName(f.Name, airport))} {(spanish ? SuffixEs(role) : Suffix(role))}", f.Khz);
        }
    }

    private static string FirstWord(string callsign)
    {
        var i = callsign.LastIndexOf(' ');
        return i > 0 ? callsign[..i] : callsign;
    }

    /// <summary>El indicativo de una dependencia con ese nombre base: "Lisboa" + Torre → "Lisboa Tower" / "Lisboa Torre".</summary>
    public static string Callsign(string baseName, AtcRole role, AtcLanguage language) =>
        $"{baseName} {(language == AtcLanguage.Spanish ? SuffixEs(role) : Suffix(role))}";

    /// <summary>Sufijos en español (AIP-CHILE AD 2.18: Autorizaciones, Control Terrestre, Torre, Aproximación).</summary>
    private static string SuffixEs(AtcRole r) => r switch
    {
        AtcRole.Delivery => "Autorizaciones",
        AtcRole.Ground => "Control Terrestre",
        AtcRole.Tower => "Torre",
        AtcRole.Departure => "Salida",
        AtcRole.Approach => "Aproximación",
        _ => "Control",
    };

    /// <summary>La dependencia que atiende ese rol (la propia o la que la cubre); null si el aeropuerto no tiene ninguna.</summary>
    public AtcStation? For(AtcRole role)
    {
        foreach (var r in Chain(role))
        {
            if (_own.TryGetValue(r, out var s))
                return s;
        }
        return null;
    }

    /// <summary>La dependencia en esa frecuencia (para saber a quién le habla el piloto según su COM1), o null.</summary>
    public AtcStation? OnFrequency(int khz) => _own.Values.FirstOrDefault(s => Math.Abs(s.Khz - khz) <= 2);

    public IReadOnlyCollection<AtcStation> All => _own.Values;

    private static IEnumerable<AtcRole> Chain(AtcRole role) => role switch
    {
        AtcRole.Delivery => [AtcRole.Delivery, AtcRole.Ground, AtcRole.Tower],
        AtcRole.Ground => [AtcRole.Ground, AtcRole.Tower],
        AtcRole.Tower => [AtcRole.Tower],
        AtcRole.Departure => [AtcRole.Departure, AtcRole.Approach, AtcRole.Center],
        AtcRole.Approach => [AtcRole.Approach, AtcRole.Center],
        _ => [role],
    };

    private static AtcRole? RoleOf(FrequencyType t) => t switch
    {
        FrequencyType.Clearance or FrequencyType.ClearancePreTaxi or FrequencyType.RemoteClearanceDelivery => AtcRole.Delivery,
        FrequencyType.Ground => AtcRole.Ground,
        FrequencyType.Tower => AtcRole.Tower,
        FrequencyType.Departure => AtcRole.Departure,
        FrequencyType.Approach => AtcRole.Approach,
        FrequencyType.Center => AtcRole.Center,
        _ => null,
    };

    private static string Suffix(AtcRole r) => r switch
    {
        AtcRole.Delivery => "Delivery",
        AtcRole.Ground => "Ground",
        AtcRole.Tower => "Tower",
        AtcRole.Departure => "Departure",
        AtcRole.Approach => "Approach",
        _ => "Control",
    };

    /// <summary>"SANTIAGO GROUND EAST" → "SANTIAGO": el nombre sin la función; si viene vacío, el nombre del aeropuerto.</summary>
    private static string BaseName(string name, AirportData airport)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(w => w is not ("GROUND" or "TOWER" or "DELIVERY" or "CLEARANCE" or "APPROACH" or "DEPARTURE" or "CONTROL"
                or "RADAR" or "CENTER" or "CENTRE"))
            .ToList();
        return words.Count > 0 ? string.Join(' ', words) : airport.Name;
    }

    private static string Title(string s) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
}

/// <summary>Cómo se escriben en la radio las frecuencias, los niveles y las altitudes (§8.2, forma escrita).</summary>
public static class RadioFormat
{
    /// <summary>118100 → "118.1"; 118350 → "118.35"; 121900 → "121.9".</summary>
    public static string Frequency(int khz) => (khz / 1000.0).ToString("0.0##", CultureInfo.InvariantCulture);

    /// <summary>Por encima de la altitud de transición, nivel ("flight level 100"); si no, altitud ("5000 feet"). En el idioma del ATC.</summary>
    public static string Level(double ft, double? transitionAltitudeFt, AtcLanguage lang = AtcLanguage.English) =>
        Level(ft, ft > (transitionAltitudeFt ?? 18000), lang);

    public static string Level(double ft, bool isFlightLevel, AtcLanguage lang = AtcLanguage.English) => AtcWords.Level(ft, isFlightLevel, lang);
}
