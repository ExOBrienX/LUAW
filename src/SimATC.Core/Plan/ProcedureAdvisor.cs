using SimATC.Core.Airports;
using SimATC.Core.Geo;
using SimATC.Core.Units;

using SimATC.Core.Text;

namespace SimATC.Core.Plan;

/// <summary>Evaluación de un procedimiento para el plan: a favor, en contra y notas (todo con datos del simulador y del plan).</summary>
public sealed record ProcedureAdvice(string Name, bool Valid, IReadOnlyList<string> Pros, IReadOnlyList<string> Cons,
    IReadOnlyList<string> Notes, double LengthNm)
{
    public bool Recommended { get; init; }
    public Procedure? Procedure { get; init; }
    public Approach? Approach { get; init; }
    /// <summary>Transición sugerida de la aproximación (la que empalma con la STAR), o null.</summary>
    public string? Transition { get; init; }
}

/// <summary>
/// Recomienda la SID, la STAR y la aproximación para el plan y explica por qué (o por qué no) cada candidata:
/// <list type="bullet">
/// <item>que sirva para la pista planificada (la pista en uso según el viento llega con el ATIS, hito 4d);</item>
/// <item>que empalme con la ruta: la SID termina donde sigue la ruta, la STAR empieza donde termina, la aproximación
///   empieza donde termina la STAR;</item>
/// <item>que sea la que eligió SimBrief (o su equivalente en el simulador);</item>
/// <item>entre las que cumplen, la más corta; en aproximaciones, la de mejor guía (ILS antes que RNAV, LOC, VOR…).</item>
/// </list>
/// </summary>
public static class ProcedureAdvisor
{
    /// <summary>Punto donde la SID entrega a la ruta: el primer fijo del plan que no es de SID/STAR.</summary>
    public static string? ExitFix(FlightPlan plan)
    {
        foreach (var f in plan.Fixes)
        {
            if (!f.IsPseudo && !f.IsSidStar)
                return f.Ident;
        }
        return null;
    }

    /// <summary>Punto donde la ruta entrega a la STAR: el último fijo del plan que no es de SID/STAR ni el destino.</summary>
    public static string? EntryFix(FlightPlan plan)
    {
        for (var i = plan.Fixes.Count - 1; i >= 0; i--)
        {
            var f = plan.Fixes[i];
            if (!f.IsPseudo && !f.IsSidStar && f.Ident != plan.Destination.Icao)
                return f.Ident;
        }
        return null;
    }

    public static IReadOnlyList<ProcedureAdvice> Sids(AirportData origin, FlightPlan plan)
    {
        var runway = plan.Origin.PlannedRunway;
        var exit = ExitFix(plan);
        var list = new List<ProcedureAdvice>();
        var match = ProcedureMatch.Best(ProcedureMatch.Rank(origin.Departures, plan.SidIdent, runway));
        foreach (var sid in origin.Departures)
        {
            var serves = runway is null || ProcedureGeometry.SidFitsRunway(sid, origin, runway); // también las comunes al revés (ADR-117)
            // Para evaluar una que no es de esta pista se usa su primera pista.
            var rwy = serves ? runway : FirstRunway(sid);
            var shape = ProcedureGeometry.Sid(sid, origin, rwy, plan.SidTransition);
            var fixes = Fixes(shape);
            var connects = exit is not null && fixes.Count > 0 && fixes[^1] == exit;
            var passes = exit is not null && fixes.Contains(exit);
            var isPlan = match is not null && ReferenceEquals(match.Procedure, sid);
            if (!connects && !passes && !isPlan)
                continue; // no tiene relación con la ruta: no se lista
            list.Add(Evaluate(sid.Name, shape, serves, runway, sid, connects, passes, exit, isPlan, plan.SidIdent, Tr.T("termina"), Tr.T("sigue"))
                with { Procedure = sid });
        }
        return Rank(list);
    }

    public static IReadOnlyList<ProcedureAdvice> Stars(AirportData dest, FlightPlan plan)
    {
        var runway = plan.Destination.PlannedRunway;
        var entry = EntryFix(plan);
        var list = new List<ProcedureAdvice>();
        var match = ProcedureMatch.Best(ProcedureMatch.Rank(dest.Arrivals, plan.StarIdent, runway));
        foreach (var star in dest.Arrivals)
        {
            var serves = runway is null || star.RunwayTransitions.Count == 0 || ProcedureMatch.ServesRunway(star, runway);
            var rwy = serves ? runway : FirstRunway(star);
            var shape = ProcedureGeometry.Star(star, dest, rwy, plan.StarTransition);
            var fixes = Fixes(shape);
            var connects = entry is not null && fixes.Count > 0 && fixes[0] == entry;
            var passes = entry is not null && fixes.Contains(entry);
            var isPlan = match is not null && ReferenceEquals(match.Procedure, star);
            if (!connects && !passes && !isPlan)
                continue;
            list.Add(Evaluate(star.Name, shape, serves, runway, star, connects, passes, entry, isPlan, plan.StarIdent, Tr.T("empieza"), Tr.T("termina"))
                with { Procedure = star });
        }
        return Rank(list);
    }

    public static IReadOnlyList<ProcedureAdvice> Approaches(AirportData dest, FlightPlan plan, string? starEndFix)
    {
        var runway = plan.Destination.PlannedRunway;
        var list = new List<ProcedureAdvice>();
        foreach (var app in dest.Approaches)
        {
            var pros = new List<string>();
            var cons = new List<string>();
            var notes = new List<string>();
            var appRwy = ProcedureMatch.ApproachRunway(app);
            var serves = runway is null || appRwy == runway;
            if (runway is not null)
            {
                if (serves)
                    pros.Add(Tr.F("es para la pista {0} del plan", runway));
                else
                    cons.Add(Tr.F("es para la {0}, el plan aterriza en la {1}", appRwy, runway));
            }
            // La transición que empieza donde termina la STAR, si hay.
            string? transition = null;
            foreach (var t in app.Transitions)
            {
                if (t.Legs.Count > 0 && t.Legs[0].FixIcao == starEndFix)
                    transition = t.Name;
            }
            if (starEndFix is not null)
            {
                if (transition is not null)
                    pros.Add(Tr.F("empalma con el final de la STAR en {0}", starEndFix));
                else
                    cons.Add(Tr.F("no tiene transición desde {0} (final de la STAR)", starEndFix));
            }
            transition ??= app.Transitions.Count > 0 ? app.Transitions[0].Name : null;
            var guidance = Guidance(app.Type);
            notes.Add(guidance.Text);
            var shape = ProcedureGeometry.Approach(app, dest, transition);
            list.Add(new ProcedureAdvice(ProcedureMatch.ApproachName(app), serves, pros, cons, notes, LengthNm(shape))
            {
                Approach = app,
                Transition = transition,
            });
        }
        // Primero las de la pista que empalman; entre ellas, la de mejor guía.
        list.Sort((a, b) =>
        {
            var c = b.Valid.CompareTo(a.Valid);
            if (c != 0)
                return c;
            c = a.Cons.Count.CompareTo(b.Cons.Count);
            if (c != 0)
                return c;
            c = Guidance(a.Approach!.Type).Rank.CompareTo(Guidance(b.Approach!.Type).Rank);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
        if (list.Count > 0 && list[0].Valid)
            list[0] = list[0] with { Recommended = true };
        return list;
    }

    private static (int Rank, string Text) Guidance(ApproachType t) => t switch
    {
        ApproachType.Ils => (0, Tr.T("ILS: guía lateral y vertical, mínimos más bajos")),
        ApproachType.Rnav or ApproachType.Gps => (1, Tr.T("RNAV: guía por GPS")),
        ApproachType.Localizer or ApproachType.Lda or ApproachType.Sdf => (2, Tr.T("localizador: solo guía lateral")),
        ApproachType.LocalizerBackCourse => (3, Tr.T("localizador de rumbo posterior")),
        ApproachType.VorDme or ApproachType.Vor => (4, Tr.T("VOR: no precisa")),
        _ => (5, Tr.T("NDB u otra: no precisa")),
    };

    private static ProcedureAdvice Evaluate(string name, ProcedureShape shape, bool serves, string? runway, Procedure p, bool connects,
        bool passes, string? fix, bool isPlan, string? planned, string verb, string routeVerb)
    {
        var pros = new List<string>();
        var cons = new List<string>();
        var notes = new List<string>();
        if (runway is not null)
        {
            if (serves)
                pros.Add(Tr.F("es para la pista {0} del plan", runway));
            else
                cons.Add(Tr.F("no es para la {0} (es para la {1})", runway, string.Join("/", p.RunwayTransitions.Select(t => RunwayEnd.FormatIdent(t.RunwayNumber, t.RunwayDesignator)))));
        }
        if (fix is not null)
        {
            if (connects)
                pros.Add(Tr.F("{0} en {1}, donde {2} tu ruta", verb, fix, routeVerb));
            else if (passes)
                notes.Add(Tr.F("pasa por {0}, pero no {1} ahí", fix, verb));
            else
                cons.Add(Tr.F("no pasa por {0} ({1} tu ruta ahí)", fix, routeVerb));
        }
        if (isPlan)
            pros.Add(planned == name ? Tr.T("es la que eligió SimBrief") : Tr.F("equivale a la {0} de SimBrief", planned));
        else if (planned is not null)
            notes.Add(Tr.F("SimBrief eligió la {0}", planned));
        var legs = AllLegs(p);
        if (legs.Any(l => l.Type == LegType.RF))
            notes.Add(Tr.T("RNAV con tramos RF (arcos): requiere RNP"));
        else if (legs.Any(l => l.FixType == "V" || l.FixType == "N"))
            notes.Add(Tr.T("convencional (radioayudas)"));
        if (legs.Any(l => l.Type is LegType.VM or LegType.FM))
            notes.Add(Tr.T("termina en vectores del ATC"));
        return new ProcedureAdvice(name, serves && (fix is null || connects || passes), pros, cons, notes, LengthNm(shape));
    }

    /// <summary>Válidas primero; entre ellas la del plan y después la más corta. La primera válida es la recomendada.</summary>
    private static List<ProcedureAdvice> Rank(List<ProcedureAdvice> list)
    {
        list.Sort((a, b) =>
        {
            var c = b.Valid.CompareTo(a.Valid);
            if (c != 0)
                return c;
            c = b.Pros.Count.CompareTo(a.Pros.Count);
            if (c != 0)
                return c;
            return a.LengthNm.CompareTo(b.LengthNm);
        });
        var valid = list.Where(x => x.Valid).ToList();
        if (valid.Count > 1)
        {
            var shortest = valid.MinBy(x => x.LengthNm)!;
            var i = list.IndexOf(shortest);
            list[i] = shortest with { Pros = [.. shortest.Pros, Tr.T("la más corta de las que sirven")] };
        }
        if (list.Count > 0 && list[0].Valid)
            list[0] = list[0] with { Recommended = true };
        return list;
    }

    private static string? FirstRunway(Procedure p) =>
        p.RunwayTransitions.Count > 0 ? RunwayEnd.FormatIdent(p.RunwayTransitions[0].RunwayNumber, p.RunwayTransitions[0].RunwayDesignator) : null;

    private static List<string> Fixes(ProcedureShape shape) => [.. shape.Waypoints.Where(w => !w.Approximate).Select(w => w.Ident)];

    private static IEnumerable<ProcedureLeg> AllLegs(Procedure p) =>
        p.Legs.Concat(p.RunwayTransitions.SelectMany(t => t.Legs)).Concat(p.EnrouteTransitions.SelectMany(t => t.Legs));

    public static double LengthNm(ProcedureShape shape)
    {
        var m = 0.0;
        foreach (var s in shape.Segments)
        {
            for (var i = 1; i < s.Points.Count; i++)
                m += GeoMath.DistanceM(s.Points[i - 1], s.Points[i]);
        }
        return m / UnitConversions.MetersPerNauticalMile;
    }
}
