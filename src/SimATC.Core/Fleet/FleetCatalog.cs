using System.Text.Json;
using System.Text.Json.Serialization;
using SimATC.Core.Simulation;

using SimATC.Core.Text;

namespace SimATC.Core.Fleet;

/// <summary>Huella de un paquete escaneado: si cambia, se vuelve a escanear.</summary>
public sealed record PackageStamp(string Path, DateTime StampUtc);

public sealed record FleetCacheFile(int SchemaVersion, IReadOnlyList<PackageStamp> Packages, IReadOnlyList<AircraftModel> Models);

[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true)]
[JsonSerializable(typeof(FleetCacheFile))]
internal sealed partial class FleetJsonContext : JsonSerializerContext;

/// <summary>
/// Arma el <see cref="ModelMatcher"/> con los modelos que el simulador puede crear (RF-15), los datos de los
/// paquetes FSLTL/AIG (en caché, porque leerlos toma varios segundos) y las reglas de FSLTL.
/// </summary>
public static class FleetCatalog
{
    public const int SchemaVersion = 2; // 2: rueda de nariz
    public const string CacheFileName = "fleet-models.json";

    /// <param name="extraFolders">Carpetas que agregó el usuario (Configuración), además de las Community de UserCfg.opt.</param>
    public static async Task<ModelMatcher> LoadAsync(ISimBridge bridge, string cacheDir, Action<string> log, CancellationToken ct,
        IReadOnlyList<string>? extraFolders = null)
    {
        var titles = await bridge.EnumerateAircraftTitlesAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var packages = FleetScanner.FindTrafficPackages([.. MsfsInstall.FindCommunityFolders(), .. extraFolders ?? []]);
            var models = LoadModels(packages, cacheDir, log);
            var rulesPath = packages.Select(p => Path.Combine(p, FsltlRules.FileName)).FirstOrDefault(File.Exists);
            List<FsltlRule> rules;
            if (rulesPath is null)
            {
                rules = [];
                log(Tr.T("No se encontró FSLTL_Rules.vmr: sin reglas de FSLTL."));
            }
            else
            {
                using var reader = File.OpenText(rulesPath);
                rules = FsltlRules.Parse(reader);
            }

            var spawnable = new HashSet<string>(titles, StringComparer.OrdinalIgnoreCase);
            var usable = models.Where(m => spawnable.Contains(m.Title)).ToList();
            log(Tr.F("Catálogo: {0} títulos en el simulador, {1} modelos de tráfico, {2} utilizables, {3} reglas FSLTL.", titles.Count, models.Count, usable.Count, rules.Count));
            return new ModelMatcher(usable, rules) { SimTitles = spawnable };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Modelos de los paquetes, desde la caché si ningún paquete cambió.</summary>
    public static List<AircraftModel> LoadModels(IReadOnlyList<string> packages, string cacheDir, Action<string> log)
    {
        var stamps = packages.Select(Stamp).ToList();
        var path = Path.Combine(cacheDir, CacheFileName);
        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize(File.ReadAllText(path), FleetJsonContext.Default.FleetCacheFile) is { } cached &&
                cached.SchemaVersion == SchemaVersion && cached.Packages.SequenceEqual(stamps))
                return cached.Models.ToList();
        }
        catch (JsonException)
        {
            // Caché dañada: se vuelve a escanear.
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var models = FleetScanner.Scan(packages);
        log(Tr.F("Paquetes de tráfico escaneados: {0} modelos en {1} ms.", models.Count, sw.ElapsedMilliseconds));
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(path, JsonSerializer.Serialize(new FleetCacheFile(SchemaVersion, stamps, models), FleetJsonContext.Default.FleetCacheFile));
        return models;
    }

    /// <summary>Fecha de su layout.json (lo reescribe el instalador al actualizar el paquete) o de la carpeta.</summary>
    private static PackageStamp Stamp(string package)
    {
        var layout = Path.Combine(package, "layout.json");
        var time = File.Exists(layout) ? File.GetLastWriteTimeUtc(layout) : Directory.GetLastWriteTimeUtc(package);
        return new PackageStamp(package, time);
    }
}
