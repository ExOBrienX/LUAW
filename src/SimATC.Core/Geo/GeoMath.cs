namespace SimATC.Core.Geo;

/// <summary>Cálculos sobre la esfera terrestre (radio medio). Distancias en metros, rumbos en grados verdaderos.</summary>
public static class GeoMath
{
    /// <summary>Radio medio de la Tierra (IUGG), en metros.</summary>
    public const double EarthRadiusM = 6371008.8;

    public const double DegToRad = Math.PI / 180.0;
    public const double RadToDeg = 180.0 / Math.PI;

    /// <summary>Lleva un ángulo a [0, 360).</summary>
    public static double NormalizeDeg(double deg)
    {
        var d = deg % 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    /// <summary>Punto a la fracción <paramref name="f"/> (0-1) de la ortodrómica de <paramref name="a"/> a <paramref name="b"/>.</summary>
    public static GeoPoint Intermediate(GeoPoint a, GeoPoint b, double f)
    {
        var d = DistanceM(a, b);
        return d < 1 ? a : Destination(a, InitialBearingDeg(a, b), d * f);
    }

    /// <summary>Lleva una longitud a [−180, 180).</summary>
    public static double NormalizeLonDeg(double lon) => NormalizeDeg(lon + 180.0) - 180.0;

    /// <summary>Diferencia angular <paramref name="to"/> − <paramref name="from"/> en (−180, 180].</summary>
    public static double AngleDiffDeg(double from, double to)
    {
        var d = NormalizeDeg(to - from);
        return d > 180.0 ? d - 360.0 : d;
    }

    /// <summary>Distancia de círculo máximo (haversine).</summary>
    public static double DistanceM(GeoPoint a, GeoPoint b)
    {
        var lat1 = a.LatDeg * DegToRad;
        var lat2 = b.LatDeg * DegToRad;
        var dLat = lat2 - lat1;
        var dLon = (b.LonDeg - a.LonDeg) * DegToRad;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
    }

    /// <summary>Rumbo inicial verdadero de <paramref name="from"/> a <paramref name="to"/>.</summary>
    public static double InitialBearingDeg(GeoPoint from, GeoPoint to)
    {
        var lat1 = from.LatDeg * DegToRad;
        var lat2 = to.LatDeg * DegToRad;
        var dLon = (to.LonDeg - from.LonDeg) * DegToRad;
        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        return NormalizeDeg(Math.Atan2(y, x) * RadToDeg);
    }

    /// <summary>Punto al que se llega desde <paramref name="origin"/> con un rumbo verdadero y una distancia.</summary>
    public static GeoPoint Destination(GeoPoint origin, double bearingDeg, double distanceM)
    {
        var lat1 = origin.LatDeg * DegToRad;
        var lon1 = origin.LonDeg * DegToRad;
        var brg = bearingDeg * DegToRad;
        var ang = distanceM / EarthRadiusM;
        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(ang) + Math.Cos(lat1) * Math.Sin(ang) * Math.Cos(brg));
        var lon2 = lon1 + Math.Atan2(Math.Sin(brg) * Math.Sin(ang) * Math.Cos(lat1),
                                     Math.Cos(ang) - Math.Sin(lat1) * Math.Sin(lat2));
        var lonDeg = (lon2 * RadToDeg + 540.0) % 360.0 - 180.0;
        return new GeoPoint(lat2 * RadToDeg, lonDeg);
    }
}
