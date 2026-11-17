using System.Globalization;

namespace SimATC.Core.Atc;

public enum AtisKind
{
    Combined,
    Departure,
    Arrival,
}

/// <summary>Un mensaje ATIS: su letra, el texto escrito y lo que la cambió.</summary>
public sealed record AtisMessage(char Letter, string Text, DateTime TimeUtc, string RunwayLabel, int QnhHpa);

/// <summary>
/// Arma el ATIS (RF-24) con la plantilla de §8.7b, en el orden de OACI Anexo 11 (SERA.9010), omitiendo lo que no se
/// conoce (nubes y punto de rocío). Lleva la letra: avanza cuando cambia algo significativo, y al menos cada hora.
/// Función pura salvo la letra, que es su estado.
/// </summary>
public sealed class AtisBuilder(string aerodromeName, AtisKind kind, char firstLetter = 'A')
{
    public const double MaxAgeS = 3600;

    private char _letter = firstLetter;
    private Snapshot? _last;

    public AtisMessage? Current { get; private set; }

    private sealed record Snapshot(string Runway, int Qnh, double WindMag, double WindKt, bool Gusty, Precipitation Precip,
        double? Tl, DateTime Time);

    /// <summary>
    /// Con una observación nueva. Devuelve true si hay un ATIS nuevo (el primero, o un cambio significativo que avanza
    /// la letra). Si nada cambió, el mensaje queda igual: el texto se arma con la observación que lo originó.
    /// </summary>
    public bool Update(WeatherObservation wx, RunwayInUse runways, IReadOnlyList<string>? departureEnds, double? transitionLevelFt,
        IReadOnlyList<string>? arrivalEnds = null)
    {
        // Pistas de llegada y de salida separadas cuando difieren (ADR-102, bloque D): "landing runway 17L, departure
        // runway 17R" (SERA.9010(b): las pistas que se usan para aterrizar y despegar; CAP 413 cap. 4: "departure runway 32").
        var dep = departureEnds is { Count: > 0 } ? departureEnds : runways.Ends;
        var arr = arrivalEnds is { Count: > 0 } ? arrivalEnds : runways.Ends;
        var split = !dep.Order().SequenceEqual(arr.Order());
        var runwayLabel = kind switch
        {
            AtisKind.Departure => Runways(dep),
            AtisKind.Arrival => Runways(arr),
            _ when split => $"{Runways(arr)} | {Runways(dep)}",
            _ => Runways(runways.Ends),
        };
        var snap = new Snapshot(runwayLabel, (int)Math.Floor(wx.QnhHpa), wx.WindFromMagneticDeg, wx.WindKt, wx.GustKt is not null,
            wx.Precipitation, transitionLevelFt, wx.TimeUtc);
        if (_last is not null && !Significant(_last, snap))
            return false;
        if (_last is not null)
            _letter = _letter == 'Z' ? 'A' : (char)(_letter + 1);
        _last = snap;
        var spoken = kind switch
        {
            AtisKind.Departure => $"departure runway {Runways(dep)}",
            AtisKind.Arrival => $"landing runway {Runways(arr)}",
            _ when split => $"landing runway {Runways(arr)}, departure runway {Runways(dep)}",
            _ => $"runway in use {Runways(runways.Ends)}",
        };
        var chip = kind switch
        {
            AtisKind.Departure => $"↑{string.Join("/", dep)}",
            AtisKind.Arrival => $"↓{string.Join("/", arr)}",
            _ when split => $"↓{string.Join("/", arr)} ↑{string.Join("/", dep)}",
            _ => string.Join("/", runways.Ends),
        };
        Current = new AtisMessage(_letter, Text(wx, spoken, transitionLevelFt), wx.TimeUtc, chip, snap.Qnh);
        return true;
    }

    private static bool Significant(Snapshot a, Snapshot b)
    {
        if (a.Runway != b.Runway || a.Qnh != b.Qnh || a.Gusty != b.Gusty || a.Precip != b.Precip || a.Tl != b.Tl)
            return true;
        if (Math.Abs(a.WindKt - b.WindKt) >= 10)
            return true;
        if (Math.Max(a.WindKt, b.WindKt) >= 10 && Math.Abs(Geo.GeoMath.AngleDiffDeg(a.WindMag, b.WindMag)) >= 30)
            return true;
        return (b.Time - a.Time).TotalSeconds >= MaxAgeS;
    }

    private string Text(WeatherObservation wx, string runwaysSpoken, double? tl)
    {
        var inv = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        var indicator = kind switch { AtisKind.Departure => " departure", AtisKind.Arrival => " arrival", _ => "" };
        parts.Add($"{aerodromeName}{indicator} information {Phonetic(_letter)}");
        parts.Add($"time {wx.TimeUtc.ToString("HHmm", inv)}");
        parts.Add(runwaysSpoken);
        if (tl is { } level)
            parts.Add($"transition level {level / 100:000}");
        parts.Add(Wind(wx));
        // Sin visibilidad: la del simulador es solo "de partículas" y no refleja lluvia ni neblina (ADR-027).
        if (wx.Precipitation != Precipitation.None)
            parts.Add(wx.Precipitation == Precipitation.Rain ? "rain" : "snow");
        parts.Add($"temperature {Math.Round(wx.TemperatureC).ToString("0", inv)}");
        parts.Add($"QNH {Math.Floor(wx.QnhHpa).ToString("0", inv)}");
        parts.Add($"acknowledge receipt of information {Phonetic(_letter)} on initial contact");
        return string.Join(", ", parts);
    }

    public static string Wind(WeatherObservation wx)
    {
        if (wx.WindKt < 1)
            return "wind calm";
        var dir = (int)(Math.Round(wx.WindFromMagneticDeg / 10) * 10) % 360;
        var gust = wx.GustKt is { } g ? $" gusting {Math.Round(g):0}" : "";
        return $"wind {(dir == 0 ? 360 : dir):000} degrees {Math.Round(wx.WindKt):0}{gust} knots";
    }

    private static string Runways(IReadOnlyList<string> ends) => string.Join(" and ", ends);

    /// <summary>Alfabeto OACI para la letra del ATIS.</summary>
    public static string Phonetic(char c) => c switch
    {
        'A' => "Alfa", 'B' => "Bravo", 'C' => "Charlie", 'D' => "Delta", 'E' => "Echo", 'F' => "Foxtrot", 'G' => "Golf",
        'H' => "Hotel", 'I' => "India", 'J' => "Juliett", 'K' => "Kilo", 'L' => "Lima", 'M' => "Mike", 'N' => "November",
        'O' => "Oscar", 'P' => "Papa", 'Q' => "Quebec", 'R' => "Romeo", 'S' => "Sierra", 'T' => "Tango", 'U' => "Uniform",
        'V' => "Victor", 'W' => "Whiskey", 'X' => "X-ray", 'Y' => "Yankee", _ => "Zulu",
    };
}
