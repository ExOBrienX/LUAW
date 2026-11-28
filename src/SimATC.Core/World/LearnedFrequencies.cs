using System.Globalization;
using System.IO.Compression;

using SimATC.Core.Airports;
using SimATC.Core.Text;

namespace SimATC.Core.World;

/// <summary>
/// Frecuencias públicas de Aproximación, Salidas y Centro por aeropuerto (OurAirports, Unlicense; ADR-173), generadas con
/// <c>tools/data-gen/airport_frequencies.py</c>. Solo de respaldo: el simulador y lo visto en VATSIM van primero.
/// </summary>
public sealed class PublicFrequencies
{
    private readonly Dictionary<string, List<(FrequencyType Type, int Khz)>> _byIcao = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byIcao.Count;

    public static string PathFor(string dataDir) => Path.Combine(dataDir, "world", "airport-frequencies.csv.gz");

    /// <summary>Lee el archivo; si no está o está dañado, queda vacío (no hay respaldo).</summary>
    public static PublicFrequencies Load(string path)
    {
        var p = new PublicFrequencies();
        try
        {
            if (!File.Exists(path))
                return p;
            using var file = File.OpenRead(path);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            reader.ReadLine(); // encabezado
            while (reader.ReadLine() is { } line)
                p.AddLine(line);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            p._byIcao.Clear();
        }
        return p;
    }

    /// <summary>Una línea "ident,role,khz,name" (también para tests).</summary>
    public void AddLine(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 3 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var khz))
            return;
        FrequencyType? type = parts[1] switch
        {
            "Approach" => FrequencyType.Approach,
            "Departure" => FrequencyType.Departure,
            "Center" => FrequencyType.Center,
            _ => null,
        };
        if (type is not { } t)
            return;
        if (!_byIcao.TryGetValue(parts[0], out var list))
            _byIcao[parts[0]] = list = [];
        list.Add((t, khz));
    }

    /// <summary>La primera frecuencia de ese tipo publicada para el aeropuerto, o null.</summary>
    public int? KhzFor(string icao, FrequencyType type)
    {
        if (!_byIcao.TryGetValue(icao, out var list))
            return null;
        foreach (var (t, khz) in list)
        {
            if (t == type)
                return khz;
        }
        return null;
    }
}

/// <summary>
/// Completa las frecuencias de un aeropuerto que el simulador no trae (ADR-173: EGLL en MSFS 2024 no tiene Aproximación ni
/// Salidas, el Centro de Londres no se conocía y el piloto voló hasta Frankfurt sin ATC). Por orden: el simulador, lo
/// visto en VATSIM y la tabla pública. Sin ninguna, queda como está (el vuelo sigue sin ATC hasta que aparezca alguien).
/// </summary>
public static class LearnedFrequencies
{
    public static AirportData Augment(AirportData airport, VatsimCenterStore? vatsim, PublicFrequencies? pub, Action<string>? log = null)
    {
        var extra = new List<Frequency>();
        // El nombre de las del propio aeropuerto ("HEATHROW"): así se llaman "Heathrow Approach" como las demás.
        var name = airport.Frequencies.FirstOrDefault(f => f.Type is FrequencyType.Tower or FrequencyType.Ground or FrequencyType.Clearance)?.Name
                   ?? airport.Name;
        foreach (var type in (ReadOnlySpan<FrequencyType>)[FrequencyType.Approach, FrequencyType.Departure])
        {
            if (Has(airport, type))
                continue;
            var fromVatsim = vatsim?.KhzForAirport(airport.Icao, type == FrequencyType.Approach ? "APP" : "DEP");
            var khz = fromVatsim ?? pub?.KhzFor(airport.Icao, type);
            if (khz is not { } k)
                continue;
            extra.Add(new Frequency(type, k, name));
            log?.Invoke(Tr.F("{0}: sin {1} en el simulador; se usa {2:0.000} ({3}).", airport.Icao, type, k / 1000.0,
                fromVatsim is not null ? Tr.T("vista en VATSIM") : Tr.T("tabla pública")));
        }
        // Sin Aproximación ni Salidas: el Centro publicado para el aeropuerto, si lo hay.
        if (extra.Count == 0 && !Has(airport, FrequencyType.Approach) && !Has(airport, FrequencyType.Departure)
            && !Has(airport, FrequencyType.Center) && pub?.KhzFor(airport.Icao, FrequencyType.Center) is { } center)
        {
            extra.Add(new Frequency(FrequencyType.Center, center, name));
            log?.Invoke(Tr.F("{0}: sin {1} en el simulador; se usa {2:0.000} ({3}).", airport.Icao, FrequencyType.Center, center / 1000.0,
                Tr.T("tabla pública")));
        }
        return extra.Count == 0 ? airport : airport with { Frequencies = [.. airport.Frequencies, .. extra] };
    }

    private static bool Has(AirportData airport, FrequencyType type) => airport.Frequencies.Any(f => f.Type == type);
}
