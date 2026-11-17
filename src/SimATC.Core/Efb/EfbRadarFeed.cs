using System.Buffers;
using System.Text.Json;
using SimATC.Core.Geo;
using SimATC.Core.Units;

namespace SimATC.Core.Efb;

/// <summary>Un avión para el radar del EFB (ADR-073). Altitud en pies MSL, velocidad en nudos, rumbo verdadero.</summary>
public readonly record struct EfbTarget(uint Id, string Callsign, string? Type, GeoPoint Position, double HeadingDeg,
    double AltitudeFt, double SpeedKt, bool OnGround, bool Parked, string? Origin = null, string? Destination = null);

/// <summary>El avión del piloto para el radar del EFB.</summary>
public readonly record struct EfbOwnship(GeoPoint Position, double HeadingDeg, double AltitudeFt, double SpeedKt, bool OnGround);

/// <summary>
/// Arma el mensaje JSON del radar del EFB (ADR-073): el avión del piloto y el tráfico dentro de <see cref="RangeNm"/>,
/// ordenado por distancia, con distancia, marcación relativa y tendencia vertical (comparando con el mensaje anterior).
/// Corre en la UI a ~2 Hz, fuera del tick del motor; reutiliza el búfer y el estado entre llamadas.
/// </summary>
public sealed class EfbRadarFeed
{
    /// <summary>Hasta esta distancia (NM) se envía el tráfico (el zoom máximo del EFB es 80 NM).</summary>
    public const double RangeNm = 100;

    /// <summary>Como máximo, estos aviones (los más cercanos).</summary>
    public const int MaxTargets = 60;

    /// <summary>Cambio de altitud (ft/min) desde el que se marca subiendo o bajando.</summary>
    public const double TrendFpm = 300;

    private readonly ArrayBufferWriter<byte> _buffer = new(16 * 1024);
    private readonly List<(EfbTarget T, double Nm, double Bearing)> _near = [];
    private readonly Dictionary<uint, double> _lastAlt = [];
    private readonly Dictionary<uint, double> _alt = [];
    private double _lastTimeS = double.NaN;

    /// <summary>El mensaje en UTF-8 (válido hasta la próxima llamada).</summary>
    /// <param name="route">La ruta del plan del piloto (fijos con nombre), o null.</param>
    public ReadOnlyMemory<byte> Build(EfbOwnship? me, IReadOnlyList<EfbTarget> traffic, double nowS,
        IReadOnlyList<(GeoPoint Position, string Name)>? route = null)
    {
        _buffer.Clear();
        _near.Clear();
        var dt = double.IsNaN(_lastTimeS) ? 0 : nowS - _lastTimeS;
        using var w = new Utf8JsonWriter(_buffer);
        w.WriteStartObject();
        w.WriteNumber("v", 1);
        if (me is { } own)
        {
            w.WriteStartObject("me");
            w.WriteNumber("lat", Math.Round(own.Position.LatDeg, 6));
            w.WriteNumber("lon", Math.Round(own.Position.LonDeg, 6));
            w.WriteNumber("hdg", Math.Round(own.HeadingDeg));
            w.WriteNumber("alt", Math.Round(own.AltitudeFt));
            w.WriteNumber("gs", Math.Round(own.SpeedKt));
            w.WriteBoolean("gnd", own.OnGround);
            w.WriteEndObject();
            foreach (var t in traffic)
            {
                var nm = GeoMath.DistanceM(own.Position, t.Position) / UnitConversions.MetersPerNauticalMile;
                if (nm <= RangeNm)
                    _near.Add((t, nm, GeoMath.InitialBearingDeg(own.Position, t.Position)));
            }
            _near.Sort(static (a, b) => a.Nm.CompareTo(b.Nm));
            if (_near.Count > MaxTargets)
                _near.RemoveRange(MaxTargets, _near.Count - MaxTargets);
        }
        w.WriteStartArray("traffic");
        _alt.Clear();
        foreach (var (t, nm, bearing) in _near)
        {
            _alt[t.Id] = t.AltitudeFt;
            var trend = 0;
            if (dt > 0.2 && _lastAlt.TryGetValue(t.Id, out var before))
            {
                var fpm = (t.AltitudeFt - before) / dt * 60;
                trend = fpm > TrendFpm ? 1 : fpm < -TrendFpm ? -1 : 0;
            }
            w.WriteStartObject();
            w.WriteNumber("id", t.Id);
            w.WriteString("cs", t.Callsign);
            if (t.Type is not null)
                w.WriteString("type", t.Type);
            w.WriteNumber("lat", Math.Round(t.Position.LatDeg, 6));
            w.WriteNumber("lon", Math.Round(t.Position.LonDeg, 6));
            w.WriteNumber("hdg", Math.Round(t.HeadingDeg));
            w.WriteNumber("alt", Math.Round(t.AltitudeFt));
            w.WriteNumber("gs", Math.Round(t.SpeedKt));
            w.WriteNumber("nm", Math.Round(nm, 1));
            w.WriteNumber("brg", Math.Round(bearing));
            w.WriteNumber("trend", trend);
            w.WriteBoolean("gnd", t.OnGround);
            w.WriteBoolean("parked", t.Parked);
            if (t.Origin is { Length: > 0 } o)
                w.WriteString("from", o);
            if (t.Destination is { Length: > 0 } d)
                w.WriteString("to", d);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (route is { Count: > 1 })
        {
            // La ruta del plan (ADR-080): [lat, lon, nombre] por punto.
            w.WriteStartArray("route");
            foreach (var (p, name) in route)
            {
                w.WriteStartArray();
                w.WriteNumberValue(Math.Round(p.LatDeg, 5));
                w.WriteNumberValue(Math.Round(p.LonDeg, 5));
                w.WriteStringValue(name);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
        w.Flush();
        // La tendencia se mide contra la última muestra de cada avión.
        _lastAlt.Clear();
        foreach (var (id, alt) in _alt)
            _lastAlt[id] = alt;
        _lastTimeS = nowS;
        return _buffer.WrittenMemory;
    }
}
