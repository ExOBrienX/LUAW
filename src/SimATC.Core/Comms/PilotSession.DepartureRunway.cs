using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Geo;
using SimATC.Core.Plan;
using SimATC.Core.Taxi;

using SimATC.Core.Text;

namespace SimATC.Core.Comms;

/// <summary>
/// La pista de salida del piloto (ADR-106, pedido de Marco 2026-11-09): la del plan si está activa para salidas; si no, la
/// activa con el rodaje más corto desde donde está. Si su pista deja de usarse para salidas antes de rodar o mientras rueda
/// lejos de ella, Ground le da la nueva (con la SID que corresponde) y un rodaje nuevo. Cerca del punto de espera o con la
/// Torre se queda con la suya (como los IA en el cambio de pista gradual, ADR-093).
/// </summary>
public sealed partial class PilotSession
{
    /// <summary>A menos de esto (m, por la ruta) del punto de espera ya no se le cambia la pista.</summary>
    public const double KeepRunwayWithinM = 1000;

    // Después de colacionar el cambio de pista, el rodaje nuevo (si ya rodaba o tenía el rodaje dado).
    private bool _retaxiAfterReadback;

    /// <summary>
    /// La pista de salida: la del plan si está entre las activas para salidas; si no, la activa con el rodaje más corto desde
    /// el avión (por las calles); sin activas, la del plan o la primera del aeropuerto.
    /// </summary>
    private string ChooseDepartureRunway(IReadOnlyList<string> active, string? planned) =>
        DepartureChoice.Runway(ctx.Graph, ctx.Airport, ctx.PilotPosition(), !PushedBack, ctx.HeadingDeg?.Invoke(), active, planned);

    private static double RouteLengthM(IReadOnlyList<GeoPoint> points)
    {
        var total = 0.0;
        for (var i = 1; i < points.Count; i++)
            total += GeoMath.DistanceM(points[i - 1], points[i]);
        return total;
    }

    /// <summary>La SID recomendada para esa pista (la del plan si sirve) y su nivel inicial.</summary>
    private (Procedure? Sid, ClearedLevel? Level) SidFor(string runway)
    {
        var sid = DepartureChoice.Sid(ctx.Airport, ctx.Plan, runway);
        return (sid, sid is not null ? SidClearance.For(sid, runway, ctx.Atc, OriginTa) : null);
    }

    /// <summary>¿Su pista sigue sirviendo para salir? (activa, o la vieja todavía en el cambio de pista gradual).</summary>
    private bool DepartureRunwayOpen(string runway) =>
        ctx.DepartureRunwayOpen?.Invoke(runway)
        ?? ctx.DepartureEnds() is not { Count: > 0 } ends || ends.Contains(runway, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Del reloj: si su pista dejó de usarse para salidas y todavía no está cerca del punto de espera, Ground le da la nueva.
    /// Solo autorizado y antes de la Torre; sin colaciones pendientes.
    /// </summary>
    private IReadOnlyList<AtcTransmission> DepartureRunwayChange(double nowS)
    {
        if (_expected is not null || Clearance is not { } c || Phase is not (PilotPhase.Cleared or PilotPhase.PushbackApproved or PilotPhase.Taxiing)
            || _holdingFor is not null || DepartureRunwayOpen(c.Runway) || ctx.Stations.For(AtcRole.Ground) is not { } ground)
            return [];
        // Cerca del punto de espera: se queda con la suya.
        if (Phase == PilotPhase.Taxiing && TaxiRoute is { } route && ctx.PilotPosition() is { } here)
        {
            var left = RemainingRouteM(route, here);
            if (left < KeepRunwayWithinM)
                return [];
        }
        var runway = ChooseDepartureRunway(ctx.DepartureEnds(), ctx.Plan.Origin.PlannedRunway);
        if (string.Equals(runway, c.Runway, StringComparison.OrdinalIgnoreCase))
            return [];
        var (sid, level) = SidFor(runway);
        var levelText = level is { } l ? RadioFormat.Level(l.AltitudeFt, l.IsFlightLevel, ctx.Language) : c.LevelText;
        log?.Invoke(Tr.F("{0}: la {1} ya no se usa para salidas: nueva pista {2}{3}.", Callsign, c.Runway, runway, (sid is not null ? $", SID {sid.Name}" : "")));
        Clearance = c with { Runway = runway, Sid = sid?.Name, LevelFt = level?.AltitudeFt ?? c.LevelFt, LevelText = levelText };
        _retaxiAfterReadback = Phase == PilotPhase.Taxiing;
        if (Phase == PilotPhase.Taxiing)
        {
            ctx.SetHoldingPoint?.Invoke(null);
            if (RunwayIndexOf(c.Runway) is { } oldIdx)
                ctx.SetTowerClearance?.Invoke(oldIdx, PilotTowerClearance.None);
        }
        _expected = new ExpectedReadback(Callsign) { Runway = runway, Sid = sid?.Name };
        var f = new Dictionary<string, string> { ["runway"] = runway };
        if (sid is not null)
            f["sid"] = sid.Name;
        return [Instruction(ground, sid is not null ? "atc.departure_runway_change" : "atc.departure_runway_change_no_sid", f, nowS)];
    }

    /// <summary>Metros que le quedan por la ruta de rodaje hasta el punto de espera (desde el punto de la ruta más cercano).</summary>
    private static double RemainingRouteM(PilotTaxiRoute route, GeoPoint here)
    {
        var pts = route.Points;
        if (pts.Count == 0)
            return 0;
        var nearest = 0;
        var bestD = double.MaxValue;
        for (var i = 0; i < pts.Count; i++)
        {
            var d = GeoMath.DistanceM(pts[i], here);
            if (d < bestD)
                (nearest, bestD) = (i, d);
        }
        var left = bestD;
        for (var i = nearest + 1; i < pts.Count; i++)
            left += GeoMath.DistanceM(pts[i - 1], pts[i]);
        return left;
    }
}

/// <summary>
/// La salida que elige LUAW (ADR-113): la pista del plan si está activa para salidas; si no, la activa con el rodaje más
/// corto desde donde está el avión; y la SID recomendada para esa pista (la del plan si sirve). La usan el ATC al dar la
/// autorización y el radar para mostrarla antes.
/// </summary>
public static class DepartureChoice
{
    public static string Runway(TaxiGraph graph, AirportData airport, GeoPoint? from, bool fromStand, double? headingDeg,
        IReadOnlyList<string> active, string? planned)
    {
        if (planned is not null && active.Contains(planned, StringComparer.OrdinalIgnoreCase))
            return planned;
        if (active.Count == 0)
            return planned ?? airport.Runways[0].Primary.Ident;
        if (active.Count == 1 || from is not { } here)
            return active[0];
        string? best = null;
        var bestM = double.MaxValue;
        foreach (var rw in active)
        {
            if (PilotTaxiRoute.From(graph, airport, here, rw, fromStand: fromStand, headingDeg: headingDeg) is not { } r)
                continue;
            var m = 0.0;
            for (var i = 1; i < r.Points.Count; i++)
                m += GeoMath.DistanceM(r.Points[i - 1], r.Points[i]);
            if (m < bestM)
                (best, bestM) = (rw, m);
        }
        return best ?? active[0];
    }

    /// <summary>La SID recomendada para esa pista (la del plan si la sirve; si no, la que la reemplaza), o null.</summary>
    public static Procedure? Sid(AirportData airport, FlightPlan plan, string runway)
    {
        var planForRunway = plan with { Origin = plan.Origin with { PlannedRunway = runway } };
        return ProcedureAdvisor.Sids(airport, planForRunway).FirstOrDefault(a => a.Recommended)?.Procedure;
    }
}
