namespace SimATC.Core.Geo;

/// <summary>
/// Proyección de Mercator centrada en un origen, con escala real en la latitud del origen. Es <b>conforme</b>: conserva
/// los ángulos en todas partes, así que una pista o un tramo de procedimiento a 400 km del origen se dibuja con su
/// rumbo verdadero. La equirectangular local (<see cref="LocalProjection"/>) estira el este-oeste lejos del origen
/// (4 % en SCIE vista desde SCEL) y gira las líneas ~1°. La usa el radar para todas sus capas (ADR-026).
/// </summary>
public readonly struct MercatorProjection
{
    private readonly double _k;
    private readonly double _y0;

    public MercatorProjection(GeoPoint origin)
    {
        Origin = origin;
        _k = GeoMath.EarthRadiusM * Math.Cos(origin.LatDeg * GeoMath.DegToRad);
        _y0 = Y(origin.LatDeg);
    }

    public GeoPoint Origin { get; }

    /// <summary>Metros en el plano: este (x) y norte (y) desde el origen. Cerca del origen, igual a la local.</summary>
    /// <remarks>
    /// La longitud se mide desde el centro por el lado corto (−180°..180°): una ruta que cruza el antimeridiano (SCEL→YSSY)
    /// se dibuja hacia el oeste y no dando la vuelta al mapa (prueba de Marco, 2026-11-05).
    /// </remarks>
    public LocalPoint ToPlane(GeoPoint p) =>
        new(_k * WrapDeg(p.LonDeg - Origin.LonDeg) * GeoMath.DegToRad, _k * (Y(p.LatDeg) - _y0));

    private static double WrapDeg(double d) => d - 360 * Math.Floor((d + 180) / 360);

    public GeoPoint ToGeo(LocalPoint p)
    {
        var lat = 2 * Math.Atan(Math.Exp(p.NorthM / _k + _y0)) - Math.PI / 2;
        return new GeoPoint(lat / GeoMath.DegToRad, WrapDeg(Origin.LonDeg + p.EastM / _k / GeoMath.DegToRad));
    }

    private static double Y(double latDeg) => Math.Log(Math.Tan(Math.PI / 4 + latDeg * GeoMath.DegToRad / 2));
}
