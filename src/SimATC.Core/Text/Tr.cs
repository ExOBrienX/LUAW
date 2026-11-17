using System.Globalization;
using System.Text.Json;

namespace SimATC.Core.Text;

/// <summary>
/// Idioma de la interfaz (no el de la radio, que es <see cref="Comms.AtcLanguage"/>). Los textos se escriben en español en
/// el código y se traducen con <c>data/i18n/{idioma}.json</c> (texto en español → traducción). Lo que no está traducido se
/// muestra en español. Se elige al abrir la app; cambiarlo pide reiniciar.
/// </summary>
public static class Tr
{
    private static Dictionary<string, string> _table = new(StringComparer.Ordinal);

    /// <summary>"es" o "en".</summary>
    public static string Language { get; private set; } = "es";

    public static bool IsSpanish => Language == "es";

    public static string PathFor(string dataDir, string language) => Path.Combine(dataDir, "i18n", language + ".json");

    /// <summary>Sin idioma elegido: el de Windows (español si es español; si no, inglés).</summary>
    public static string Resolve(string? setting) =>
        setting is "es" or "en" ? setting : CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";

    /// <summary>Carga la tabla del idioma (en español no hace falta). Sin archivo o ilegible: todo queda en español.</summary>
    public static void Load(string dataDir, string language, Action<string>? log = null)
    {
        Language = language;
        _table = new Dictionary<string, string>(StringComparer.Ordinal);
        if (language == "es")
            return;
        var path = PathFor(dataDir, language);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } t)
                _table = new Dictionary<string, string>(t, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Traducciones ilegibles en {path} ({ex.Message}); la interfaz queda en español.");
        }
    }

    /// <summary>Solo para tests: una tabla en memoria.</summary>
    public static void Use(string language, IReadOnlyDictionary<string, string> table)
    {
        Language = language;
        _table = new Dictionary<string, string>(table, StringComparer.Ordinal);
    }

    /// <summary>El texto en el idioma de la interfaz.</summary>
    public static string T(string spanish) => _table.TryGetValue(spanish, out var t) && t.Length > 0 ? t : spanish;

    /// <summary>Texto con valores: el formato en español ("Pista {0} en uso") se traduce y después se llena.</summary>
    public static string F(string spanishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(spanishFormat), args);
}
