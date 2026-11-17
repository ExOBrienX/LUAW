using System.Text.RegularExpressions;
using SimATC.Core.Comms;

namespace SimATC.Voice;

/// <summary>
/// El texto tal como lo tiene que leer la voz (solo para el TTS; el chat no cambia). Piper (eSpeak) deletrea las palabras
/// cortas en mayúsculas ("LAN CHILE" → "ele-a-ene Chile", "TAM" → "T-A-M"; prueba con <c>--debug</c>, 2026-11-01): la
/// telefonía va en minúscula normal, salvo las que de verdad se deletrean. Y en español el alfabeto OACI se escribe como
/// se pronuncia ("Juliett" salía con jota y "Echo" con "ch").
/// </summary>
public static partial class TtsText
{
    /// <summary>Telefonías que por radio se dicen letra por letra.</summary>
    private static readonly HashSet<string> SpelledOut = new(StringComparer.Ordinal) { "UPS", "KLM", "DHL", "TNT", "ABX", "ATC", "ATIS", "QNH", "IFR", "VFR", "ILS", "RNAV", "SID", "STAR" };

    /// <summary>Alfabeto OACI escrito para que una voz en español lo pronuncie bien.</summary>
    private static readonly Dictionary<string, string> PhoneticEs = new(StringComparer.Ordinal)
    {
        ["Charlie"] = "Charli", ["Echo"] = "Eco", ["Juliett"] = "Yuliet", ["Mike"] = "Maik", ["November"] = "Noviember",
        ["Uniform"] = "Iuniform", ["Whiskey"] = "Güiski", ["Yankee"] = "Yanqui",
    };

    /// <param name="isTelephonyWord">Si una palabra es parte de una telefonía ("LAN", "CHILE"); las demás en mayúsculas (un
    /// código OACI como "SCIE") siguen deletreándose.</param>
    public static string Prepare(string spoken, AtcLanguage language, Func<string, bool>? isTelephonyWord = null)
    {
        var s = UpperWordRe().Replace(spoken, m => isTelephonyWord?.Invoke(m.Value) == true && !SpelledOut.Contains(m.Value)
            ? m.Value[0] + m.Value[1..].ToLowerInvariant()
            : m.Value);
        if (language == AtcLanguage.Spanish)
        {
            s = WordRe().Replace(s, m => PhoneticEs.TryGetValue(m.Value, out var es) ? es : m.Value);
            s = s.Replace("X-ray", "Ecs-rei", StringComparison.Ordinal);
        }
        return s;
    }

    [GeneratedRegex(@"\b[A-Z]{2,}\b")]
    private static partial Regex UpperWordRe();

    [GeneratedRegex(@"\b[A-Z][a-z]+(?:-[a-z]+)?\b")]
    private static partial Regex WordRe();
}
