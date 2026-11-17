using SimATC.Core.Text;

namespace SimATC.Core.Plan;

/// <summary>Avisos sobre el plan cargado, para el panel Plan de vuelo.</summary>
public static class PlanChecks
{
    /// <summary>Un plan generado hace más que esto probablemente no es el del vuelo de hoy.</summary>
    public static readonly TimeSpan OldPlan = TimeSpan.FromHours(24);

    /// <param name="magVarDeg">Variación magnética (este positivo) para la derrota magnética; sin ella, la verdadera.</param>
    public static IReadOnlyList<string> Warnings(FlightPlan plan, string? loadedAirportIcao, DateTime nowUtc, double magVarDeg = 0)
    {
        var warnings = new List<string>();
        if (loadedAirportIcao is { } icao && !string.Equals(plan.Origin.Icao, icao, StringComparison.OrdinalIgnoreCase))
            warnings.Add(Tr.F("El plan sale de {0}, pero el aeropuerto cargado es {1}.", plan.Origin.Icao, icao));
        if (plan.GeneratedUtc is { } generated && nowUtc - generated > OldPlan)
            warnings.Add(Tr.F("El plan se generó hace {0} día(s): ¿es el de este vuelo?", (int)(nowUtc - generated).TotalDays));
        if (plan.Source == PlanSource.SimBrief && plan.Fixes.Count == 0)
            warnings.Add(Tr.T("El plan no trae puntos de ruta: no se dibuja en el radar."));
        if (plan.CruiseAltitudeFt <= 0)
            warnings.Add(Tr.T("El plan no trae nivel de crucero."));
        // OFP sin aerolínea (vuelo RJTT-RJSS: indicativo "878"): el ATC lo llamaría solo por el número.
        if (plan.Callsign.Length > 0 && !plan.Callsign.Any(char.IsLetter))
            warnings.Add(Tr.F("El indicativo \"{0}\" no trae aerolínea: agrégala en SimBrief (campo Airline) o edita el indicativo.", plan.Callsign));
        if (ChileLevelWarning(plan, magVarDeg) is { } level)
            warnings.Add(level);
        return warnings;
    }

    /// <summary>
    /// Niveles de crucero IFR en Chile (AIP-CHILE ENR 1.7-3, AMDT 62, 28 NOV 2024): derrota magnética de 030° a 209°, niveles
    /// impares (FL270, FL290…); de 210° a 029°, pares (FL260, FL280…). Solo vuelos dentro de Chile (origen y destino SC**),
    /// hasta FL410, y con 5° de margen en los bordes (la derrota es la del origen al destino, no la de cada tramo).
    /// </summary>
    public static string? ChileLevelWarning(FlightPlan plan, double magVarDeg = 0)
    {
        if (!plan.Origin.Icao.StartsWith("SC", StringComparison.OrdinalIgnoreCase)
            || !plan.Destination.Icao.StartsWith("SC", StringComparison.OrdinalIgnoreCase)
            || plan.Origin.Position is not { } o || plan.Destination.Position is not { } d
            || plan.CruiseAltitudeFt < 1000 || plan.CruiseAltitudeFt > 41_000 || plan.CruiseAltitudeFt % 1000 != 0)
            return null;
        var track = Geo.GeoMath.NormalizeDeg(Geo.GeoMath.InitialBearingDeg(o, d) - magVarDeg);
        var fromBorder = Math.Min(Math.Abs(Geo.GeoMath.AngleDiffDeg(track, 30)), Math.Abs(Geo.GeoMath.AngleDiffDeg(track, 210)));
        if (fromBorder < 5)
            return null;
        var odd = track is >= 30 and < 210;
        var level = plan.CruiseAltitudeFt / 1000;
        if (level % 2 == (odd ? 1 : 0))
            return null;
        return Tr.F("Nivel FL{0:000} con derrota {1:000}°: en Chile (AIP ENR 1.7-3) de 030° a 209° van niveles ", plan.CruiseAltitudeFt / 100, track) +
               (odd ? Tr.T("impares (FL250, FL270, FL290…).") : Tr.T("pares (FL260, FL280, FL300…)."));
    }
}
