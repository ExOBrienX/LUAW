using SimATC.Core.Geo;

namespace SimATC.Core.Atc;

/// <summary>Viento para el ATIS y la pista: medio de 2 minutos y ráfaga, si la hay.</summary>
public readonly record struct AveragedWind(double FromTrueDeg, double MeanKt, double? GustKt, int Samples);

/// <summary>
/// Promedia el viento como un ATIS real (OACI Anexo 3; ADR-028): media **vectorial** de los últimos 2 minutos, y ráfaga
/// = la mayor media de 3 s de los últimos 10 minutos si supera la media en 10 kt o más. Las muestras llegan cada ~1 s desde
/// el simulador (viento instantáneo en la posición del piloto). Buffer fijo: sin asignaciones después de crearse.
/// </summary>
public sealed class WindAverager
{
    public const double MeanWindowS = 120;
    public const double GustWindowS = 600;
    public const double GustThresholdKt = 10;

    private readonly (double T, double U, double V, double Kt)[] _buf;
    private int _start;
    private int _count;

    public WindAverager(int capacity = 1200) => _buf = new (double, double, double, double)[capacity];

    /// <summary>Una muestra: viento desde <paramref name="fromTrueDeg"/> a <paramref name="kt"/>, en el instante <paramref name="t"/> (s).</summary>
    public void Add(double t, double fromTrueDeg, double kt)
    {
        var rad = fromTrueDeg * GeoMath.DegToRad;
        // Se suma el vector "desde": la dirección media resultante sigue siendo "desde".
        var sample = (t, kt * Math.Sin(rad), kt * Math.Cos(rad), kt);
        if (_count == _buf.Length)
        {
            _buf[_start] = sample;
            _start = (_start + 1) % _buf.Length;
        }
        else
        {
            _buf[(_start + _count) % _buf.Length] = sample;
            _count++;
        }
        // Lo más viejo que la ventana de ráfagas ya no sirve.
        while (_count > 0 && t - _buf[_start].T > GustWindowS)
        {
            _start = (_start + 1) % _buf.Length;
            _count--;
        }
    }

    public void Clear() => _start = _count = 0;

    /// <summary>Media de 2 min y ráfaga de 10 min hasta el instante <paramref name="now"/>; null si no hay muestras.</summary>
    public AveragedWind? Current(double now)
    {
        double su = 0, sv = 0, peak = 0;
        var n = 0;
        // Ráfaga: la mayor velocidad media de 3 s (OMM/Anexo 3), no una muestra suelta: con muestras de ~1 s, la media de
        // 3 consecutivas. Una muestra instantánea incluye la turbulencia del simulador y exageraría la ráfaga.
        double k1 = 0, k2 = 0;
        for (var i = 0; i < _count; i++)
        {
            var (t, u, v, kt) = _buf[(_start + i) % _buf.Length];
            if (now - t <= GustWindowS && i >= 2)
            {
                var three = (kt + k1 + k2) / 3;
                if (three > peak)
                    peak = three;
            }
            (k2, k1) = (k1, kt);
            if (now - t > MeanWindowS)
                continue;
            su += u;
            sv += v;
            n++;
        }
        if (n == 0)
            return null;
        // Velocidad: módulo del vector medio (un viento que cambia de dirección promedia menos que su rapidez media).
        var mean = Math.Sqrt(su * su + sv * sv) / n;
        var from = GeoMath.NormalizeDeg(Math.Atan2(su, sv) / GeoMath.DegToRad);
        double? gust = peak >= mean + GustThresholdKt ? peak : null;
        return new AveragedWind(from, mean, gust, n);
    }
}
