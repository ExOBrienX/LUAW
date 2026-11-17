using SimATC.Core.Airports;

using SimATC.Core.Text;

namespace SimATC.Core.Atc;

/// <summary>
/// Una cabecera para elegir en el plan (ADR-127): si está en uso ahora (punto verde, como el EFB del simulador), para qué
/// la usa el tráfico y el viento sobre ella.
/// </summary>
public sealed record RunwayOption(string Ident, bool InUse, string Note)
{
    public override string ToString() => Ident;
}

/// <summary>
/// Las cabeceras de un aeropuerto para el selector de pista del plan (ADR-127, pedido de Marco 2026-11-11): todas, con las
/// que están en uso marcadas. "En uso" son las del sentido que eligió el viento más las del reparto de llegadas y salidas.
/// </summary>
public static class RunwayOptions
{
    /// <param name="windEnds">Las que eligió el viento (<see cref="RunwayInUse.Ends"/>); vacío si todavía no hay pista en uso.</param>
    /// <param name="arrivals">Las de llegadas del reparto (o del AIP).</param>
    /// <param name="departures">Las de salidas.</param>
    /// <param name="windFromTrueDeg">Viento (de dónde viene, verdadero); null sin viento conocido.</param>
    public static IReadOnlyList<RunwayOption> For(AirportData airport, IReadOnlyList<string> windEnds, IReadOnlyList<string> arrivals,
        IReadOnlyList<string> departures, double? windFromTrueDeg, double? windKt, double calmKt = 3)
    {
        var list = new List<RunwayOption>();
        foreach (var r in airport.Runways)
        {
            Add(r.Primary);
            Add(r.Secondary);
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Ident, b.Ident));
        return list;

        void Add(RunwayEnd end)
        {
            var arr = Has(arrivals, end.Ident);
            var dep = Has(departures, end.Ident);
            var inUse = arr || dep || Has(windEnds, end.Ident);
            var role = arr && dep ? Tr.T("en uso · llegadas y salidas") : arr ? Tr.T("en uso · llegadas") : dep ? Tr.T("en uso · salidas") : inUse ? Tr.T("en uso") : "";
            var wind = "";
            if (windFromTrueDeg is { } from && windKt is { } kt)
            {
                if (kt < calmKt)
                    wind = Tr.T("viento calmo");
                else
                {
                    var w = WindComponents.For(end.HeadingTrueDeg, from, kt);
                    wind = w.HeadwindKt >= 0.5 ? Tr.F("frente {0:0} kt", w.HeadwindKt) : w.TailwindKt >= 0.5 ? Tr.F("cola {0:0} kt", w.TailwindKt) : Tr.T("sin viento de frente");
                    if (w.CrosswindKt >= 5)
                        wind += Tr.F(", cruzado {0:0} kt", w.CrosswindKt);
                }
            }
            list.Add(new RunwayOption(end.Ident, inUse, role.Length > 0 && wind.Length > 0 ? $"{role} · {wind}" : role + wind));
        }
    }

    /// <summary>
    /// ¿El piloto puede salir por la pista de su plan? Si está en uso: en el sentido que eligió el viento o en el reparto,
    /// aunque los IA la usen para llegar (la Torre lo intercala). Sin pista en uso todavía, cualquiera que exista. Nunca en
    /// sentido contrario al tráfico (antes bastaba con 10 kt de cola o menos, ADR-081).
    /// </summary>
    public static bool PlanRunwayInUse(AirportData airport, string planned, IReadOnlyList<string> windEnds, IReadOnlyList<string> arrivals,
        IReadOnlyList<string> departures)
    {
        var exists = false;
        foreach (var r in airport.Runways)
            exists |= Same(r.Primary.Ident, planned) || Same(r.Secondary.Ident, planned);
        if (!exists)
            return false;
        return windEnds.Count == 0 || Has(windEnds, planned) || Has(arrivals, planned) || Has(departures, planned);
    }

    private static bool Has(IReadOnlyList<string> ends, string ident)
    {
        foreach (var e in ends)
        {
            if (Same(e, ident))
                return true;
        }
        return false;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
