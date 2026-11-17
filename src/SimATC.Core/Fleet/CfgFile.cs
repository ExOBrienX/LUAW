namespace SimATC.Core.Fleet;

/// <summary>
/// Lector de los archivos .cfg de MSFS (formato INI): <c>[SECCIÓN]</c>, <c>clave = valor</c>, comentarios desde ';'
/// (fuera de comillas). Las comillas que rodean un valor se quitan. Claves y secciones sin distinguir mayúsculas.
/// </summary>
public sealed class CfgFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Sections => _sections.Keys;

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var v) ? v : null;

    /// <summary>Todas las claves de una sección (vacío si no existe).</summary>
    public IEnumerable<KeyValuePair<string, string>> Entries(string section) =>
        _sections.TryGetValue(section, out var values) ? values : [];

    public static CfgFile Parse(string text)
    {
        var cfg = new CfgFile();
        Dictionary<string, string>? current = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;
            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                var name = (end > 0 ? line[1..end] : line[1..]).Trim();
                if (!cfg._sections.TryGetValue(name, out current))
                    cfg._sections[name] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0 || current is null)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            current[key] = value.Trim();
        }
        return cfg;
    }

    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inQuotes = !inQuotes;
            else if (line[i] == ';' && !inQuotes)
                return line[..i];
        }
        return line;
    }
}
