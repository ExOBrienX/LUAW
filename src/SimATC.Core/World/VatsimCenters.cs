using System.Globalization;
using System.Text.Json;

using SimATC.Core.Text;

namespace SimATC.Core.World;

/// <summary>
/// Frecuencias de Centro vistas en VATSIM (ADR-069/071): de cada controlador "_CTR" del feed se guarda su frecuencia por
/// indicativo ("SCEZ" → 128.3), con cuántas veces se la vio. Crece entre sesiones como las rutas. Datos de VATSIM: uso no
/// comercial. Sin frecuencia vista no hay Centro (no se inventa). ADR-173: también Aproximación y Salidas ("EGLL_N_APP"
/// se guarda con su sufijo), para los aeropuertos que no las traen en el simulador; y la app trae una semilla con las que
/// se juntaron al preparar la versión (<see cref="SeedPathFor"/>), así un usuario nuevo no empieza de cero.
/// </summary>
public sealed class VatsimCenterStore
{
    /// <summary>Facility 6 del feed v3: "CTR, Enroute".</summary>
    private const int EnrouteFacility = 6;

    /// <summary>Facility 5 del feed v3: "APP, Approach/Departure".</summary>
    private const int ApproachFacility = 5;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Indicativo sin "_CTR" ("SCEZ", "SCEZ_N", "MIA_06") → frecuencia en kHz (texto) → veces vista.
    private readonly Dictionary<string, Dictionary<string, int>> _seen = new(StringComparer.Ordinal);

    // El feed se lee en segundo plano y la sesión consulta desde la UI.
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
                return _seen.Count;
        }
    }

    public static string PathFor(string dir) => Path.Combine(dir, "vatsim-centers.json");

    /// <summary>La semilla que trae la app (data/world): frecuencias vistas al preparar la versión.</summary>
    public static string SeedPathFor(string dataDir) => Path.Combine(dataDir, "world", "vatsim-frequencies-seed.json");

    /// <param name="seedPath">Semilla de la app: lo que no está en el archivo del usuario se agrega una vez vista.</param>
    public static VatsimCenterStore Load(string path, string? seedPath = null)
    {
        var s = new VatsimCenterStore();
        s.Merge(path, overwrite: true);
        if (seedPath is not null)
            s.Merge(seedPath, overwrite: false);
        return s;
    }

    private void Merge(string path, bool overwrite)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), VatsimJson.Default.DictionaryStringDictionaryStringInt32) is { } d)
            {
                foreach (var (k, v) in d)
                {
                    if (overwrite || !_seen.ContainsKey(k))
                        _seen[k] = v;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Archivo dañado: se empieza de nuevo.
        }
    }

    public void Save(string path)
    {
        try
        {
            string json;
            lock (_lock)
                json = JsonSerializer.Serialize(_seen, VatsimJson.Default.DictionaryStringDictionaryStringInt32);
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin disco: se pierde lo de esta sesión.
        }
    }

    /// <summary>
    /// Un controlador del feed. Cuenta un Centro ("_CTR", facility 6) o una Aproximación o Salidas ("_APP"/"_DEP",
    /// facility 5) con frecuencia válida de VHF. Los Centros se guardan sin el sufijo; las otras, con él.
    /// </summary>
    public bool Add(string? callsign, string? frequencyMhz, int facility)
    {
        if (callsign is null)
            return false;
        var center = facility == EnrouteFacility && callsign.EndsWith("_CTR", StringComparison.OrdinalIgnoreCase);
        var approach = facility == ApproachFacility
                       && (callsign.EndsWith("_APP", StringComparison.OrdinalIgnoreCase) || callsign.EndsWith("_DEP", StringComparison.OrdinalIgnoreCase));
        if (!center && !approach)
            return false;
        var khz = FirMap.ParseKhz(frequencyMhz);
        if (khz is < 118_000 or > 136_990)
            return false; // 199.998 = sin frecuencia (observador o sin voz)
        var key = (center ? callsign[..^4] : callsign).ToUpperInvariant();
        if (key.Length == 0)
            return false;
        var k = khz.ToString(CultureInfo.InvariantCulture);
        lock (_lock)
        {
            if (!_seen.TryGetValue(key, out var freqs))
                _seen[key] = freqs = [];
            freqs[k] = freqs.GetValueOrDefault(k) + 1;
        }
        return true;
    }

    /// <summary>
    /// La frecuencia del Centro de esa FIR: la más vista del indicativo base (prefijo de VATSpy o el código de la FIR); si
    /// no, la más vista de sus sectores ("SCEZ_N"). null si nunca se vio.
    /// </summary>
    public int? KhzFor(Fir fir)
    {
        // VATSpy da un prefijo de indicativo por FIR ("LEMD" para LECM), pero los controladores se conectan también con el
        // código de la FIR ("LECM_CTR"): se prueban los dos, y el de la FIR sin el sector ("LECM-ZMU" → "LECM"). ADR-129:
        // el Centro de Madrid estaba guardado como LECM y nunca se encontró (se voló hasta Madrid con Málaga Aproximación).
        var prefix = fir.CallsignPrefix.ToUpperInvariant();
        var icao = fir.Icao.ToUpperInvariant();
        var dash = icao.IndexOf('-');
        lock (_lock)
        {
            return (prefix.Length > 0 ? KhzForLocked(prefix) : null) ?? KhzForLocked(icao.Replace('-', '_'))
                   ?? (dash > 0 ? KhzForLocked(icao[..dash]) : KhzForLocked(icao));
        }
    }

    private int? KhzForLocked(string key)
    {
        if (_seen.TryGetValue(key, out var exact) && Best(exact) is { } k)
            return k;
        (int Khz, int Count)? best = null;
        foreach (var (callsign, freqs) in _seen)
        {
            if (!callsign.StartsWith(key + "_", StringComparison.Ordinal) || IsApproach(callsign))
                continue;
            foreach (var (f, n) in freqs)
            {
                if (best is null || n > best.Value.Count)
                    best = (int.Parse(f, CultureInfo.InvariantCulture), n);
            }
        }
        return best?.Khz;
    }

    private static bool IsApproach(string key) =>
        key.EndsWith("_APP", StringComparison.Ordinal) || key.EndsWith("_DEP", StringComparison.Ordinal);

    /// <summary>
    /// La Aproximación ("APP") o Salidas ("DEP") de un aeropuerto vista en VATSIM: "EGLL_APP" o sus sectores ("EGLL_N_APP"),
    /// la más vista. null si nunca se vio.
    /// </summary>
    public int? KhzForAirport(string icao, string suffix)
    {
        var start = icao.ToUpperInvariant() + "_";
        var end = "_" + suffix;
        (int Khz, int Count)? best = null;
        lock (_lock)
        {
            foreach (var (callsign, freqs) in _seen)
            {
                if (!callsign.StartsWith(start, StringComparison.Ordinal) || !callsign.EndsWith(end, StringComparison.Ordinal))
                    continue;
                foreach (var (f, n) in freqs)
                {
                    if (best is null || n > best.Value.Count)
                        best = (int.Parse(f, CultureInfo.InvariantCulture), n);
                }
            }
        }
        return best?.Khz;
    }

    private static int? Best(Dictionary<string, int> freqs)
    {
        string? best = null;
        var count = 0;
        foreach (var (f, n) in freqs)
        {
            if (n > count)
                (best, count) = (f, n);
        }
        return best is null ? null : int.Parse(best, CultureInfo.InvariantCulture);
    }

    /// <summary>Lee los controladores del feed de VATSIM (el mismo de las rutas) y guarda los Centros vistos.</summary>
    public async Task<int> RefreshAsync(Action<string>? log, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(VatsimRouteStore.FeedUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return 0;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var added = 0;
            if (doc.RootElement.TryGetProperty("controllers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in arr.EnumerateArray())
                {
                    var facility = c.TryGetProperty("facility", out var fe) && fe.ValueKind == JsonValueKind.Number ? fe.GetInt32() : -1;
                    if (Add(Str(c, "callsign"), Str(c, "frequency"), facility))
                        added++;
                }
            }
            log?.Invoke(Tr.F("VATSIM: {0} Centros y Aproximaciones en línea ({1} indicativos guardados).", added, Count));
            return added;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke(Tr.F("VATSIM no disponible ({0}); se usan los Centros guardados.", ex.GetBaseException().Message));
            return 0;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
}
