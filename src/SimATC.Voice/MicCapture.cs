using NAudio.Wave;

namespace SimATC.Voice;

/// <summary>
/// El micrófono del piloto (etapa 5c, ADR-126): captura a 16 kHz mono (lo que pide el reconocedor) con NAudio (winmm),
/// informa el nivel de cada bloque para la barra y guarda las muestras mientras se graba. Solo está abierto cuando hace
/// falta (hablar, probar o ver el nivel en Configuración).
/// </summary>
public sealed class MicCapture : IDisposable
{
    public const int SampleRate = 16000;

    private readonly Lock _lock = new();
    private readonly List<float> _recorded = [];
    private WaveInEvent? _input;
    private bool _recording;

    /// <summary>Ganancia aplicada a lo capturado (1 = sin cambio).</summary>
    public float Gain { get; set; } = 1f;

    /// <summary>El pico de cada bloque (~50 ms), de 0 a 1, ya con la ganancia. Llega desde el hilo del audio.</summary>
    public event Action<float>? Level;

    public bool IsOpen => _input is not null;

    /// <summary>El dispositivo que quedó abierto (el elegido, o el predeterminado si no estaba).</summary>
    public string? OpenDevice { get; private set; }

    /// <summary>
    /// Los micrófonos de Windows, por nombre. winmm corta los nombres a 31 caracteres: se usan tal cual para elegirlo.
    /// </summary>
    public static IReadOnlyList<string> Devices()
    {
        var list = new List<string>();
        try
        {
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
                list.Add(WaveInEvent.GetCapabilities(i).ProductName);
        }
        catch (Exception ex) when (ex is NAudio.MmException or DllNotFoundException)
        {
            // Sin dispositivos de audio: lista vacía.
        }
        return list;
    }

    /// <summary>Abre el micrófono <paramref name="deviceName"/> (o el predeterminado) y empieza a medir el nivel.</summary>
    /// <exception cref="InvalidOperationException">No hay micrófono o Windows no deja abrirlo.</exception>
    public void Open(string? deviceName)
    {
        Close();
        var devices = Devices();
        if (devices.Count == 0)
            throw new InvalidOperationException("Windows no informa ningún micrófono.");
        var index = deviceName is null ? -1 : devices.ToList().IndexOf(deviceName);
        // -1 es el "mapeador" de Windows: el micrófono predeterminado.
        var input = new WaveInEvent { DeviceNumber = index, WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 50 };
        input.DataAvailable += OnData;
        try
        {
            input.StartRecording();
        }
        catch (NAudio.MmException ex)
        {
            input.Dispose();
            throw new InvalidOperationException($"No se pudo abrir el micrófono ({ex.Result}).", ex);
        }
        _input = input;
        OpenDevice = index >= 0 ? devices[index] : "predeterminado de Windows";
    }

    public void Close()
    {
        var input = _input;
        _input = null;
        OpenDevice = null;
        if (input is null)
            return;
        input.DataAvailable -= OnData;
        try
        {
            input.StopRecording();
        }
        catch (NAudio.MmException)
        {
            // El dispositivo se desconectó: no hay nada que parar.
        }
        input.Dispose();
        lock (_lock)
            _recording = false;
    }

    /// <summary>Desde ahora lo que entra se guarda (hasta <see cref="EndRecording"/>).</summary>
    public void BeginRecording()
    {
        lock (_lock)
        {
            _recorded.Clear();
            _recording = true;
        }
    }

    /// <summary>Deja de guardar y devuelve lo grabado (muestras de -1 a 1, a <see cref="SampleRate"/> Hz).</summary>
    public float[] EndRecording()
    {
        lock (_lock)
        {
            _recording = false;
            var samples = _recorded.ToArray();
            _recorded.Clear();
            return samples;
        }
    }

    /// <summary>Copia de lo grabado hasta ahora, sin dejar de grabar (para mostrar lo que se va entendiendo).</summary>
    public float[] Snapshot()
    {
        lock (_lock)
            return _recorded.ToArray();
    }

    /// <summary>Segundos grabados hasta ahora.</summary>
    public double RecordedS
    {
        get
        {
            lock (_lock)
                return _recorded.Count / (double)SampleRate;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var gain = Gain;
        var peak = 0f;
        var count = e.BytesRecorded / 2;
        lock (_lock)
        {
            for (var i = 0; i < count; i++)
            {
                var s = Math.Clamp((short)(e.Buffer[2 * i] | e.Buffer[2 * i + 1] << 8) / 32768f * gain, -1f, 1f);
                var a = Math.Abs(s);
                if (a > peak)
                    peak = a;
                if (_recording)
                    _recorded.Add(s);
            }
        }
        Level?.Invoke(peak);
    }

    /// <summary>
    /// El pico (0 a 1) en la escala de la barra de nivel (0 a 100): de -50 dB a 0 dB, que es como se oye (una voz normal
    /// queda hacia la mitad y no pegada al piso).
    /// </summary>
    public static double Meter(float peak) =>
        peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 50) * 2, 0, 100);

    public void Dispose() => Close();
}
