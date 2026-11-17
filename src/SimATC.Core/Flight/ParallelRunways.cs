using SimATC.Core.Airports;
using SimATC.Core.Geo;

namespace SimATC.Core.Flight;

/// <summary>
/// Llegadas por dos pistas paralelas según la demanda (ADR-082). Fuente para SCEL: AIP-CHILE AD 2 SCEL, reglamentos locales
/// 2.1 (AMDT 64): "se podrán efectuar aproximaciones paralelas simultáneas a las pistas 17R y 17L" (ILS o RNP), activadas
/// por el ATC según la demanda. Para cualquier aeropuerto: dos cabeceras en la misma dirección (± 5°) con los ejes a 915 m o
/// más, el mínimo OACI para aproximaciones paralelas dependientes (Doc 4444 §6.7 / Doc 9643). La demanda que las activa no
/// está publicada: APROXIMADO (4 llegadas a menos de 60 NM; se apagan con 1 o menos).
/// </summary>
public static class ParallelRunways
{
    /// <summary>Separación mínima entre ejes (m) para aproximaciones paralelas dependientes (OACI).</summary>
    public const double MinSeparationM = 915;

    /// <summary>Llegadas a menos de <see cref="DemandNm"/> desde las que se abren las dos pistas. APROXIMADO.</summary>
    public const int OnAtArrivals = 4;

    /// <summary>Con estas llegadas o menos se vuelve a una pista. APROXIMADO.</summary>
    public const int OffAtArrivals = 1;

    public const double DemandNm = 60;

    /// <summary>Dos llegadas más juntas que esto (NM, en distancia al aeropuerto) comprimen la secuencia. APROXIMADO.</summary>
    public const double CompressedGapNm = 5;

    /// <summary>
    /// ¿La secuencia viene comprimida? Alguna pareja de llegadas consecutivas (por distancia al aeropuerto) a menos de
    /// <see cref="CompressedGapNm"/>: con una sola pista habría que reducir, esperar o frustrar (pedido de Marco, ADR-082).
    /// </summary>
    /// <summary>Dos llegadas a la misma pista con menos de esto (s) entre sus aterrizajes estimados van demasiado juntas. APROXIMADO.</summary>
    public const double CompressedGapS = 110;

    /// <summary>
    /// ¿Dos llegadas aterrizarían demasiado juntas? Por el tiempo estimado al umbral de cada una (por su ruta), no por la
    /// distancia en línea recta (ADR-131): así la paralela se abre antes de que haga falta una espera.
    /// </summary>
    public static bool CompressedEta(List<double> etasS)
    {
        etasS.Sort();
        for (var i = 1; i < etasS.Count; i++)
        {
            if (etasS[i] - etasS[i - 1] < CompressedGapS)
                return true;
        }
        return false;
    }

    public static bool Compressed(List<double> distancesNm)
    {
        distancesNm.Sort();
        for (var i = 1; i < distancesNm.Count; i++)
        {
            if (distancesNm[i] - distancesNm[i - 1] < CompressedGapNm)
                return true;
        }
        return false;
    }

    /// <summary>La cabecera paralela a <paramref name="ident"/> (misma dirección, ejes a ≥ 915 m), o null.</summary>
    public static string? PartnerOf(AirportData airport, string ident)
    {
        RunwayEnd? me = null;
        RunwayEnd? meOther = null;
        foreach (var r in airport.Runways)
        {
            if (string.Equals(r.Primary.Ident, ident, StringComparison.OrdinalIgnoreCase))
                (me, meOther) = (r.Primary, r.Secondary);
            else if (string.Equals(r.Secondary.Ident, ident, StringComparison.OrdinalIgnoreCase))
                (me, meOther) = (r.Secondary, r.Primary);
        }
        if (me is null || meOther is null)
            return null;
        string? best = null;
        var bestSep = double.MaxValue;
        foreach (var r in airport.Runways)
        {
            foreach (var (end, other) in new[] { (r.Primary, r.Secondary), (r.Secondary, r.Primary) })
            {
                if (ReferenceEquals(end, me) || Math.Abs(GeoMath.AngleDiffDeg(end.HeadingTrueDeg, me.HeadingTrueDeg)) > 5)
                    continue;
                // Distancia del umbral de la otra al eje de esta (perpendicular).
                var d = GeoMath.DistanceM(me.Position, end.Position);
                var off = GeoMath.AngleDiffDeg(me.HeadingTrueDeg, GeoMath.InitialBearingDeg(me.Position, end.Position)) * Math.PI / 180;
                var sep = Math.Abs(d * Math.Sin(off));
                if (sep >= MinSeparationM && sep < bestSep)
                {
                    bestSep = sep;
                    best = end.Ident;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Las pistas de llegada con la demanda actual (con histéresis): las del modo (p. ej. 17L) y, con
    /// <see cref="OnAtArrivals"/> llegadas o más, también la paralela (17R) mientras no baje a <see cref="OffAtArrivals"/>.
    /// </summary>
    /// <param name="compressed">La secuencia viene comprimida (<see cref="Compressed"/>): también abre la paralela.</param>
    public static IReadOnlyList<string> LandingSet(AirportData airport, IReadOnlyList<string> modeArrivals, int arrivalsNear,
        ref bool parallel, bool compressed = false)
    {
        if (modeArrivals.Count != 1 || PartnerOf(airport, modeArrivals[0]) is not { } partner)
        {
            parallel = false;
            return modeArrivals;
        }
        if (arrivalsNear >= OnAtArrivals || compressed && arrivalsNear >= 2)
            parallel = true;
        else if (arrivalsNear <= OffAtArrivals)
            parallel = false;
        return parallel ? [modeArrivals[0], partner] : modeArrivals;
    }
}
