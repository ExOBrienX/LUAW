using SimATC.Core.Geo;
using SimATC.Core.Units;

namespace SimATC.Core.Flight;

/// <summary>
/// Separación en ruta (el Centro, antes de la secuencia de llegadas): dos vuelos al mismo destino que van juntos por la
/// misma parte de la ruta no se encima uno al otro; el de atrás vuela más lento que el de adelante hasta tener
/// <see cref="TrailNm"/>. Vuelo de Marco a KEWR (2026-11-15): FDX1480 (desde KCLT) y UAL4140 (desde KATL) se juntaron
/// sobre QUART a FL350, a 0,3 NM, porque nada los separaba antes de pasar a llegada controlada. Sin asignaciones.
/// </summary>
public static class EnrouteSpacing
{
    /// <summary>Separación en ruta buscada (NM, por la ruta). APROXIMADO: radar en ruta 5 NM con margen.</summary>
    public const double TrailNm = 8;

    /// <summary>Con la separación tomada, se suelta la velocidad recién pasado este margen (NM), para no oscilar.</summary>
    public const double ReleaseMarginNm = 2;

    /// <summary>Solo cuenta si están así de cerca en línea recta (NM): misma distancia al destino por otro lado no es conflicto.</summary>
    public const double NearNm = 12;

    /// <summary>El de atrás vuela a la TAS del de adelante menos esto (kt), como un "reduce speed" del Centro. APROXIMADO.</summary>
    public const double SlowerKt = 30;

    public static void Update(List<AirborneAgent> air)
    {
        for (var i = 0; i < air.Count; i++)
            air[i].NextTrailTasKt = 0;
        var near = NearNm * UnitConversions.MetersPerNauticalMile;
        for (var i = 0; i < air.Count; i++)
        {
            var a = air[i];
            if (!Spaced(a))
                continue;
            for (var j = i + 1; j < air.Count; j++)
            {
                var b = air[j];
                if (!Spaced(b) || !string.Equals(a.Destination, b.Destination, StringComparison.Ordinal)
                    || GeoMath.DistanceM(a.Position, b.Position) > near)
                    continue;
                // El de atrás es el que tiene más ruta por delante.
                var (lead, follow) = a.RemainingM <= b.RemainingM ? (a, b) : (b, a);
                var gap = (follow.RemainingM - lead.RemainingM) / UnitConversions.MetersPerNauticalMile;
                var limit = follow.TrailTasKt > 0 ? TrailNm + ReleaseMarginNm : TrailNm;
                if (gap >= limit)
                    continue;
                var kt = Math.Max(1, lead.SpeedKt - SlowerKt);
                follow.NextTrailTasKt = follow.NextTrailTasKt > 0 ? Math.Min(follow.NextTrailTasKt, kt) : kt;
            }
        }
        for (var i = 0; i < air.Count; i++)
            air[i].TrailTasKt = air[i].NextTrailTasKt;
    }

    /// <summary>En crucero o descenso (antes de la aproximación; ahí manda la secuencia de llegadas).</summary>
    private static bool Spaced(AirborneAgent a) => a.Phase is FlightPhase.Cruise or FlightPhase.Descent;
}
