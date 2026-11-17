using System.Globalization;
using SimATC.Core.Units;

namespace SimATC.Core.Fleet;

/// <summary>
/// Lee los modelos de los paquetes de tráfico instalados (FSLTL y AIG) desde sus aircraft.cfg y flight_model.cfg
/// (V-08). Solo lectura de archivos de configuración legibles; nada de ingeniería inversa.
/// </summary>
public static class FleetScanner
{
    /// <summary>Prefijos de los paquetes de tráfico que se usan (§3). Los demás paquetes se ignoran.</summary>
    private static readonly (string Prefix, ModelSource Source)[] TrafficPackages =
    [
        ("fsltl-traffic", ModelSource.Fsltl),
        ("aig-aitraffic", ModelSource.Aig),
    ];

    /// <summary>Generic de FSLTL: icao_airline = "ZZZZ" significa livery genérica.</summary>
    private const string GenericAirline = "ZZZZ";

    public static ModelSource? SourceOf(string packageName)
    {
        foreach (var (prefix, source) in TrafficPackages)
        {
            if (packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return source;
        }
        return null;
    }

    /// <summary>
    /// Paquetes de tráfico dentro de las carpetas Community dadas. Sigue los enlaces (junctions) y quita duplicados:
    /// el mismo paquete puede estar enlazado desde dos carpetas.
    /// </summary>
    public static IReadOnlyList<string> FindTrafficPackages(IEnumerable<string> communityDirs)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var community in communityDirs)
        {
            if (!Directory.Exists(community))
                continue;
            // La carpeta elegida puede ser un paquete en sí (fsltl-traffic-base) o una carpeta que los contiene.
            var dirs = SourceOf(Path.GetFileName(Path.TrimEndingDirectorySeparator(community))) is not null
                ? [community]
                : Directory.EnumerateDirectories(community);
            foreach (var dir in dirs)
            {
                if (SourceOf(Path.GetFileName(dir)) is null)
                    continue;
                var real = new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dir;
                if (seen.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(real))))
                    result.Add(dir);
            }
        }
        return result;
    }

    /// <summary>Todos los modelos (una entrada por [FLTSIM.n]) de los paquetes dados.</summary>
    public static List<AircraftModel> Scan(IEnumerable<string> packageDirs)
    {
        var models = new List<AircraftModel>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var pkg in packageDirs)
        {
            if (SourceOf(Path.GetFileName(Path.TrimEndingDirectorySeparator(pkg))) is not { } source)
                continue;
            foreach (var cfgPath in Directory.EnumerateFiles(pkg, "aircraft.cfg", options))
                ReadAircraftCfg(cfgPath, source, models);
        }
        return models;
    }

    private static void ReadAircraftCfg(string cfgPath, ModelSource source, List<AircraftModel> models)
    {
        var dir = Path.GetDirectoryName(cfgPath)!;
        var cfg = CfgFile.Parse(File.ReadAllText(cfgPath));

        // Las liveries en paquetes aparte apuntan al modelo base con [VARIATION] base_container.
        var baseDir = dir;
        var baseCfg = cfg;
        if (cfg.Get("VARIATION", "base_container") is { Length: > 0 } baseContainer)
        {
            baseDir = Path.GetFullPath(Path.Combine(dir, baseContainer));
            var basePath = Path.Combine(baseDir, "aircraft.cfg");
            if (File.Exists(basePath))
                baseCfg = CfgFile.Parse(File.ReadAllText(basePath));
        }

        var type = cfg.Get("GENERAL", "icao_type_designator") ?? baseCfg.Get("GENERAL", "icao_type_designator") ?? "";
        var wake = AircraftModel.ParseWake(cfg.Get("GENERAL", "icao_WTC") ?? baseCfg.Get("GENERAL", "icao_WTC"));
        var engines = ParseInt(cfg.Get("GENERAL", "icao_engine_count") ?? baseCfg.Get("GENERAL", "icao_engine_count"));
        var wingspan = ReadWingspanM(dir) ?? ReadWingspanM(baseDir) ?? 0;
        var noseGear = ReadNoseGearM(dir) ?? ReadNoseGearM(baseDir) ?? 0;

        foreach (var section in cfg.Sections)
        {
            if (!section.StartsWith("FLTSIM.", StringComparison.OrdinalIgnoreCase))
                continue;
            var title = cfg.Get(section, "title");
            if (string.IsNullOrWhiteSpace(title))
                continue;
            var airline = (cfg.Get(section, "icao_airline") ?? "").Trim().ToUpperInvariant();
            if (airline == GenericAirline)
                airline = "";
            models.Add(new AircraftModel(title.Trim(), source, type.Trim().ToUpperInvariant(), airline, wake, engines,
                wingspan, SplitList(cfg.Get(section, "atc_parking_types")), SplitList(cfg.Get(section, "atc_parking_codes")),
                noseGear));
        }
    }

    /// <summary>wing_span de [AIRPLANE_GEOMETRY] en flight_model.cfg (en pies) convertido a metros.</summary>
    private static double? ReadWingspanM(string dir)
    {
        var path = Path.Combine(dir, "flight_model.cfg");
        if (!File.Exists(path))
            return null;
        var value = CfgFile.Parse(File.ReadAllText(path)).Get("AIRPLANE_GEOMETRY", "wing_span");
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var feet) && feet > 0
            ? UnitConversions.FeetToMeters(feet)
            : null;
    }

    /// <summary>
    /// Distancia (m) de la rueda de nariz por delante del punto de referencia del avión: el punto de contacto de rueda
    /// (clase 1) más adelantado sobre el eje, en [CONTACT_POINTS] de flight_model.cfg (pies, relativo al datum), más la
    /// posición del datum ([WEIGHT_AND_BALANCE] reference_datum_position). Es donde se engancha el remolcador.
    /// </summary>
    internal static double? ReadNoseGearM(string dir)
    {
        var path = Path.Combine(dir, "flight_model.cfg");
        if (!File.Exists(path))
            return null;
        var cfg = CfgFile.Parse(File.ReadAllText(path));
        double? best = null;
        foreach (var (key, value) in cfg.Entries("CONTACT_POINTS"))
        {
            if (!key.StartsWith("point.", StringComparison.OrdinalIgnoreCase))
                continue;
            var f = value.Split(',');
            if (f.Length < 3 || f[0].Trim() != "1" || !TryFeet(f[1], out var longitudinal) || !TryFeet(f[2], out var lateral))
                continue;
            if (Math.Abs(lateral) < 1 && (best is null || longitudinal > best))
                best = longitudinal;
        }
        if (best is null)
            return null;
        var datum = cfg.Get("WEIGHT_AND_BALANCE", "reference_datum_position")?.Split(',');
        var datumFeet = datum is { Length: > 0 } && TryFeet(datum[0], out var d) ? d : 0;
        return UnitConversions.FeetToMeters(best.Value + datumFeet);
    }

    private static bool TryFeet(string text, out double feet) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out feet);

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static string[] SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant()).ToArray();
}
