using System.Globalization;
using System.Text.Json;

namespace SimATC.Core;

/// <summary>
/// Aviso de versión nueva (ADR-161): al abrir, como mucho una vez cada <see cref="Interval"/>, se consulta la última versión
/// publicada (Release de GitHub) y, si es más nueva, se invita a bajarla desde flightsim.to. La app no descarga nada sola
/// (reglas de flightsim.to: las actualizaciones se bajan desde ahí).
/// </summary>
public static class UpdateCheck
{
    /// <summary>Última versión publicada (API pública de GitHub, sin cuenta).</summary>
    public const string LatestReleaseApi = "https://api.github.com/repos/ExOBrienX/LUAW/releases/latest";

    /// <summary>Donde se baja la versión nueva.</summary>
    public const string DownloadPage = "https://flightsim.to/addon/119356/luaw-atc-and-ai-traffic-for-msfs-2024-beta";

    /// <summary>Cada cuánto se consulta, como mucho.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(20);

    /// <summary>¿Toca consultar? (nunca se consultó, o pasó <see cref="Interval"/>; un reloj atrasado también consulta).</summary>
    public static bool Due(DateTime? lastCheckUtc, DateTime nowUtc) =>
        lastCheckUtc is not { } last || nowUtc - last >= Interval || nowUtc < last;

    /// <summary>La versión del Release ("tag_name", con o sin "v"), o null si la respuesta no la trae.</summary>
    public static string? LatestVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("tag_name", out var tag)
                && tag.GetString() is { Length: > 0 } t)
                return t.TrimStart('v', 'V');
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>
    /// ¿<paramref name="latest"/> es más nueva que <paramref name="current"/>? Versiones semánticas ("0.9.3-beta.1"):
    /// primero los números; con los mismos números, la final gana a la preliminar y las preliminares se comparan por partes
    /// (números como números). Una versión que no se entiende nunca es "más nueva".
    /// </summary>
    public static bool IsNewer(string latest, string current) =>
        Parse(latest) is { } l && Parse(current) is { } c && Compare(l, c) > 0;

    private static (int[] Core, string[] Pre)? Parse(string version)
    {
        var v = version.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = v.IndexOf('-', StringComparison.Ordinal);
        var core = (dash >= 0 ? v[..dash] : v).Split('.');
        var nums = new int[3];
        if (core.Length is < 1 or > 3)
            return null;
        for (var i = 0; i < core.Length; i++)
        {
            if (!int.TryParse(core[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
                return null;
        }
        return (nums, dash >= 0 ? v[(dash + 1)..].Split('.') : []);
    }

    private static int Compare((int[] Core, string[] Pre) a, (int[] Core, string[] Pre) b)
    {
        for (var i = 0; i < 3; i++)
        {
            if (a.Core[i] != b.Core[i])
                return a.Core[i].CompareTo(b.Core[i]);
        }
        if (a.Pre.Length == 0 || b.Pre.Length == 0)
            return b.Pre.Length.CompareTo(a.Pre.Length); // sin preliminar gana
        for (var i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            var an = int.TryParse(a.Pre[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x);
            var bn = int.TryParse(b.Pre[i], NumberStyles.None, CultureInfo.InvariantCulture, out var y);
            var cmp = an && bn ? x.CompareTo(y) : an ? -1 : bn ? 1 : string.CompareOrdinal(a.Pre[i], b.Pre[i]);
            if (cmp != 0)
                return cmp;
        }
        return a.Pre.Length.CompareTo(b.Pre.Length);
    }
}

/// <summary>Lo que se recuerda del aviso de versión nueva (<c>%LOCALAPPDATA%\LUAW\update-check.json</c>).</summary>
public sealed record UpdateState(DateTime? LastCheckUtc = null, string? Latest = null, string? Dismissed = null)
{
    public static string DefaultPath => Path.Combine(AppPaths.Root, "update-check.json");

    public static UpdateState Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin guardar: se vuelve a consultar la próxima vez.
        }
    }
}
