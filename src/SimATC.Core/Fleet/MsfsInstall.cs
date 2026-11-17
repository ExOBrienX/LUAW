namespace SimATC.Core.Fleet;

/// <summary>
/// Ubica las carpetas Community de MSFS 2024 a partir de <c>InstalledPackagesPath</c> en UserCfg.opt
/// (versión Microsoft Store o Steam).
/// </summary>
public static class MsfsInstall
{
    private static readonly string[] UserCfgCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft Flight Simulator 2024", "UserCfg.opt"),
    ];

    /// <summary>Carpetas Community existentes ("Community" y "Community2024" bajo cada InstalledPackagesPath).</summary>
    public static IReadOnlyList<string> FindCommunityFolders()
    {
        var result = new List<string>();
        foreach (var cfg in UserCfgCandidates)
        {
            if (!File.Exists(cfg) || ReadInstalledPackagesPath(File.ReadAllText(cfg)) is not { } packages)
                continue;
            foreach (var name in new[] { "Community", "Community2024" })
            {
                var dir = Path.Combine(packages, name);
                if (Directory.Exists(dir) && !result.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    result.Add(dir);
            }
        }
        return result;
    }

    /// <summary>Lee la línea <c>InstalledPackagesPath "C:\..."</c> de UserCfg.opt.</summary>
    public static string? ReadInstalledPackagesPath(string userCfg)
    {
        foreach (var raw in userCfg.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase))
                continue;
            var first = line.IndexOf('"');
            var last = line.LastIndexOf('"');
            return first >= 0 && last > first ? line[(first + 1)..last] : null;
        }
        return null;
    }
}
