using System.Globalization;

namespace SimATC.Core.Comms;

/// <summary>Idioma del ATC (§8.6, etapa 5a): inglés OACI por defecto, o español. Uno solo por frecuencia.</summary>
public enum AtcLanguage
{
    English,
    Spanish,
}

/// <summary>Fraseología en inglés (ADR-099): OACI, FAA, o automática (FAA en aeropuertos de EE. UU.).</summary>
public enum PhraseologyStyle
{
    Auto,
    Icao,
    Faa,
}

/// <summary>
/// Los pedazos de frase que arma el código con datos y que no son una plantilla entera (el nivel, el viento, el lado
/// por donde pasa un tráfico, cuántos salen antes), en el idioma del ATC. Las frases completas siguen en
/// <c>data/phraseology</c> (§8.1).
/// </summary>
public static class AtcWords
{
    /// <summary>Archivo de fraseología de cada idioma (<c>data/phraseology/{nombre}.json</c>).</summary>
    public static string PhraseFile(AtcLanguage lang) => lang == AtcLanguage.Spanish ? "icao-es" : "icao-en";

    /// <summary>
    /// ¿El aeropuerto es de EE. UU. (fraseología FAA en automático, ADR-099)? Por el prefijo OACI: K (contiguos), PA
    /// (Alaska), PH (Hawái), PG (Guam y Marianas), TJ (Puerto Rico), TI (Islas Vírgenes).
    /// </summary>
    public static bool IsUnitedStates(string? icao) =>
        icao is { Length: 4 } i && (i[0] is 'K' or 'k' || i[..2].ToUpperInvariant() is "PA" or "PH" or "PG" or "TJ" or "TI");

    /// <summary>¿Se usa la capa FAA? Solo en inglés: a mano, o en automático en un aeropuerto de EE. UU.</summary>
    public static bool UseFaa(AtcLanguage lang, PhraseologyStyle style, string? airportIcao) =>
        lang == AtcLanguage.English && (style == PhraseologyStyle.Faa || style == PhraseologyStyle.Auto && IsUnitedStates(airportIcao));

    /// <summary>Altímetro en pulgadas de mercurio como lo dice la FAA ("29.92"), desde el QNH en hPa.</summary>
    public static string AltimeterInHg(double qnhHpa) =>
        (qnhHpa * 0.0295299830714).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>"flight level 100" / "nivel de vuelo 100"; "5000 feet" / "5000 pies" (Doc 4444 12.3.1.2).</summary>
    public static string Level(double ft, bool isFlightLevel, AtcLanguage lang) => (isFlightLevel, lang) switch
    {
        (true, AtcLanguage.Spanish) => $"nivel de vuelo {Math.Round(ft / 100):000}",
        (true, _) => $"flight level {Math.Round(ft / 100):000}",
        (false, AtcLanguage.Spanish) => $"{Math.Round(ft).ToString("0", CultureInfo.InvariantCulture)} pies",
        _ => $"{Math.Round(ft).ToString("0", CultureInfo.InvariantCulture)} feet",
    };

    /// <summary>El viento sin la palabra "wind": "190 degrees 11 knots" / "190 grados 11 nudos"; calma.</summary>
    public static string Wind(double fromMagneticDeg, double kt, double? gustKt, AtcLanguage lang)
    {
        if (kt < 1)
            return lang == AtcLanguage.Spanish ? "calma" : "calm";
        var dir = (int)(Math.Round(fromMagneticDeg / 10) * 10) % 360;
        var d = (dir == 0 ? 360 : dir).ToString("000", CultureInfo.InvariantCulture);
        return lang == AtcLanguage.Spanish
            ? $"{d} grados {Math.Round(kt):0}{(gustKt is { } g ? $" ráfagas {Math.Round(g):0}" : "")} nudos"
            : $"{d} degrees {Math.Round(kt):0}{(gustKt is { } g2 ? $" gusting {Math.Round(g2):0}" : "")} knots";
    }

    /// <summary>Por dónde pasa un tráfico visto desde quien cede el paso.</summary>
    public static string Direction(bool leftToRight, AtcLanguage lang) => lang == AtcLanguage.Spanish
        ? leftToRight ? "de izquierda a derecha" : "de derecha a izquierda"
        : leftToRight ? "left to right" : "right to left";

    private static readonly string[] CountEn = ["no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
    private static readonly string[] CountEs = ["ninguna", "una", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve"];

    /// <summary>Cuántas aeronaves: "two aircraft" / "dos aeronaves" (con la concordancia del español).</summary>
    public static string Aircraft(int n, AtcLanguage lang)
    {
        var i = Math.Clamp(n, 0, CountEn.Length - 1);
        return lang == AtcLanguage.Spanish
            ? $"{CountEs[i]} {(i == 1 ? "aeronave" : "aeronaves")}"
            : $"{CountEn[i]} aircraft";
    }
}
