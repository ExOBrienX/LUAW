using System.Text.Json;
using System.Text.Json.Serialization;

using SimATC.Core.Text;

namespace SimATC.Core.Atc;

/// <summary>Dependencia ATC (RF-22). <c>Departure</c> puede no existir: en SCEL las salidas las toma Approach.</summary>
public enum AtcRole
{
    Delivery,
    Ground,
    Tower,
    Departure,
    Approach,
    Center,
}

/// <summary>De dónde salen los datos (AIP), y si Marco los revisó.</summary>
public sealed record AtcSource(string Title, string Url, string Retrieved, string? Amendment = null);

/// <summary>
/// Una dependencia con sus frecuencias. <c>CallsignEn</c> es el distintivo en inglés OACI (nombre + sufijo estándar:
/// DELIVERY, GROUND, TOWER, APPROACH, DEPARTURE, CONTROL); <c>CallsignLocal</c>, el del AIP en el idioma local.
/// La primera frecuencia es la principal.
/// </summary>
public sealed record AtcPosition(AtcRole Role, string CallsignEn, IReadOnlyList<int> FrequenciesKhz, string? CallsignLocal = null,
    string? Notes = null)
{
    /// <summary>Frecuencias que aparecen en alguna fuente pero no están confirmadas: <b>nunca</b> se usan.</summary>
    public IReadOnlyList<int> UnverifiedFrequenciesKhz { get; init; } = [];
}

/// <summary>Una configuración de pistas: las cabeceras en uso y, si el AIP lo indica, cuáles para salidas y cuáles para llegadas.</summary>
public sealed record RunwayMode(IReadOnlyList<string> Ends, IReadOnlyList<string>? Departure = null, IReadOnlyList<string>? Arrival = null)
{
    /// <summary>false si la asignación de salidas y llegadas es supuesta (se usa, pero se avisa en el log).</summary>
    public bool Verified { get; init; } = true;
    public string? Notes { get; init; }
}

/// <summary>Una fila de la tabla de nivel de transición: QNH (hPa) de <c>From</c> a <c>To</c>, inclusive, da el nivel <c>Fl</c>.</summary>
public sealed record TransitionLevelRow(double From, double To, int Fl);

/// <summary>
/// Nivel de transición según el QNH (Chile: AIP ENR 1.7-4). Se busca con el QNH redondeado a 0,1 hPa; fuera de la
/// tabla se usa el extremo más cercano (y quien llama lo registra en el log).
/// </summary>
public sealed record TransitionLevelTable(IReadOnlyList<TransitionLevelRow> ByQnhHpa, string? Source = null, string? Unit = "FL",
    string? Notes = null)
{
    /// <summary>Nivel de transición en pies para ese QNH; <paramref name="outOfRange"/> si cayó fuera de la tabla.</summary>
    public double? LevelFt(double qnhHpa, out bool outOfRange)
    {
        outOfRange = false;
        if (ByQnhHpa.Count == 0)
            return null;
        var q = Math.Round(qnhHpa, 1, MidpointRounding.AwayFromZero);
        foreach (var row in ByQnhHpa)
        {
            if (q >= row.From - 1e-9 && q <= row.To + 1e-9)
                return row.Fl * 100.0;
        }
        outOfRange = true;
        var nearest = ByQnhHpa.MinBy(r => Math.Min(Math.Abs(q - r.From), Math.Abs(q - r.To)))!;
        return nearest.Fl * 100.0;
    }
}

/// <summary>Nivel que el ATC da en la autorización para una SID (tope de la SID, de la carta).</summary>
public sealed record SidClimbEntry(IReadOnlyList<string>? Runways = null, string? ClearedLevel = null, string? Notes = null)
{
    /// <summary>"FL130" → 13000; "5000" → 5000; null si no hay o no se entiende.</summary>
    public double? ClearedLevelFt => ParseLevel(ClearedLevel);

    public static double? ParseLevel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var t = text.Trim().ToUpperInvariant();
        if (t.StartsWith("FL", StringComparison.Ordinal) && int.TryParse(t[2..], out var fl))
            return fl * 100.0;
        return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ft) ? ft : null;
    }
}

public sealed record SidClimb(IReadOnlyDictionary<string, SidClimbEntry> BySid, string? Notes = null);

/// <summary>
/// Un espacio aéreo (CTR, ATZ, TMA). Si es un círculo, centro y radio; si no, el texto de los límites del AIP (se pasa a
/// polígono cuando una etapa lo use: etapas 6 y 8).
/// </summary>
public sealed record AtcAirspace(string Name, string? Class, AtcRole ControlledBy)
{
    public double? CenterLatDeg { get; init; }
    public double? CenterLonDeg { get; init; }
    public double? RadiusNm { get; init; }
    public double? UpperFt { get; init; }
    /// <summary>"ALT" (sobre el nivel del mar) o "AGL".</summary>
    public string? UpperReference { get; init; }
    public string? LateralLimitsText { get; init; }
}

/// <summary>
/// Datos ATC de un aeropuerto que el simulador no trae (frecuencias de Approach/Center, configuración de pistas, nivel de
/// transición, espacios aéreos), en <c>data/atc/{ICAO}.json</c>. Salen del AIP público y los revisa Marco
/// (<see cref="Reviewed"/>). Es opcional: sin archivo se usan los datos del simulador y las reglas de respaldo (RF-22:
/// si falta una dependencia, la cubre la superior). Valores en las unidades internas: pies, nudos, kHz.
/// </summary>
public sealed record AirportAtcData(string Airport, AtcSource Source, bool Reviewed)
{
    public IReadOnlyList<AtcSource> AdditionalSources { get; init; } = [];
    /// <summary>Lo que falta confirmar (lo mantiene Marco).</summary>
    public IReadOnlyList<string> Pending { get; init; } = [];
    public double? TransitionAltitudeFt { get; init; }
    public TransitionLevelTable? TransitionLevelTable { get; init; }
    public IReadOnlyList<string> PreferredRunways { get; init; } = [];
    /// <summary>Viento de cola aceptado en la pista preferente (SCEL: 10 kt).</summary>
    public double? PreferredMaxTailwindKt { get; init; }
    public IReadOnlyList<RunwayMode> RunwayModes { get; init; } = [];
    public IReadOnlyList<AtcPosition> Positions { get; init; } = [];
    public int? AtisArrivalKhz { get; init; }
    public int? AtisDepartureKhz { get; init; }
    /// <summary>A quién se pasa una salida después del despegue (SCEL: Approach, indicado en la autorización).</summary>
    public AtcRole? DepartureHandoffTo { get; init; }
    public SidClimb? SidClimb { get; init; }
    public IReadOnlyList<AtcAirspace> Airspaces { get; init; } = [];
    public string? Notes { get; init; }

    public AtcPosition? Position(AtcRole role) => Positions.FirstOrDefault(p => p.Role == role);

    /// <summary>El modo de pistas cuyas cabeceras son exactamente esas (en cualquier orden), o null.</summary>
    public RunwayMode? ModeFor(IReadOnlyList<string> ends) =>
        RunwayModes.FirstOrDefault(m => m.Ends.Order().SequenceEqual(ends.Order()));

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string PathFor(string dataDir, string icao) => Path.Combine(dataDir, "atc", $"{icao.ToUpperInvariant()}.json");

    /// <summary>null si no hay archivo para ese aeropuerto (es opcional) o no se puede leer (se avisa por el log).</summary>
    public static AirportAtcData? Load(string path, Action<string>? log = null)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AirportAtcData>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke(Tr.F("Datos ATC ilegibles en {0} ({1}); se usan los del simulador.", path, ex.Message));
            return null;
        }
    }
}
