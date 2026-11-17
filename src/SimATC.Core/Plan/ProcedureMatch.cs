using SimATC.Core.Airports;
using SimATC.Core.Geo;

using SimATC.Core.Text;

namespace SimATC.Core.Plan;

/// <summary>Un candidato del simulador para el procedimiento del plan, con su parecido (0–100) y el porqué.</summary>
public sealed record ProcedureCandidate(Procedure Procedure, int Score, string Reason)
{
    public string Name => Procedure.Name;
}

/// <summary>Un punto del trazado de un procedimiento (para el radar).</summary>
public sealed record ProcedurePoint(string Ident, GeoPoint Position);

/// <summary>
/// Busca entre las SID/STAR del simulador la que corresponde a la del plan de SimBrief. Los nombres pueden diferir: la
/// revisión cambia con el AIRAC (ARUN1A → ARUN2A), y SimBrief a veces usa el nombre largo del punto (GELUS5B frente a
/// GELU5B en los datos de 6 caracteres). Se compara por partes: punto base, número de revisión y letra, y se prefiere
/// la que sirve para la pista planificada. Todo sale de los datos del simulador; nada se inventa.
/// </summary>
public static class ProcedureMatch
{
    /// <summary>Por debajo de este puntaje no se propone automáticamente (se elige a mano).</summary>
    public const int MinAutoScore = 50;

    /// <summary>Candidatos ordenados del más parecido al menos (solo los que comparten el punto base).</summary>
    public static IReadOnlyList<ProcedureCandidate> Rank(IReadOnlyList<Procedure> procedures, string? planned, string? runway)
    {
        var result = new List<ProcedureCandidate>();
        if (string.IsNullOrWhiteSpace(planned))
            return result;
        var want = Split(planned.Trim().ToUpperInvariant());
        foreach (var p in procedures)
        {
            var have = Split(p.Name.ToUpperInvariant());
            var (score, reason) = Compare(want, have, planned.Trim().ToUpperInvariant(), p.Name.ToUpperInvariant());
            if (score <= 0)
                continue;
            if (runway is not null && p.RunwayTransitions.Count > 0)
            {
                if (ServesRunway(p, runway))
                {
                    score += 10;
                }
                else
                {
                    score -= 20;
                    reason += Tr.F(", no es para la pista {0}", runway);
                }
            }
            result.Add(new ProcedureCandidate(p, Math.Clamp(score, 0, 100), reason));
        }
        result.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>El mejor candidato si alcanza <see cref="MinAutoScore"/>; si no, null (que elija el piloto).</summary>
    public static ProcedureCandidate? Best(IReadOnlyList<ProcedureCandidate> ranked) =>
        ranked.Count > 0 && ranked[0].Score >= MinAutoScore ? ranked[0] : null;

    public static bool ServesRunway(Procedure p, string runway)
    {
        foreach (var t in p.RunwayTransitions)
        {
            if (RunwayEnd.FormatIdent(t.RunwayNumber, t.RunwayDesignator) == runway)
                return true;
        }
        return false;
    }

    /// <summary>"GELU5B" → ("GELU", "5", "B"); "SABLA8" → ("SABLA", "8", ""); sin número → (nombre, "", "").</summary>
    internal static (string Base, string Number, string Suffix) Split(string name)
    {
        var i = 0;
        while (i < name.Length && !char.IsAsciiDigit(name[i]))
            i++;
        var j = i;
        while (j < name.Length && char.IsAsciiDigit(name[j]))
            j++;
        return (name[..i], name[i..j], name[j..]);
    }

    private static (int Score, string Reason) Compare((string Base, string Number, string Suffix) want,
        (string Base, string Number, string Suffix) have, string wantName, string haveName)
    {
        if (wantName == haveName)
            return (100, Tr.T("igual al plan"));
        int score;
        string reason;
        if (want.Base == have.Base)
        {
            (score, reason) = (50, Tr.T("mismo punto"));
        }
        else if (want.Base.Length >= 3 && have.Base.Length >= 3 && (want.Base.StartsWith(have.Base, StringComparison.Ordinal)
                     || have.Base.StartsWith(want.Base, StringComparison.Ordinal)))
        {
            (score, reason) = (45, Tr.T("mismo punto, nombre abreviado")); // GELUS / GELU
        }
        else if (want.Base.Length == have.Base.Length && want.Base.Length >= 4 && Differences(want.Base, have.Base) == 1)
        {
            (score, reason) = (30, Tr.T("punto con una letra distinta"));
        }
        else
        {
            return (0, "");
        }
        if (want.Number == have.Number)
        {
            score += 25;
        }
        else
        {
            score += 10;
            reason += Tr.T(", otra revisión");
        }
        if (want.Suffix == have.Suffix)
        {
            score += 15;
        }
        else
        {
            reason += Tr.T(", otra letra");
        }
        return (score, reason);
    }

    private static int Differences(string a, string b)
    {
        var n = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                n++;
        }
        return n;
    }

    /// <summary>
    /// Trazado de una SID: tramos de la pista, comunes y de la transición en ruta (si el plan la nombra). Solo los
    /// tramos que terminan en un punto con coordenadas: los de rumbo o altitud (VA, CA, VM…) no tienen posición y no se
    /// dibujan (no se inventa su geometría).
    /// </summary>
    public static IReadOnlyList<ProcedurePoint> SidPath(Procedure sid, string? runway, string? transition)
    {
        var points = new List<ProcedurePoint>();
        if (runway is not null)
            AddLegs(points, RunwayLegs(sid, runway));
        AddLegs(points, sid.Legs);
        if (EnrouteLegs(sid, transition) is { } t)
            AddLegs(points, t);
        return points;
    }

    /// <summary>Trazado de una STAR: transición en ruta (si el plan la nombra), tramos comunes y los de la pista.</summary>
    public static IReadOnlyList<ProcedurePoint> StarPath(Procedure star, string? runway, string? transition)
    {
        var points = new List<ProcedurePoint>();
        if (EnrouteLegs(star, transition) is { } t)
            AddLegs(points, t);
        AddLegs(points, star.Legs);
        if (runway is not null)
            AddLegs(points, RunwayLegs(star, runway));
        return points;
    }

    /// <summary>Trazado de una aproximación: la transición elegida (si hay) y los tramos finales; sin la frustrada.</summary>
    public static IReadOnlyList<ProcedurePoint> ApproachPath(Approach approach, string? transition)
    {
        var points = new List<ProcedurePoint>();
        foreach (var t in approach.Transitions)
        {
            if (t.Name == transition)
                AddLegs(points, t.Legs);
        }
        AddLegs(points, approach.FinalLegs);
        return points;
    }

    /// <summary>Nombre legible: "ILS Z 17L", "RNAV 35R", "VOR/DME 17L".</summary>
    public static string ApproachName(Approach a)
    {
        var type = a.Type switch
        {
            ApproachType.Ils => "ILS",
            ApproachType.Localizer => "LOC",
            ApproachType.Rnav or ApproachType.Gps => "RNAV",
            ApproachType.VorDme => "VOR/DME",
            ApproachType.Vor => "VOR",
            ApproachType.NdbDme => "NDB/DME",
            ApproachType.Ndb => "NDB",
            ApproachType.Lda => "LDA",
            ApproachType.Sdf => "SDF",
            ApproachType.LocalizerBackCourse => "LOC BC",
            _ => "APP",
        };
        var suffix = a.Suffix.Trim();
        return $"{type}{(suffix.Length > 0 ? $" {suffix}" : "")} {RunwayEnd.FormatIdent(a.RunwayNumber, a.RunwayDesignator)}";
    }

    public static string ApproachRunway(Approach a) => RunwayEnd.FormatIdent(a.RunwayNumber, a.RunwayDesignator);

    private static IReadOnlyList<ProcedureLeg> RunwayLegs(Procedure p, string runway)
    {
        foreach (var t in p.RunwayTransitions)
        {
            if (RunwayEnd.FormatIdent(t.RunwayNumber, t.RunwayDesignator) == runway)
                return t.Legs;
        }
        return [];
    }

    private static IReadOnlyList<ProcedureLeg>? EnrouteLegs(Procedure p, string? transition)
    {
        if (transition is null)
            return null;
        foreach (var t in p.EnrouteTransitions)
        {
            if (string.Equals(t.Name, transition, StringComparison.OrdinalIgnoreCase))
                return t.Legs;
        }
        return null;
    }

    private static void AddLegs(List<ProcedurePoint> points, IReadOnlyList<ProcedureLeg> legs)
    {
        foreach (var leg in legs)
        {
            if (leg.FixIcao.Length == 0 || (leg.FixPosition.LatDeg == 0 && leg.FixPosition.LonDeg == 0))
                continue;
            // Un mismo punto al final de un tramo y al inicio del siguiente se dibuja una vez.
            if (points.Count > 0 && points[^1].Ident == leg.FixIcao)
                continue;
            points.Add(new ProcedurePoint(leg.FixIcao, leg.FixPosition));
        }
    }
}
