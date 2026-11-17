using System.Text.Json;
using System.Text.Json.Serialization;
using SimATC.Core.Atc;
using SimATC.Core.Plan;

namespace SimATC.Core.Comms;

/// <summary>
/// Lo que hace falta para retomar el vuelo del piloto si LUAW se cierra de golpe (prueba de Marco 2026-11-09): el plan,
/// la fase con el ATC, lo autorizado y con quién hablaba. Se guarda mientras hay un vuelo en curso y se borra al cerrar
/// LUAW normalmente o al terminar el vuelo; si al abrir todavía existe, la app se cerró de forma inesperada.
/// </summary>
public sealed record FlightSnapshot(
    FlightPlan Plan,
    PilotPhase Phase,
    ClearanceData? Clearance,
    bool PushedBack,
    AtcStation? Station,
    DateTime SavedUtc)
{
    public double? ClearedFt { get; init; }
    public bool CruiseCleared { get; init; }
    public bool DescentGiven { get; init; }
    public bool ApproachExpected { get; init; }
    public bool ApproachCleared { get; init; }
    public bool LandingCleared { get; init; }
    public string? DirectFix { get; init; }

    // ADR-105: lo que faltaba para retomar bien la llegada (prueba de Marco 2026-11-09 en RJBB).
    /// <summary>La pista ya anunciada ("expect … runway 06R"): si cambia, el ATC avisa la nueva.</summary>
    public string? AnnouncedRunway { get; init; }
    /// <summary>La pista de llegada que se usaba (la app la prefiere a la del plan mientras no haya viento del destino).</summary>
    public string? ArrivalRunway { get; init; }
    public bool MissedApproach { get; init; }
    public bool ReintegrationGiven { get; init; }
    public int? SpeedKt { get; init; }
    public bool ViaStar { get; init; }
    /// <summary>La instrucción que esperaba colación (se vuelve a pedir al retomar), o null.</summary>
    public AtcTransmission? PendingInstruction { get; init; }
    public ExpectedReadback? PendingReadback { get; init; }
    public string? PendingHint { get; init; }
    public AtcStation? HandoffTo { get; init; }
    /// <summary>Los últimos mensajes de la conversación, solo para mostrarlos al retomar (no se usan para deducir nada).</summary>
    public IReadOnlyList<SavedMessage> Chat { get; init; } = [];
}

/// <summary>
/// Un mensaje de la conversación guardado para mostrarlo al retomar: hora, quién, texto, qué es (ATC, piloto, otro avión o
/// aviso: el nombre de su tipo en la interfaz) y su ícono.
/// </summary>
public sealed record SavedMessage(DateTime Utc, string From, string Text, string Kind, string? Avatar);

/// <summary>Guardar y leer el vuelo para retomarlo, y en qué fase seguir según dónde está el avión ahora.</summary>
public static class FlightResume
{
    /// <summary>Más viejo que esto (h), no se ofrece: ya no es el mismo vuelo.</summary>
    public const double MaxAgeH = 12;

    public static string DefaultPath { get; } = Path.Combine(AppPaths.Root, "flight-resume.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(FlightSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

    public static FlightSnapshot? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<FlightSnapshot>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Escribe a un archivo temporal y lo reemplaza: un cierre a mitad de la escritura no deja un archivo roto.</summary>
    public static void Save(string path, FlightSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(snapshot));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>El vuelo guardado, o null si no hay, está roto, es muy viejo o no hay nada que retomar.</summary>
    public static FlightSnapshot? Load(string path, DateTime nowUtc)
    {
        try
        {
            if (!File.Exists(path) || Deserialize(File.ReadAllText(path)) is not { } s)
                return null;
            return (nowUtc - s.SavedUtc).TotalHours > MaxAgeH || s.Phase is PilotPhase.Parked or PilotPhase.Arrived ? null : s;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no se pudo borrar: al abrir se ofrecerá retomar y el piloto puede elegir empezar de nuevo
        }
    }

    /// <summary>
    /// En qué fase seguir: la guardada si todavía calza con el avión, si no la más cercana a donde está. Las fases que
    /// esperaban una colación pasan a la siguiente estable (la instrucción ya se había dado); en tierra con Torre, el piloto
    /// vuelve a avisar que está listo. null: nada que retomar.
    /// </summary>
    public static PilotPhase? Phase(PilotPhase saved, bool onGround, bool atStand, bool pushedBack)
    {
        if (saved is PilotPhase.Parked or PilotPhase.Arrived)
            return null;
        if (saved <= PilotPhase.TakeoffCleared)
        {
            if (!onGround)
                return PilotPhase.Airborne; // despegó mientras LUAW estaba cerrado
            return saved switch
            {
                PilotPhase.ClearanceGiven or PilotPhase.Cleared => PilotPhase.Cleared,
                // En el puesto sin haber empujado: vuelve a pedir el pushback.
                PilotPhase.PushbackApproved => atStand && !pushedBack ? PilotPhase.Cleared : PilotPhase.PushbackApproved,
                PilotPhase.TaxiGiven or PilotPhase.Taxiing => PilotPhase.Taxiing,
                _ => PilotPhase.WithTower,
            };
        }
        if (saved <= PilotPhase.ArrivalTower)
            // Aterrizó mientras LUAW estaba cerrado: con Ground del destino.
            return onGround && saved >= PilotPhase.Arrival ? PilotPhase.Landed : onGround ? null : saved;
        // Landed o TaxiIn: con Ground del destino, que le vuelve a dar el rodaje al puesto.
        return onGround ? PilotPhase.Landed : null;
    }
}
