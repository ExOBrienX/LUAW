namespace SimATC.Core.Geo;

/// <summary>Punto en un plano local en metros: este y norte desde el origen de una <see cref="LocalProjection"/>.</summary>
public readonly record struct LocalPoint(double EastM, double NorthM)
{
    public static double Distance(LocalPoint a, LocalPoint b)
    {
        var de = b.EastM - a.EastM;
        var dn = b.NorthM - a.NorthM;
        return Math.Sqrt(de * de + dn * dn);
    }
}

/// <summary>
/// Proyección equirectangular alrededor de un origen (el punto de referencia del aeropuerto). Precisa a escala de
/// aeropuerto (pocos km). Es la misma relación que usan los BIAS_X (este) y BIAS_Z (norte) de las facilities
/// (V-04, verificado en SCEL a menos de 3 m).
/// </summary>
public readonly struct LocalProjection
{
    private readonly double _metersPerDegLat;
    private readonly double _metersPerDegLon;

    public LocalProjection(GeoPoint origin)
    {
        Origin = origin;
        _metersPerDegLat = GeoMath.EarthRadiusM * GeoMath.DegToRad;
        _metersPerDegLon = _metersPerDegLat * Math.Cos(origin.LatDeg * GeoMath.DegToRad);
    }

    public GeoPoint Origin { get; }

    public LocalPoint ToLocal(GeoPoint p) =>
        new((p.LonDeg - Origin.LonDeg) * _metersPerDegLon, (p.LatDeg - Origin.LatDeg) * _metersPerDegLat);

    public GeoPoint ToGeo(LocalPoint p) =>
        new(Origin.LatDeg + p.NorthM / _metersPerDegLat, Origin.LonDeg + p.EastM / _metersPerDegLon);
}
