using System.Globalization;
using System.Text.RegularExpressions;

namespace SimATC.Core.Comms;

/// <summary>Lo que el piloto quiere hacer con su mensaje (§8.8 paso 3). Puede haber varias a la vez.</summary>
[Flags]
public enum PilotIntent
{
    None = 0,
    RequestClearance = 1 << 0,
    RequestPushback = 1 << 1,
    RequestStartup = 1 << 2,
    RequestTaxi = 1 << 3,
    ReadyForDeparture = 1 << 4,
    SayAgain = 1 << 5,
    Unable = 1 << 6,
    Acknowledge = 1 << 7, // wilco, roger, copied
    CheckIn = 1 << 8,     // primer contacto en una frecuencia (passing / climbing / with you)
    Affirm = 1 << 9,
    Negative = 1 << 10,
    RadioCheck = 1 << 11,
    // En ruta y llegada (bloque C, ADR-069).
    RequestDirect = 1 << 12,   // "request direct UMKAL"
    RequestDescent = 1 << 13,  // "request descent"
    RequestClimb = 1 << 14,    // "request climb FL360" / "request FL360"
    GoingAround = 1 << 15,     // "going around"
    RunwayVacated = 1 << 16,   // "runway vacated"
    Final = 1 << 17,           // "final runway 17L" / "established ILS"
    RequestRunway = 1 << 18,   // "request runway 17R" / "solicito pista 17R" (ADR-097)
    Confirm = 1 << 19,         // "confirm clearance" / "¿estoy autorizado?" / "sigo esperando" (ADR-132)
}

/// <summary>Los valores y las intenciones de un mensaje del piloto. Los valores que no aparecen quedan en null.</summary>
public sealed record ParsedMessage(string Normalized, PilotIntent Intents)
{
    public bool HasCallsign { get; init; }
    public string? Runway { get; init; }
    public double? AltitudeFt { get; init; }
    public bool AltitudeIsFlightLevel { get; init; }
    public int? Squawk { get; init; }
    public int? FrequencyKhz { get; init; }
    public string? Procedure { get; init; }
    public string? Destination { get; init; }
    public char? AtisLetter { get; init; }
    public int? HeadingDeg { get; init; }
    public int? QnhHpa { get; init; }
    public string? Stand { get; init; }

    /// <summary>Velocidad dicha ("reduce speed to 210 knots", "reduciendo a 210 nudos"), en nudos (ADR-105).</summary>
    public int? SpeedKt { get; init; }

    /// <summary>El fijo de un "direct to" ("request direct UMKAL" → "UMKAL").</summary>
    public string? Fix { get; init; }

    /// <summary>Trae algún valor (una colación lleva al menos uno).</summary>
    public bool HasValues => Runway is not null || AltitudeFt is not null || Squawk is not null || FrequencyKhz is not null
                             || Procedure is not null || Destination is not null || HeadingDeg is not null || QnhHpa is not null
                             || SpeedKt is not null;

    public bool Has(PilotIntent i) => (Intents & i) != 0;
}

/// <summary>
/// Pasos 2 y 3 de la canalización (§8.8): extrae del texto normalizado (<see cref="InputNormalizer"/>) el indicativo, la
/// pista, la altitud o el nivel, el squawk, la frecuencia, el procedimiento, el destino, la letra del ATIS, el rumbo y
/// el QNH, y reconoce las intenciones. Acepta muchas formas de decir lo mismo; no decide nada (eso es del ATC).
/// </summary>
public static partial class MessageParser
{
    /// <param name="callsignForms">Formas del indicativo del piloto ya normalizadas: "lan205", "lan 205", "latam 205"…</param>
    public static ParsedMessage Parse(string text, IReadOnlyCollection<string> callsignForms)
    {
        var n = InputNormalizer.Normalize(text);
        var padded = $" {n} ";
        var hasCallsign = callsignForms.Any(c => c.Length > 0 && padded.Contains($" {c} ", StringComparison.Ordinal));

        var intents = PilotIntent.None;
        void If(bool condition, PilotIntent i)
        {
            if (condition)
                intents |= i;
        }
        // En inglés y en español (§8.6; el texto ya viene sin tildes).
        If(Has(n, "request clearance", "request ifr clearance", "ifr clearance", "request ifr", "clearance to", "request departure clearance",
            "solicito autorizacion", "solicitamos autorizacion", "autorizacion ifr", "solicito plan de vuelo"), PilotIntent.RequestClearance);
        If(Has(n, "request pushback", "request push", "ready for pushback", "ready to push", "request push back", "ready for push",
            "solicito retroceso", "listo para retroceso", "listos para retroceso", "solicito pushback"), PilotIntent.RequestPushback);
        If(Has(n, "request startup", "request start up", "ready for start", "request start",
            "solicito encendido", "solicito puesta en marcha", "solicito encendido de motores"), PilotIntent.RequestStartup);
        If(Has(n, "request taxi", "ready to taxi", "ready for taxi", "taxi please", "solicito rodaje", "listo para rodaje", "listos para rodaje"),
            PilotIntent.RequestTaxi);
        If(Has(n, "ready for departure", "ready for takeoff", "ready for take off", "ready to go", "ready at holding", "ready runway",
            "listo para salida", "listos para salida", "listo para despegue", "listos para despegue", "listo para despegar",
            // Pedidos y faltas de ortografía comunes al escribir rápido (prueba de Marco, 2026-11-05: "pido autorización para despege").
            "request takeoff", "request take off", "requesting takeoff", "listo para despege", "listos para despege",
            "solicito despegue", "solicito despege", "solicito despegar", "solicitamos despegue", "solicitamos despege",
            "solicitud de despegue", "solicitud de despege", "solicitud despegue", "solicito salida", "solicitud de salida",
            "request departure", "requesting departure", "takeoff request", "take off request",
            "pido despegue", "pido despege", "pido autorizacion para despegar", "pido autorizacion para despegue",
            "pido autorizacion para despege", "solicito autorizacion para despegar", "solicito autorizacion para despegue",
            "solicito autorizacion para despege"), PilotIntent.ReadyForDeparture);
        If(Has(n, "say again", "repeat", "come again", "repita"), PilotIntent.SayAgain);
        If(Has(n, "unable", "imposible"), PilotIntent.Unable);
        If(Has(n, "wilco", "roger", "copied", "copy", "give way", "follow", "holding",
            "recibido", "comprendido", "cedo paso", "sigo", "manteniendo", "mantengo", "rodando mas lento",
            "continue taxi", "continuing", "continuo rodaje", "continuando",
            // Información de tráfico (CAP 413 §5.26-5.27): acuse, a la vista o no.
            "traffic in sight", "traffic not sighted", "looking out", "transito a la vista", "trafico a la vista",
            "sin contacto visual", "buscando el transito"), PilotIntent.Acknowledge);
        If(Has(n, "passing", "climbing", "with you", "checking in", "level", "pasando", "ascendiendo", "en contacto", "con usted"), PilotIntent.CheckIn);
        If(Has(n, "affirm", "affirmative", "afirmo", "afirmativo"), PilotIntent.Affirm);
        If(Has(n, "negative", "negativo"), PilotIntent.Negative);
        If(Has(n, "radio check", "how do you read", "prueba de radio", "como me recibe"), PilotIntent.RadioCheck);
        If(Has(n, "request direct", "request direct to", "requesting direct", "solicito directo", "solicitamos directo"), PilotIntent.RequestDirect);
        // El normalizador lleva "descent" a "descend" (ADR-098: "request descent" en inglés nunca se reconocía).
        If(Has(n, "request descent", "request descend", "request lower", "ready for descent", "ready for descend", "solicito descenso",
                "listo para descenso", "listos para descenso"),
            PilotIntent.RequestDescent);
        If(Has(n, "request climb", "request higher", "request flight level", "request fl", "solicito ascenso", "solicito nivel"), PilotIntent.RequestClimb);
        If(Has(n, "going around", "go around", "missed approach", "motor y al aire", "frustrada", "aproximacion frustrada"), PilotIntent.GoingAround);
        If(Has(n, "runway vacated", "vacated", "clear of runway", "pista libre", "pista abandonada", "abandonamos pista"), PilotIntent.RunwayVacated);
        If(Has(n, "final", "established", "on final", "en final", "establecido", "establecidos"), PilotIntent.Final);
        If(Has(n, "request runway", "requesting runway", "solicito pista", "solicitamos pista"), PilotIntent.RequestRunway);
        // "¿Qué tengo autorizado?" (ADR-132): el ATC responde con lo vigente y vuelve a mirar lo que estaba en espera.
        If(Has(n, "confirm clearance", "confirm we are cleared", "confirm cleared", "am i cleared", "are we cleared", "confirm my clearance",
            "still waiting", "still holding", "request status", "confirm instructions",
            "confirme autorizacion", "confirmar autorizacion", "confirme mi autorizacion", "estoy autorizado", "estamos autorizados",
            "tengo autorizacion", "sigo esperando", "seguimos esperando", "sigo en espera", "confirme instrucciones"), PilotIntent.Confirm);

        return new ParsedMessage(n, intents)
        {
            HasCallsign = hasCallsign,
            Runway = Match(RunwayRe(), n),
            AltitudeFt = Altitude(n, out var isFl),
            AltitudeIsFlightLevel = isFl,
            Squawk = Match(SquawkRe(), n) is { } sq ? int.Parse(sq, CultureInfo.InvariantCulture) : null,
            FrequencyKhz = Match(FrequencyRe(), n) is { } f ? (int)Math.Round(double.Parse(f, CultureInfo.InvariantCulture) * 1000) : null,
            Procedure = Match(ProcedureRe(), n)?.ToUpperInvariant(),
            Destination = Match(DestinationRe(), n)?.ToUpperInvariant(),
            AtisLetter = Match(AtisRe(), n) is { Length: 1 } a ? char.ToUpperInvariant(a[0]) : null,
            HeadingDeg = Match(HeadingRe(), n) is { } h ? int.Parse(h, CultureInfo.InvariantCulture) : null,
            QnhHpa = Match(QnhRe(), n) is { } q ? int.Parse(q, CultureInfo.InvariantCulture) : null,
            Stand = Match(StandRe(), n)?.ToUpperInvariant(),
            Fix = Match(DirectRe(), n)?.ToUpperInvariant(),
            SpeedKt = SpeedRe().Match(n) is { Success: true } sp
                ? int.Parse((sp.Groups[1].Success ? sp.Groups[1] : sp.Groups[2]).Value, CultureInfo.InvariantCulture)
                : null,
        };
    }

    private static bool Has(string n, params string[] phrases)
    {
        var padded = $" {n} ";
        return phrases.Any(p => padded.Contains($" {p} ", StringComparison.Ordinal));
    }

    private static string? Match(Regex re, string n) => re.Match(n) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>"flight level 240" / "fl240" / "fl 240" → 24000 (nivel); "climb 5000" / "5000 feet" / "altitude 5000" → 5000.</summary>
    private static double? Altitude(string n, out bool isFlightLevel)
    {
        isFlightLevel = false;
        if (FlightLevelRe().Match(n) is { Success: true } fl)
        {
            isFlightLevel = true;
            return int.Parse(fl.Groups[1].Value, CultureInfo.InvariantCulture) * 100.0;
        }
        if (AltitudeRe().Match(n) is { Success: true } alt)
            return double.Parse((alt.Groups[1].Success ? alt.Groups[1] : alt.Groups[2]).Value, CultureInfo.InvariantCulture);
        return null;
    }

    [GeneratedRegex(@"\b(?:runway|rwy|pista)\s*(\d{1,2}[lrc]?)\b")]
    private static partial Regex RunwayRe();

    [GeneratedRegex(@"\b(?:squawk|squawking|transponder|transpondedor|code|codigo)\s*([0-7]{4})\b")]
    private static partial Regex SquawkRe();

    [GeneratedRegex(@"\b(1[1-3]\d\.\d{1,3})\b")]
    private static partial Regex FrequencyRe();

    // SID/STAR: 2-5 letras, una cifra y quizá una letra (GELU5B, ALBA7C, AMB5), sin confundirse con indicativos (LAN205).
    [GeneratedRegex(@"\b([a-z]{2,5}\d[a-z]?)\b(?:\s*(?:departure|arrival|salida|llegada))?")]
    private static partial Regex ProcedureRe();

    // Destino: 4 letras después de "to"/"cleared", que no sean palabras comunes ("cleared to land", "line up").
    [GeneratedRegex(@"\b(?:to|destination|cleared|autorizado\s+a|autorizacion\s+(?:ifr\s+)?a|ifr\s+a|destino|hasta)\s+(?!land\b|line\b|taxi\b|wait\b|push\b|hold\b|stop\b|left\b|turn\b|pista\b|despegar\b)([a-z]{4})\b")]
    private static partial Regex DestinationRe();

    [GeneratedRegex(@"\b(?:information|info|atis|informacion)\s+([a-z])\b")]
    private static partial Regex AtisRe();

    [GeneratedRegex(@"\b(?:flight\s*level|fl|nivel\s+de\s+vuelo|nivel)\s*(\d{2,3})\b")]
    private static partial Regex FlightLevelRe();

    [GeneratedRegex(@"\b(?:climb|climbing|descend|descending|maintain|altitude|passing|ascienda|ascendiendo|asciendo|descienda|descendiendo|mantenga|manteniendo|altitud|pasando)\s+(?:to\s+|a\s+)?(\d{3,5})\b(?!\.)|\b(\d{3,5})\s*(?:feet|ft|pies)\b")]
    private static partial Regex AltitudeRe();

    // "speed 210" / "velocidad a 210" / "210 knots" / "210 nudos" (ADR-105).
    [GeneratedRegex(@"\b(?:speed|velocidad)\s+(?:to\s+|a\s+)?(\d{3})\b|\b(\d{3})\s*(?:knots|knot|kts|kt|nudos|nudo)\b")]
    private static partial Regex SpeedRe();

    [GeneratedRegex(@"\b(?:heading|rumbo)\s+(\d{3})\b")]
    private static partial Regex HeadingRe();

    [GeneratedRegex(@"\bqnh\s+(\d{3,4})\b")]
    private static partial Regex QnhRe();

    [GeneratedRegex(@"\b(?:stand|gate|parking|puesto|estacionamiento)\s+([a-z]?\d{1,3}[a-z]?)\b")]
    private static partial Regex StandRe();

    // "direct UMKAL", "direct to umkal", "directo a umkal": un fijo de 2-5 letras (o un VOR de 3).
    [GeneratedRegex(@"\b(?:direct|directo)\s+(?:to\s+|a\s+)?([a-z]{2,5})\b")]
    private static partial Regex DirectRe();
}
