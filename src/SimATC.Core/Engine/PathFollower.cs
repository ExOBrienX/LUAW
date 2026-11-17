using SimATC.Core.Geo;

namespace SimATC.Core.Engine;

/// <summary>
/// Línea poligonal en metros locales con distancia acumulada y rumbo suave (interpolado entre vértices), para
/// seguir rutas en tierra. Normalmente se arma con <see cref="Smooth"/> para que las esquinas sean curvas.
/// </summary>
public sealed class Polyline
{
    private readonly LocalPoint[] _points;
    private readonly double[] _cumulative;
    private readonly double[] _vertexHeading;

    public Polyline(IReadOnlyList<LocalPoint> points)
    {
        if (points.Count < 2)
            throw new ArgumentException("Una ruta necesita al menos 2 puntos.", nameof(points));
        _points = [.. points];
        _cumulative = new double[_points.Length];
        for (var i = 1; i < _points.Length; i++)
            _cumulative[i] = _cumulative[i - 1] + LocalPoint.Distance(_points[i - 1], _points[i]);

        // Rumbo en cada vértice = promedio de los tramos vecinos: el rumbo varía de forma continua.
        _vertexHeading = new double[_points.Length];
        for (var i = 0; i < _points.Length; i++)
        {
            var before = i > 0 ? Bearing(_points[i - 1], _points[i]) : Bearing(_points[0], _points[1]);
            var after = i < _points.Length - 1 ? Bearing(_points[i], _points[i + 1]) : before;
            _vertexHeading[i] = GeoMath.NormalizeDeg(before + GeoMath.AngleDiffDeg(before, after) / 2);
        }
        _vertexHeading[0] = Bearing(_points[0], _points[1]);
        _vertexHeading[^1] = Bearing(_points[^2], _points[^1]);
    }

    public double Length => _cumulative[^1];

    public LocalPoint Start => _points[0];

    public LocalPoint End => _points[^1];

    public IReadOnlyList<LocalPoint> Points => _points;

    /// <summary>Punto a una distancia s desde el inicio (recortado a [0, Length]).</summary>
    public LocalPoint PointAt(double s)
    {
        if (s <= 0)
            return _points[0];
        if (s >= Length)
            return _points[^1];
        var i = Segment(s);
        var t = Fraction(i, s);
        return new LocalPoint(
            _points[i].EastM + (_points[i + 1].EastM - _points[i].EastM) * t,
            _points[i].NorthM + (_points[i + 1].NorthM - _points[i].NorthM) * t);
    }

    /// <summary>Rumbo verdadero en la distancia s, interpolado entre vértices (sin escalones).</summary>
    public double HeadingAt(double s)
    {
        s = Math.Clamp(s, 0, Length);
        var i = Segment(Math.Min(s, Length - 1e-9));
        var t = Fraction(i, s);
        var a = _vertexHeading[i];
        return GeoMath.NormalizeDeg(a + GeoMath.AngleDiffDeg(a, _vertexHeading[i + 1]) * t);
    }

    /// <summary>Curvatura (1/m) del tramo que contiene s: cambio de rumbo por metro.</summary>
    public double CurvatureAt(double s)
    {
        var i = Segment(Math.Clamp(s, 0, Length - 1e-9));
        var len = _cumulative[i + 1] - _cumulative[i];
        return len > 0 ? Math.Abs(GeoMath.AngleDiffDeg(_vertexHeading[i], _vertexHeading[i + 1])) * GeoMath.DegToRad / len : 0;
    }

    /// <summary>Distancia sobre la ruta del punto más cercano a p.</summary>
    public double Project(LocalPoint p)
    {
        var best = 0.0;
        var bestD = double.MaxValue;
        for (var i = 0; i < _points.Length - 1; i++)
        {
            var a = _points[i];
            var b = _points[i + 1];
            var abE = b.EastM - a.EastM;
            var abN = b.NorthM - a.NorthM;
            var len2 = abE * abE + abN * abN;
            var t = len2 > 0 ? Math.Clamp(((p.EastM - a.EastM) * abE + (p.NorthM - a.NorthM) * abN) / len2, 0, 1) : 0;
            var dE = p.EastM - (a.EastM + abE * t);
            var dN = p.NorthM - (a.NorthM + abN * t);
            var d = dE * dE + dN * dN;
            if (d < bestD)
            {
                bestD = d;
                best = _cumulative[i] + Math.Sqrt(len2) * t;
            }
        }
        return best;
    }

    /// <summary>
    /// Punto más cercano a p entre las distancias <paramref name="fromS"/> y <paramref name="toS"/> de la ruta: su distancia
    /// sobre la ruta y la separación lateral (m). Solo recorre ese tramo (barato para buscar tráfico adelante).
    /// </summary>
    public double ProjectWithin(LocalPoint p, double fromS, double toS, out double lateralM)
    {
        fromS = Math.Clamp(fromS, 0, Length);
        toS = Math.Clamp(toS, fromS, Length);
        var best = fromS;
        var bestD = double.MaxValue;
        for (var i = Segment(Math.Min(fromS, Length - 1e-9)); i < _points.Length - 1 && _cumulative[i] <= toS; i++)
        {
            var a = _points[i];
            var b = _points[i + 1];
            var abE = b.EastM - a.EastM;
            var abN = b.NorthM - a.NorthM;
            var len2 = abE * abE + abN * abN;
            var t = len2 > 0 ? Math.Clamp(((p.EastM - a.EastM) * abE + (p.NorthM - a.NorthM) * abN) / len2, 0, 1) : 0;
            var s = Math.Clamp(_cumulative[i] + Math.Sqrt(len2) * t, fromS, toS);
            var q = PointAt(s);
            var d = (p.EastM - q.EastM) * (p.EastM - q.EastM) + (p.NorthM - q.NorthM) * (p.NorthM - q.NorthM);
            if (d < bestD)
            {
                bestD = d;
                best = s;
            }
        }
        lateralM = Math.Sqrt(bestD);
        return best;
    }

    /// <summary>Parte de la ruta desde la distancia s hasta el final.</summary>
    public Polyline From(double s)
    {
        s = Math.Clamp(s, 0, Length - 0.5);
        var points = new List<LocalPoint> { PointAt(s) };
        for (var i = 0; i < _points.Length; i++)
        {
            if (_cumulative[i] > s + 0.3)
                points.Add(_points[i]);
        }
        if (points.Count < 2)
            points.Add(_points[^1]);
        return new Polyline(points);
    }

    /// <summary>
    /// Ruta con cada esquina reemplazada por un arco circular de radio <paramref name="radiusM"/> (o menor si los
    /// tramos son cortos), muestreada cada ~<paramref name="stepM"/> metros. Así el avión sigue la línea sin cortar.
    /// </summary>
    /// <param name="maxDeviationM">
    /// Cuánto puede alejarse el arco de la esquina original (la línea pintada): en giros cerrados el radio se achica
    /// para seguir la línea pintada y no salirse de la calle. Para 90° y 3 m: radio ≈ 7 m.
    /// </param>
    public static Polyline Smooth(IReadOnlyList<LocalPoint> corners, double radiusM, double stepM = 2.0, double maxDeviationM = 3.0, double minRadiusM = 15.0)
    {
        // Puntos a menos de 3 m crean micro-quiebres (giros bruscos): se juntan, conservando el inicio y el final.
        var pts = Dedup(corners, minSpacingM: 3);
        if (pts.Count < 3)
            return new Polyline(Densify(pts, stepM));

        var output = new List<LocalPoint> { pts[0] };
        var cursor = pts[0];
        for (var i = 1; i < pts.Count - 1; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            var c = pts[i + 1];
            var h1 = Bearing(a, b);
            var h2 = Bearing(b, c);
            var turn = GeoMath.AngleDiffDeg(h1, h2);
            var theta = Math.Abs(turn) * GeoMath.DegToRad;
            if (theta < 2 * GeoMath.DegToRad)
                continue; // casi recto: sin arco

            // Distancia de tangencia; sin pasar la mitad de cada tramo vecino para que los arcos no se pisen.
            var maxT = Math.Min(LocalPoint.Distance(a, b), LocalPoint.Distance(b, c)) / 2;
            var half = Math.Min(theta, 179 * GeoMath.DegToRad) / 2;
            var tan = Math.Tan(half);
            // Desviación del punto medio del arco respecto de la esquina = r·(1/cos(θ/2) − 1).
            // ...pero nunca menos que un radio de giro posible para un jet (en giros muy cerrados se abre la curva).
            var radius = Math.Max(Math.Min(minRadiusM, radiusM), Math.Min(radiusM, maxDeviationM / (1 / Math.Cos(half) - 1)));
            var t = Math.Min(radius * tan, maxT);
            var r = t / tan;

            var p1 = Offset(b, h1 + 180, t);
            AddLine(output, cursor, p1, stepM);
            // Arco de p1 a p2 alrededor del centro, a la izquierda o derecha según el giro.
            var side = Math.Sign(turn);
            var center = Offset(p1, h1 + 90 * side, r);
            var start = Bearing(center, p1);
            var steps = Math.Max(2, (int)Math.Ceiling(r * theta / stepM));
            for (var k = 1; k <= steps; k++)
                output.Add(Offset(center, start + side * theta * GeoMath.RadToDeg * k / steps, r));
            cursor = output[^1];
        }
        AddLine(output, cursor, pts[^1], stepM);
        return new Polyline(Dedup(output));
    }

    private static void AddLine(List<LocalPoint> output, LocalPoint from, LocalPoint to, double stepM)
    {
        var d = LocalPoint.Distance(from, to);
        var n = Math.Max(1, (int)Math.Ceiling(d / stepM));
        for (var k = 1; k <= n; k++)
            output.Add(new LocalPoint(from.EastM + (to.EastM - from.EastM) * k / n, from.NorthM + (to.NorthM - from.NorthM) * k / n));
    }

    private static List<LocalPoint> Densify(List<LocalPoint> pts, double stepM)
    {
        var output = new List<LocalPoint> { pts[0] };
        for (var i = 1; i < pts.Count; i++)
            AddLine(output, pts[i - 1], pts[i], stepM);
        return output;
    }

    public static List<LocalPoint> Dedup(IReadOnlyList<LocalPoint> points, double minSpacingM = 0.2)
    {
        var result = new List<LocalPoint>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (result.Count == 0 || LocalPoint.Distance(result[^1], p) > minSpacingM)
                result.Add(p);
            else if (i == points.Count - 1)
                result[^1] = p; // el final exacto se conserva
        }
        if (result.Count == 1)
            result.Add(new LocalPoint(result[0].EastM, result[0].NorthM + 1));
        return result;
    }

    public static LocalPoint Offset(LocalPoint p, double bearingDeg, double distanceM)
    {
        var rad = bearingDeg * GeoMath.DegToRad;
        return new LocalPoint(p.EastM + Math.Sin(rad) * distanceM, p.NorthM + Math.Cos(rad) * distanceM);
    }

    public static double Bearing(LocalPoint a, LocalPoint b) =>
        GeoMath.NormalizeDeg(Math.Atan2(b.EastM - a.EastM, b.NorthM - a.NorthM) * GeoMath.RadToDeg);

    private double Fraction(int i, double s)
    {
        var len = _cumulative[i + 1] - _cumulative[i];
        return len > 0 ? Math.Clamp((s - _cumulative[i]) / len, 0, 1) : 0;
    }

    private int Segment(double s)
    {
        int lo = 0, hi = _cumulative.Length - 2;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_cumulative[mid] <= s)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }
}

/// <summary>
/// Sigue una ruta suavizada exactamente ("sobre rieles"): la posición y el rumbo salen de la propia ruta, así que
/// nunca corta esquinas ni se sale de la línea. La velocidad respeta la curvatura (aceleración lateral máxima) y
/// frena con anticipación antes de curvas y del final (RF-17). Sin asignaciones en <see cref="Step"/>.
/// </summary>
public struct PathFollower
{
    public LocalPoint Position;
    public double HeadingDeg;
    public double SpeedKt;
    /// <summary>Avance sobre la ruta (m).</summary>
    public double Progress;
    /// <summary>true = avanza marcha atrás (pushback): la nariz apunta al revés del movimiento.</summary>
    public bool Reverse;

    private const double KtToMs = 0.514444;

    /// <summary>Velocidad mínima en curva (~1 kt) para no quedarse detenido en un quiebre del grafo.</summary>
    private const double MinCurveSpeedMs = 0.5;

    public static PathFollower StartOn(Polyline path, bool reverse)
    {
        var heading = path.HeadingAt(0);
        return new PathFollower
        {
            Position = path.Start,
            HeadingDeg = reverse ? GeoMath.NormalizeDeg(heading + 180) : heading,
            Reverse = reverse,
        };
    }

    public readonly bool Arrived(Polyline path) => path.Length - Progress < 0.05 && SpeedKt < 0.1;

    /// <summary>Detenido en la distancia <paramref name="stopAt"/> de la ruta.</summary>
    public readonly bool StoppedAt(double stopAt) => stopAt - Progress < 0.05 && SpeedKt < 0.1;

    public void Step(Polyline path, double dt, in GroundLimits limits, double maxSpeedKt, bool stopAtEnd) =>
        Step(path, dt, limits, maxSpeedKt, stopAtEnd ? path.Length : double.PositiveInfinity);

    /// <param name="stopAt">Distancia de la ruta donde detenerse (p. ej. el punto de espera), o infinito.</param>
    public void Step(Polyline path, double dt, in GroundLimits limits, double maxSpeedKt, double stopAt)
    {
        var stopAtEnd = !double.IsPositiveInfinity(stopAt);
        stopAt = Math.Min(stopAt, path.Length);
        var v = SpeedKt * KtToMs;
        var decel = limits.DecelKtPerS * KtToMs;
        var latAccel = limits.LateralAccelMs2;

        // Velocidad permitida: la menor entre la recta, la curva actual y lo que exige frenar para lo que viene
        // (curvas y el final) dentro de la distancia de frenado.
        var allowed = maxSpeedKt * KtToMs;
        var horizon = v * v / (2 * decel) + 15;
        for (var d = 0.0; d <= horizon; d += 1)
        {
            var s = Progress + d;
            if (s > path.Length)
                break;
            var k = path.CurvatureAt(s);
            if (k > 1e-6)
            {
                // Aceleración lateral y velocidad de giro máximas (v = ω/κ): en curvas cerradas frena lo necesario.
                var vCurve = Math.Max(Math.Min(Math.Sqrt(latAccel / k), limits.MaxYawRateDegPerS * GeoMath.DegToRad / k), MinCurveSpeedMs);
                allowed = Math.Min(allowed, Math.Sqrt(vCurve * vCurve + 2 * decel * d));
            }
        }
        if (stopAtEnd)
        {
            var remaining = Math.Max(0, stopAt - Progress);
            allowed = Math.Min(allowed, Math.Sqrt(2 * decel * remaining));
        }

        var accel = limits.AccelKtPerS * KtToMs;
        v = v < allowed ? Math.Min(allowed, v + accel * dt) : Math.Max(allowed, v - decel * 1.5 * dt);
        SpeedKt = v / KtToMs;

        // Nunca hacia atrás: si el punto de parada quedó detrás (tráfico que aparece encima), se detiene donde está.
        Progress = Math.Max(Progress, Math.Min(stopAtEnd ? stopAt : path.Length, Progress + v * dt));
        if (stopAtEnd && stopAt - Progress < 0.05)
        {
            Progress = Math.Max(Progress, stopAt);
            SpeedKt = 0;
        }
        Position = path.PointAt(Progress);
        var heading = path.HeadingAt(Progress);
        HeadingDeg = Reverse ? GeoMath.NormalizeDeg(heading + 180) : heading;
    }
}

/// <summary>Límites del movimiento en tierra (valores APROXIMADOS, ver Performance).</summary>
public readonly record struct GroundLimits(
    double AccelKtPerS,
    double DecelKtPerS,
    double LateralAccelMs2,
    double MaxYawRateDegPerS,
    double CornerRadiusM);
