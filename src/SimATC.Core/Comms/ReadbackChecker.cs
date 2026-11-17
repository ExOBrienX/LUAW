using SimATC.Core.Plan;

namespace SimATC.Core.Comms;

/// <summary>Elementos que el piloto debe colacionar (§8.4).</summary>
public enum ReadbackElement
{
    Destination,
    Sid,
    Runway,
    Level,
    Squawk,
    Frequency,
    Callsign,
    /// <summary>Velocidad asignada (ADR-105).</summary>
    Speed,
    Heading,
}

/// <summary>Nivel de exigencia de las colaciones (RF-33b).</summary>
public enum ReadbackStrictness
{
    /// <summary>Práctica: valores dichos correctos y, de lo obligatorio, solo pista, squawk y frecuencia.</summary>
    Permissive,
    /// <summary>Todos los elementos de §8.4 y el indicativo.</summary>
    Realistic,
}

/// <summary>Lo que el ATC espera oír en la colación: los valores de la instrucción que dio (los que no aplican, en null).</summary>
public sealed record ExpectedReadback(string Callsign)
{
    public string? Destination { get; init; }
    public string? Sid { get; init; }
    public string? Runway { get; init; }
    public double? LevelFt { get; init; }
    public int? Squawk { get; init; }
    public int? FrequencyKhz { get; init; }
    /// <summary>El fijo de un "route direct" (bloque C, ADR-069).</summary>
    public string? Fix { get; init; }
    /// <summary>El puesto de un "taxi to stand" del destino ("11", "C4").</summary>
    public string? Stand { get; init; }
    /// <summary>La velocidad de un "reduce speed to" (kt, ADR-105).</summary>
    public int? SpeedKt { get; init; }
    /// <summary>El rumbo magnético de un vector ("turn left heading 240", ADR-123).</summary>
    public int? HeadingDeg { get; init; }
    /// <summary>
    /// Instrucción sin valores ("make a three sixty turn right"): alguna de estas palabras en la colación basta para que
    /// cuente como colación (ADR-105). En minúsculas, como el texto normalizado.
    /// </summary>
    public IReadOnlyList<string>? Words { get; init; }
}

public enum ReadbackVerdict
{
    /// <summary>Correcta y completa (o abreviada con todo lo obligatorio): el ATC sigue.</summary>
    Correct,
    /// <summary>Falta un elemento obligatorio: "read back {elemento}".</summary>
    Missing,
    /// <summary>Un valor equivocado: "negative, {corrección}".</summary>
    Wrong,
    /// <summary>El piloto pide que repita: el ATC repite la instrucción completa.</summary>
    PilotSaysAgain,
    /// <summary>"Unable": el ATC ofrece una alternativa.</summary>
    Unable,
    /// <summary>Otra solicitud válida: se trata como una solicitud nueva.</summary>
    OtherRequest,
    /// <summary>No se entiende: "say again" (el ATC no repite su mensaje).</summary>
    NotUnderstood,
}

public sealed record ReadbackResult(ReadbackVerdict Verdict, ReadbackElement? Element = null);

/// <summary>
/// Paso 4 de la canalización (§8.8) y tabla de fallbacks (§8.7): compara la colación del piloto con la instrucción.
/// Lo que sobra o está enredado se ignora si están los datos clave. Un valor equivocado gana a uno que falta
/// (es más peligroso). Función pura.
/// </summary>
public static class ReadbackChecker
{
    public static ReadbackResult Check(ParsedMessage m, ExpectedReadback e, ReadbackStrictness strictness)
    {
        if (m.Has(PilotIntent.SayAgain))
            return new(ReadbackVerdict.PilotSaysAgain);
        if (m.Has(PilotIntent.Unable))
            return new(ReadbackVerdict.Unable);
        // Un pedido ("request descent", "request runway 17R", "request direct…") nunca es una colación, aunque traiga
        // valores: es una solicitud nueva (ADR-098; antes "request runway 20" se tomaba como colación con otra pista).
        if (m.Has(PilotIntent.RequestDescent) || m.Has(PilotIntent.RequestClimb) || m.Has(PilotIntent.RequestRunway)
            || m.Has(PilotIntent.RequestDirect) && e.Fix is null)
            return new(ReadbackVerdict.OtherRequest);

        // 1. Valores dichos que no calzan (aunque sea en modo permisivo).
        if (e.Destination is { } dest && m.Destination is { } md && !string.Equals(md, dest, StringComparison.OrdinalIgnoreCase))
            return new(ReadbackVerdict.Wrong, ReadbackElement.Destination);
        if (e.Sid is { } sid && m.Procedure is { } mp && !SameProcedure(mp, sid))
            return new(ReadbackVerdict.Wrong, ReadbackElement.Sid);
        if (e.Runway is { } rwy && m.Runway is { } mr && !string.Equals(mr, rwy, StringComparison.OrdinalIgnoreCase))
            return new(ReadbackVerdict.Wrong, ReadbackElement.Runway);
        if (e.LevelFt is { } lvl && m.AltitudeFt is { } ma && Math.Abs(ma - lvl) > 1)
            return new(ReadbackVerdict.Wrong, ReadbackElement.Level);
        if (e.Squawk is { } sq && m.Squawk is { } ms && ms != sq)
            return new(ReadbackVerdict.Wrong, ReadbackElement.Squawk);
        if (e.SpeedKt is { } spd && m.SpeedKt is { } mspd && mspd != spd)
            return new(ReadbackVerdict.Wrong, ReadbackElement.Speed);
        if (e.HeadingDeg is { } hdg && m.HeadingDeg is { } mhdg && mhdg % 360 != hdg % 360)
            return new(ReadbackVerdict.Wrong, ReadbackElement.Heading);
        if (e.FrequencyKhz is { } f && m.FrequencyKhz is { } mf && mf != f)
            return new(ReadbackVerdict.Wrong, ReadbackElement.Frequency);
        // Otro fijo en un "route direct": el ATC repite la instrucción (sin frase de corrección propia en §8).
        if (e.Fix is { } fix && m.Fix is { } mfix && !string.Equals(mfix, fix, StringComparison.OrdinalIgnoreCase))
            return new(ReadbackVerdict.PilotSaysAgain);
        if (e.Stand is { } stand && m.Stand is { } mstand && !string.Equals(mstand, stand, StringComparison.OrdinalIgnoreCase))
            return new(ReadbackVerdict.PilotSaysAgain);

        // 2. Ni valores ni algo reconocible: otra solicitud, un acuse corto, o no se entiende.
        var fixReadback = e.Fix is not null && m.Fix is not null && !m.Has(PilotIntent.RequestDirect)
                          || e.Stand is not null && m.Stand is not null;
        var wordsReadback = e.Words is { } words && words.Any(w => $" {m.Normalized} ".Contains($" {w} ", StringComparison.Ordinal));
        if (!m.HasValues && !fixReadback && !wordsReadback)
        {
            if (m.Intents is not PilotIntent.None and not PilotIntent.Acknowledge && !m.Has(PilotIntent.Acknowledge))
                return new(ReadbackVerdict.OtherRequest);
            if (!m.Has(PilotIntent.Acknowledge))
                return new(ReadbackVerdict.NotUnderstood);
        }

        // 3. Lo que falta, según la exigencia (RF-33b). Primero lo de seguridad: pista, squawk, frecuencia.
        if (e.Runway is not null && m.Runway is null)
            return new(ReadbackVerdict.Missing, ReadbackElement.Runway);
        if (e.Squawk is not null && m.Squawk is null)
            return new(ReadbackVerdict.Missing, ReadbackElement.Squawk);
        if (e.FrequencyKhz is not null && m.FrequencyKhz is null)
            return new(ReadbackVerdict.Missing, ReadbackElement.Frequency);
        if (e.Fix is not null && m.Fix is null || e.Stand is not null && m.Stand is null)
            return new(ReadbackVerdict.PilotSaysAgain);
        if (strictness == ReadbackStrictness.Realistic)
        {
            if (e.LevelFt is not null && m.AltitudeFt is null)
                return new(ReadbackVerdict.Missing, ReadbackElement.Level);
            if (e.SpeedKt is not null && m.SpeedKt is null)
                return new(ReadbackVerdict.Missing, ReadbackElement.Speed);
            if (e.HeadingDeg is not null && m.HeadingDeg is null)
                return new(ReadbackVerdict.Missing, ReadbackElement.Heading);
            if (e.Sid is { } sidSaid && m.Procedure is null && !SaysProcedureWithoutNumber(m, sidSaid))
                return new(ReadbackVerdict.Missing, ReadbackElement.Sid);
            if (e.Destination is not null && m.Destination is null)
                return new(ReadbackVerdict.Missing, ReadbackElement.Destination);
            if (!m.HasCallsign)
                return new(ReadbackVerdict.Missing, ReadbackElement.Callsign);
        }
        return new(ReadbackVerdict.Correct);
    }

    /// <summary>
    /// Mismo procedimiento aunque uno use el nombre largo del punto y el otro el de 6 caracteres (GELUS5B / GELU5B):
    /// mismo número, misma letra y un punto base que empieza igual.
    /// </summary>
    /// <summary>
    /// Procedimientos sin número (STAR "CANDYA" de RJBB, prueba de Marco 2026-11-09): el parser solo reconoce nombres con
    /// cifra (GELU5B), así que se busca el nombre esperado como palabra en el texto.
    /// </summary>
    private static bool SaysProcedureWithoutNumber(ParsedMessage m, string procedure)
    {
        foreach (var ch in procedure)
        {
            if (char.IsDigit(ch))
                return false;
        }
        var name = procedure.ToLowerInvariant();
        var text = m.Normalized;
        for (var i = text.IndexOf(name, StringComparison.Ordinal); i >= 0; i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
        {
            var end = i + name.Length;
            if ((i == 0 || !char.IsLetterOrDigit(text[i - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end])))
                return true;
        }
        return false;
    }

    public static bool SameProcedure(string a, string b)
    {
        var x = ProcedureMatch.Split(a.ToUpperInvariant());
        var y = ProcedureMatch.Split(b.ToUpperInvariant());
        return x.Number == y.Number && x.Suffix == y.Suffix && x.Base.Length >= 2 && y.Base.Length >= 2
               && (x.Base.StartsWith(y.Base, StringComparison.Ordinal) || y.Base.StartsWith(x.Base, StringComparison.Ordinal));
    }
}
