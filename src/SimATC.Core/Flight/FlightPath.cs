using SimATC.Core.Geo;

namespace SimATC.Core.Flight;

/// <summary>Qué tramo de la ruta es (para la fase, el ATC y el radar).</summary>
public enum PathSection
{
    Sid,
    Enroute,
    Star,
    Approach,
    /// <summary>Final recta hasta el umbral (senda de 3°).</summary>
    Final,
}

/// <summary>
/// Un punto de la ruta lateral. <see cref="RemainingM"/>: distancia por la ruta desde aquí hasta el final. Restricciones de
/// la carta (0 = sin restricción): altitud mínima y máxima (ft; "at" = las dos iguales) y velocidad máxima (kt IAS).
/// <see cref="ProfileFt"/>: altitud prevista por el perfil de descenso (0 si no hay perfil).
/// </summary>
public readonly record struct PathPoint(GeoPoint Position, string Name, PathSection Section, double RemainingM,
    double MinAltFt = 0, double MaxAltFt = 0, double SpeedMaxKt = 0, double ProfileFt = 0, string Region = "")
{
    public bool IsFix => Name.Length > 0;
}

/// <summary>Un punto para armar la ruta (sin las distancias, que calcula <see cref="FlightPath"/>).</summary>
public readonly record struct PathInput(GeoPoint Position, string Name, PathSection Section, double MinAltFt = 0,
    double MaxAltFt = 0, double SpeedMaxKt = 0, string Region = "")
{
    public static implicit operator PathInput((GeoPoint Position, string Name, PathSection Section) p) =>
        new(p.Position, p.Name, p.Section);
}

/// <summary>
/// Ruta lateral de un vuelo IA (etapa 6): puntos geográficos densos (las curvas de los procedimientos ya vienen como arcos
/// de <see cref="Plan.ProcedureGeometry"/>). El avión la sigue apuntando al punto activo y anticipando los giros.
/// </summary>
public sealed class FlightPath
{
    private readonly PathPoint[] _points;

    public FlightPath(IReadOnlyList<(GeoPoint Position, string Name, PathSection Section)> points)
        : this(points.Select(p => (PathInput)p).ToList())
    {
    }

    public FlightPath(IReadOnlyList<PathInput> points)
    {
        var clean = new List<PathInput>(points.Count);
        foreach (var p in points)
        {
            // Sin puntos repetidos (rompen el rumbo entre puntos); si el repetido trae nombre o restricciones, se conservan.
            if (clean.Count > 0 && GeoMath.DistanceM(clean[^1].Position, p.Position) < 5)
            {
                var last = clean[^1];
                if (last.Name.Length == 0 && p.Name.Length > 0 || p.MinAltFt > 0 || p.MaxAltFt > 0 || p.SpeedMaxKt > 0)
                    clean[^1] = p with { Section = last.Section };
                continue;
            }
            clean.Add(p);
        }
        _points = new PathPoint[clean.Count];
        var remaining = 0.0;
        for (var i = clean.Count - 1; i >= 0; i--)
        {
            if (i < clean.Count - 1)
                remaining += GeoMath.DistanceM(clean[i].Position, clean[i + 1].Position);
            var c = clean[i];
            _points[i] = new PathPoint(c.Position, c.Name, c.Section, remaining, c.MinAltFt, c.MaxAltFt, c.SpeedMaxKt, 0, c.Region);
        }
    }

    /// <summary>Ángulo de la senda de la aproximación (°), de la carta o 3°.</summary>
    public double GlideDeg { get; init; } = 3;

    /// <summary>Procedimientos que usa (para el radar y la ficha), o null.</summary>
    public string? SidName { get; init; }
    public string? StarName { get; init; }
    public string? ApproachName { get; init; }

    /// <summary>La ruta en ruta como en un plan de vuelo ("DCT ALGEK UQ808 EROLO"), o null si va directo.</summary>
    public string? RouteText { get; init; }

    /// <summary>Tipo de la aproximación como se dice por radio ("ILS", "RNP"), o null.</summary>
    public string? ApproachWord { get; init; }

    /// <summary>¿Tiene perfil de descenso calculado?</summary>
    public bool HasProfile { get; private set; }

    /// <summary>
    /// Perfil de descenso hacia atrás desde el final (<paramref name="endFt"/>): ~3° (300 ft/NM) en ruta y STAR, la senda
    /// (<paramref name="glideFtPerNm"/>) en la final, ajustado a cada restricción de la carta ("at", "at or above", "at or
    /// below", "between"). Nunca sobre <paramref name="ceilingFt"/>. Con <paramref name="climbAtCeiling"/>, la SID queda en el
    /// techo (sus restricciones son de ascenso, no de descenso: el avión no debe "bajar" hacia ellas).
    /// </summary>
    public void ComputeDescentProfile(double endFt, double glideFtPerNm, double ceilingFt, bool climbAtCeiling = false)
    {
        if (_points.Length == 0)
            return;
        var a = endFt;
        _points[^1] = _points[^1] with { ProfileFt = a };
        for (var i = _points.Length - 2; i >= 0; i--)
        {
            var p = _points[i];
            var segNm = (p.RemainingM - _points[i + 1].RemainingM) / 1852;
            a += segNm * (_points[i + 1].Section == PathSection.Final ? glideFtPerNm : 300);
            if (p.MaxAltFt > 0)
                a = Math.Min(a, p.MaxAltFt);
            if (p.MinAltFt > 0)
                a = Math.Max(a, p.MinAltFt);
            a = Math.Min(a, ceilingFt);
            if (climbAtCeiling && p.Section == PathSection.Sid)
                a = ceilingFt;
            _points[i] = p with { ProfileFt = a };
        }
        HasProfile = true;
    }

    public int Count => _points.Length;

    public PathPoint this[int index] => _points[index];

    public PathPoint End => _points[^1];

    public double LengthM => _points.Length > 0 ? _points[0].RemainingM : 0;

    /// <summary>
    /// Un punto de la ruta a <paramref name="alongM"/> metros desde su inicio: posición, rumbo del tramo y altitud del perfil
    /// (0 sin perfil). Para que un vuelo nazca sobre su ruta (prueba de Marco: nada de giros al aparecer).
    /// </summary>
    public (GeoPoint Position, double HeadingDeg, double ProfileFt) AtDistance(double alongM)
    {
        var (pos, heading, profile, _) = PointAt(alongM);
        return (pos, heading, profile);
    }

    /// <summary>Como <see cref="AtDistance"/>, y además el índice del punto siguiente (al que vuela desde ahí).</summary>
    public (GeoPoint Position, double HeadingDeg, double ProfileFt, int NextIndex) PointAt(double alongM)
    {
        if (_points.Length < 2)
            return (_points.Length == 1 ? _points[0].Position : default, 0, 0, 0);
        var target = Math.Clamp(LengthM - alongM, 0, LengthM); // distancia que le queda al llegar ahí
        for (var i = 0; i < _points.Length - 1; i++)
        {
            var a = _points[i];
            var b = _points[i + 1];
            if (b.RemainingM > target)
                continue;
            var seg = Math.Max(1, a.RemainingM - b.RemainingM);
            var into = a.RemainingM - target;
            var heading = GeoMath.InitialBearingDeg(a.Position, b.Position);
            var profile = a.ProfileFt > 0 && b.ProfileFt > 0 ? a.ProfileFt + (b.ProfileFt - a.ProfileFt) * into / seg : 0;
            return (GeoMath.Destination(a.Position, heading, into), heading, profile, i + 1);
        }
        return (_points[^1].Position, GeoMath.InitialBearingDeg(_points[^2].Position, _points[^1].Position), _points[^1].ProfileFt,
            _points.Length - 1);
    }

    /// <summary>Índice del primer punto de la sección (o -1).</summary>
    public int FirstOf(PathSection section)
    {
        for (var i = 0; i < _points.Length; i++)
        {
            if (_points[i].Section == section)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Punto de la ruta al que conviene apuntar estando en <paramref name="position"/>: el siguiente al tramo más cercano
    /// entre <paramref name="from"/> y unos puntos más adelante (para retomar la ruta tras un desvío o al crearse a mitad).
    /// </summary>
    public int NearestAhead(GeoPoint position, int from = 0)
    {
        var best = Math.Min(from, _points.Length - 1);
        var bestD = double.MaxValue;
        for (var i = from; i < _points.Length; i++)
        {
            var d = GeoMath.DistanceM(position, _points[i].Position);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        // Si el más cercano quedó atrás (el avión ya lo pasó), el siguiente.
        if (best + 1 < _points.Length)
        {
            var legBearing = GeoMath.InitialBearingDeg(_points[best].Position, _points[best + 1].Position);
            var toPoint = GeoMath.InitialBearingDeg(position, _points[best].Position);
            if (Math.Abs(GeoMath.AngleDiffDeg(legBearing, toPoint)) > 90)
                best++;
        }
        return best;
    }
}
