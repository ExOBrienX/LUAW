using SimATC.Core.Text;
using SimATC.Core.Airports;
using SimATC.Core.Plan;

namespace SimATC.Core.Atc;

/// <summary>Nivel autorizado para una SID y de dónde salió (para el log y la interfaz).</summary>
public sealed record ClearedLevel(double AltitudeFt, string Source, bool IsFlightLevel = false);

/// <summary>
/// Nivel que el ATC da en la autorización de salida ("climb {altitude}"). En orden:
/// <list type="number">
/// <item>la tabla <c>sidClimb.bySid</c> del archivo ATC (de la carta SID del AIP). Las claves pueden usar el nombre largo
///   del punto (GELUS5B) y el simulador el de 6 caracteres (GELU5B): se emparejan con <see cref="ProcedureMatch"/>
///   exigiendo el mismo número y la misma letra;</item>
/// <item>si la SID no está en la tabla o no tiene nivel: la mayor altitud de las restricciones de sus tramos en los datos
///   del simulador (el tope de la SID);</item>
/// <item>si tampoco hay: null. El ATC no inventa un valor y quien llama lo avisa.</item>
/// </list>
/// </summary>
public static class SidClearance
{
    /// <summary>Parecido mínimo para emparejar una clave de la tabla con la SID: mismo punto (o abreviado), número y letra.</summary>
    public const int MinTableMatchScore = 85;

    public static ClearedLevel? For(Procedure sid, string runway, AirportAtcData? atc, double? simTransitionAltitudeFt = null)
    {
        if (atc?.SidClimb is { } climb)
        {
            string? bestKey = null;
            var bestScore = 0;
            foreach (var key in climb.BySid.Keys)
            {
                var ranked = ProcedureMatch.Rank([sid], key, null);
                if (ranked.Count > 0 && ranked[0].Score > bestScore)
                    (bestKey, bestScore) = (key, ranked[0].Score);
            }
            if (bestKey is not null && bestScore >= MinTableMatchScore && climb.BySid[bestKey].ClearedLevelFt is { } ft)
            {
                // Se respeta cómo lo escribe la carta: "FL100" es nivel aunque coincida con la altitud de transición.
                var isFl = climb.BySid[bestKey].ClearedLevel!.Trim().StartsWith("FL", StringComparison.OrdinalIgnoreCase);
                return new ClearedLevel(ft, $"tabla sidClimb ({bestKey})", isFl);
            }
        }
        var top = TopOfSid(sid, runway);
        // Sin datos AIP: la del simulador (cada aeropuerto del mundo trae la suya); 18000 ft solo si tampoco hay.
        var ta = atc?.TransitionAltitudeFt ?? simTransitionAltitudeFt ?? 18000;
        return top is { } t ? new ClearedLevel(t, Tr.T("restricciones de la SID en el simulador"), t > ta) : null;
    }

    /// <summary>La mayor altitud de las restricciones de los tramos de la SID para esa pista (el límite superior en "entre").</summary>
    public static double? TopOfSid(Procedure sid, string runway)
    {
        double? top = null;
        foreach (var leg in ProcedureGeometry.RunwayLegs(sid, runway).Concat(sid.Legs))
        {
            var alt = leg.AltitudeConstraint switch
            {
                AltitudeConstraint.At or AltitudeConstraint.AtOrAbove or AltitudeConstraint.AtOrBelow => leg.Altitude1Ft,
                AltitudeConstraint.Between => Math.Max(leg.Altitude1Ft, leg.Altitude2Ft),
                _ => 0,
            };
            if (alt > 0 && (top is null || alt > top))
                top = Math.Round(alt);
        }
        return top;
    }
}
