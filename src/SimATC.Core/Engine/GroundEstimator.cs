namespace SimATC.Core.Engine;

/// <summary>
/// Estima la altura del suelo bajo un avión a partir de muestras (altura − AGL medidos del propio avión),
/// suavizadas y descartando saltos bruscos (ADR-017: en SCEL hay puntos donde el AGL salta ~1,4 ft).
/// </summary>
public struct GroundEstimator
{
    /// <summary>Peso de cada muestra nueva en el promedio móvil.</summary>
    public const double Smoothing = 0.25;

    /// <summary>Saltos mayores que esto (ft) se toman como ruido... salvo que se repitan.</summary>
    public const double OutlierFt = 0.8;

    /// <summary>Muestras atípicas seguidas que hacen aceptar el nuevo nivel (el terreno cambió de verdad).</summary>
    public const int OutliersToAccept = 3;

    private bool _initialized;
    private int _outliers;

    public double GroundFt { get; private set; }

    public void Reset(double groundFt)
    {
        GroundFt = groundFt;
        _initialized = true;
        _outliers = 0;
    }

    /// <summary>Agrega una muestra: altura del avión y su AGL medido, en pies.</summary>
    public void AddSample(double altitudeFt, double aglFt)
    {
        var sample = altitudeFt - aglFt;
        if (!_initialized)
        {
            Reset(sample);
            return;
        }
        if (Math.Abs(sample - GroundFt) > OutlierFt)
        {
            if (++_outliers < OutliersToAccept)
                return;
            GroundFt = sample; // varias seguidas: es un cambio real de nivel
            _outliers = 0;
            return;
        }
        _outliers = 0;
        GroundFt += (sample - GroundFt) * Smoothing;
    }
}
