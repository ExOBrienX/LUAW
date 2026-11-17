using SimATC.Core.Geo;

namespace SimATC.Core.Plan;

/// <summary>
/// La ruta del plan como la vuela el avión (y la dibuja un FMS): en cada punto <i>fly-by</i> el giro se anticipa y se
/// hace en un arco tangente a los dos tramos, en vez de una esquina. Solo para dibujar.
/// </summary>
public static class RouteGeometry
{
    /// <summary>
    /// Radio de giro en ruta (m): ~420 kt verdaderos con 25° de inclinación (r = v² / (g·tan φ) ≈ 10 km). APROXIMADO:
    /// el avión real depende de su velocidad y de su FMS.
    /// </summary>
    public const double EnrouteTurnRadiusM = 10_000;

    /// <summary>Por debajo de este cambio de rumbo (°) no hay arco; por encima de este, el giro sería casi en U (sin arco).</summary>
    public const double MinTurnDeg = 2;
    public const double MaxTurnDeg = 150;

    /// <summary>
    /// Los puntos de la ruta con un arco en cada esquina. El arco empieza a una distancia r·tan(Δ/2) antes del punto;
    /// si eso pasa de la mitad de algún tramo, el radio se achica para que quepa.
    /// </summary>
    public static List<GeoPoint> FlyBy(IReadOnlyList<GeoPoint> points, double radiusM = EnrouteTurnRadiusM)
    {
        var result = new List<GeoPoint>(points.Count * 4);
        if (points.Count == 0)
            return result;
        result.Add(points[0]);
        for (var i = 1; i < points.Count - 1; i++)
        {
            var corner = points[i];
            var proj = new LocalProjection(corner);
            var a = proj.ToLocal(points[i - 1]);
            var b = proj.ToLocal(points[i + 1]);
            var inBearing = Bearing(a, new LocalPoint(0, 0));
            var outBearing = Bearing(new LocalPoint(0, 0), b);
            var turn = GeoMath.AngleDiffDeg(inBearing, outBearing); // positivo: a la derecha
            var abs = Math.Abs(turn);
            var lenIn = Length(a);
            var lenOut = Length(b);
            if (abs < MinTurnDeg || abs > MaxTurnDeg || lenIn < 1 || lenOut < 1)
            {
                result.Add(corner);
                continue;
            }
            var half = Math.Tan(abs / 2 * GeoMath.DegToRad);
            var r = Math.Min(radiusM, Math.Min(lenIn, lenOut) / 2 / half);
            var lead = r * half;
            // Inicio del arco (sobre el tramo de llegada) y su centro (a la derecha o a la izquierda del rumbo).
            var start = Offset(new LocalPoint(0, 0), inBearing + 180, lead);
            var right = turn > 0;
            var center = Offset(start, inBearing + (right ? 90 : -90), r);
            var radial = inBearing + (right ? -90 : 90);
            var steps = Math.Max(2, (int)Math.Ceiling(abs / 5));
            for (var k = 0; k <= steps; k++)
            {
                var ang = radial + (right ? 1 : -1) * abs * k / steps;
                result.Add(proj.ToGeo(Offset(center, ang, r)));
            }
        }
        if (points.Count > 1)
            result.Add(points[^1]);
        return result;
    }

    private static double Length(LocalPoint p) => Math.Sqrt(p.EastM * p.EastM + p.NorthM * p.NorthM);

    private static double Bearing(LocalPoint a, LocalPoint b) =>
        GeoMath.NormalizeDeg(Math.Atan2(b.EastM - a.EastM, b.NorthM - a.NorthM) / GeoMath.DegToRad);

    private static LocalPoint Offset(LocalPoint p, double bearingDeg, double distanceM)
    {
        var rad = bearingDeg * GeoMath.DegToRad;
        return new LocalPoint(p.EastM + Math.Sin(rad) * distanceM, p.NorthM + Math.Cos(rad) * distanceM);
    }
}
