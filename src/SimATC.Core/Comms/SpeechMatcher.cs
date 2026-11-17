using System.Globalization;
using System.Text;

namespace SimATC.Core.Comms;

/// <summary>La frase esperada que más se parece a lo que reconoció el micrófono, y cuánto (0 a 1).</summary>
public readonly record struct SpeechMatch(int Index, double Score);

/// <summary>
/// Reconocimiento sesgado a las frases esperadas (RF-32, etapa 5c, ADR-126): lo que transcribe el micrófono se compara
/// con las frases que el piloto puede decir ahora (las sugerencias, en su forma escrita y en la hablada) y se elige la más
/// parecida. Así "lan chile two zero five request push back" cuenta como "LAN205, request pushback". Lógica pura: no
/// sabe de audio.
/// </summary>
public static class SpeechMatcher
{
    /// <summary>Parecido mínimo para dar por dicha una frase esperada. APROXIMADO (se ajusta con pruebas de Marco).</summary>
    public const double AcceptScore = 0.72;

    private static readonly string[] DigitWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    // Formas de radio y variantes de transcripción que valen lo mismo. Los números en español van a la misma forma interna.
    private static readonly Dictionary<string, string> Same = new(StringComparer.Ordinal)
    {
        ["fife"] = "five", ["niner"] = "nine", ["tree"] = "three", ["fower"] = "four", ["oh"] = "zero",
        ["cero"] = "zero", ["uno"] = "one", ["una"] = "one", ["dos"] = "two", ["tres"] = "three", ["cuatro"] = "four", ["cinco"] = "five",
        ["seis"] = "six", ["siete"] = "seven", ["ocho"] = "eight", ["nueve"] = "nine",
        ["takeoff"] = "take off", ["pushback"] = "push back", ["startup"] = "start up", ["readback"] = "read back",
        ["lineup"] = "line up", ["alpha"] = "alfa", ["juliet"] = "juliett", ["x-ray"] = "xray",
        ["decimal"] = "point", ["coma"] = "point", ["punto"] = "point",
    };

    /// <summary>
    /// Deja el texto comparable: minúsculas, sin tildes ni puntuación, cada cifra como palabra ("205" → "two zero five"),
    /// y las formas de radio unificadas ("fife" → "five", "pushback" → "push back").
    /// </summary>
    public static IReadOnlyList<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        void Flush()
        {
            if (word.Length == 0)
                return;
            var w = word.ToString();
            word.Clear();
            if (Same.TryGetValue(w, out var same))
                tokens.AddRange(same.Split(' '));
            else
                tokens.Add(w);
        }
        foreach (var raw in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark)
                continue; // tilde
            var c = char.ToLowerInvariant(raw);
            if (c is >= '0' and <= '9')
            {
                Flush();
                tokens.Add(DigitWords[c - '0']);
            }
            else if (c is >= 'a' and <= 'z' || c == 'ñ')
            {
                word.Append(c);
            }
            else if (c == '.' && word.Length == 0 && tokens.Count > 0 && tokens[^1] is var last && Array.IndexOf(DigitWords, last) >= 0)
            {
                tokens.Add("point"); // "121.7"
            }
            else
            {
                Flush();
            }
        }
        Flush();
        // Un "point" que quedó al final (punto de cierre después de un número) no es un decimal.
        if (tokens.Count > 0 && tokens[^1] == "point")
            tokens.RemoveAt(tokens.Count - 1);
        return Thousands(tokens);
    }

    /// <summary>
    /// "six thousand" → 6 0 0 0, "two thousand five hundred" → 2 5 0 0, "five hundred" → 5 0 0 (y "mil" en español): así
    /// una altitud dicha con palabras vale lo mismo que escrita con cifras.
    /// </summary>
    private static List<string> Thousands(List<string> tokens)
    {
        if (!tokens.Contains("thousand") && !tokens.Contains("hundred") && !tokens.Contains("mil"))
            return tokens;
        static bool Digit(string t) => Array.IndexOf(DigitWords, t) >= 0;
        var result = new List<string>(tokens.Count + 4);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var afterDigit = result.Count > 0 && Digit(result[^1]);
            if (t is "thousand" or "mil" && afterDigit)
            {
                if (i + 2 < tokens.Count && Digit(tokens[i + 1]) && tokens[i + 2] == "hundred")
                {
                    result.Add(tokens[i + 1]);
                    result.Add("zero");
                    result.Add("zero");
                    i += 2;
                }
                else
                {
                    result.Add("zero");
                    result.Add("zero");
                    result.Add("zero");
                }
            }
            else if (t == "hundred" && afterDigit)
            {
                result.Add("zero");
                result.Add("zero");
            }
            else
            {
                result.Add(t);
            }
        }
        return result;
    }

    /// <summary>Parecido entre dos textos (0 a 1): 1 menos la distancia de edición por palabras sobre el largo mayor.</summary>
    public static double Similarity(string a, string b) => Similarity(Tokens(a), Tokens(b));

    private static double Similarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        var prev = new int[b.Count + 1];
        var cur = new int[b.Count + 1];
        for (var j = 0; j <= b.Count; j++)
            prev[j] = j;
        for (var i = 1; i <= a.Count; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Count; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return 1 - prev[b.Count] / (double)Math.Max(a.Count, b.Count);
    }

    /// <summary>
    /// La frase esperada más parecida a <paramref name="transcript"/> (contra su forma escrita y su forma hablada), o null
    /// si no hay frases o no se dijo nada. El que llama decide con <see cref="AcceptScore"/>.
    /// </summary>
    public static SpeechMatch? Best(string transcript, IReadOnlyList<(string Text, string Spoken)> expected)
    {
        var said = Tokens(transcript);
        if (said.Count == 0 || expected.Count == 0)
            return null;
        var best = -1;
        var bestScore = -1.0;
        for (var i = 0; i < expected.Count; i++)
        {
            var score = Math.Max(Similarity(said, Tokens(expected[i].Text)),
                expected[i].Spoken.Length > 0 ? Similarity(said, Tokens(expected[i].Spoken)) : 0);
            if (score > bestScore)
            {
                best = i;
                bestScore = score;
            }
        }
        return new SpeechMatch(best, bestScore);
    }

    /// <summary>
    /// ¿Los dos textos dicen los mismos números, en el mismo orden? Si el piloto colacionó otro valor (otra altitud, otra
    /// frecuencia), no se le "arregla" la frase: se manda lo que dijo y el ATC lo corrige.
    /// </summary>
    public static bool SameNumbers(string a, string b)
    {
        var x = Tokens(a);
        var y = Tokens(b);
        int i = 0, j = 0;
        while (true)
        {
            while (i < x.Count && Array.IndexOf(DigitWords, x[i]) < 0)
                i++;
            while (j < y.Count && Array.IndexOf(DigitWords, y[j]) < 0)
                j++;
            if (i >= x.Count || j >= y.Count)
                return i >= x.Count && j >= y.Count;
            if (x[i] != y[j])
                return false;
            i++;
            j++;
        }
    }

    /// <summary>
    /// El texto que se le da al reconocedor como contexto: las frases esperadas, para que acierte con los nombres y la
    /// fraseología ("Santiago Ground", "squawk", "holding point").
    /// </summary>
    /// <param name="glossary">Vocabulario fijo de la fraseología (<see cref="Glossary"/>): va primero, porque el reconocedor
    /// conserva el final del contexto y lo más importante son las frases esperadas ahora.</param>
    public static string Prompt(IReadOnlyList<(string Text, string Spoken)> expected, int maxChars = 600, string? glossary = null)
    {
        var sb = new StringBuilder();
        if (glossary is { Length: > 0 })
            sb.Append(glossary).Append(". ");
        var start = sb.Length;
        foreach (var (text, _) in expected)
        {
            if (sb.Length - start + text.Length + 2 > maxChars)
                break;
            sb.Append(text).Append(". ");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Vocabulario del piloto sacado de las plantillas de fraseología (ADR-127): los trozos fijos de las frases "pilot.*"
    /// sin sus campos ("solicito rodaje", "listo para salida"). Con él el reconocedor acierta más cuando se dice algo que no
    /// está entre las sugerencias del momento. Primero los llamados que inicia el piloto; las colaciones ya van como frases
    /// esperadas.
    /// </summary>
    public static string Glossary(IReadOnlyDictionary<string, PhraseTemplate> templates, int maxChars = 320)
    {
        var ids = new List<string>();
        foreach (var id in templates.Keys)
        {
            if (id.StartsWith("pilot.", StringComparison.Ordinal))
                ids.Add(id);
        }
        ids.Sort((a, b) =>
        {
            var ra = a.Contains("readback", StringComparison.Ordinal);
            var rb = b.Contains("readback", StringComparison.Ordinal);
            return ra != rb ? ra.CompareTo(rb) : string.CompareOrdinal(a, b);
        });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (templates[id].Variants.Count == 0)
                continue;
            // Los trozos entre campos: "{station}, {callsign}, solicito rodaje" → "solicito rodaje".
            var text = templates[id].Variants[0];
            var i = 0;
            while (i < text.Length)
            {
                var open = text.IndexOf('{', i);
                var end = open < 0 ? text.Length : open;
                var phrase = text[i..end].Trim(' ', ',', '.', ';', ':');
                if (phrase.Length >= 6 && seen.Add(phrase))
                {
                    if (sb.Length + phrase.Length + 2 > maxChars)
                        return sb.ToString().TrimEnd(' ', ',');
                    sb.Append(phrase).Append(", ");
                }
                var close = open < 0 ? -1 : text.IndexOf('}', open);
                i = close < 0 ? text.Length : close + 1;
            }
        }
        return sb.ToString().TrimEnd(' ', ',');
    }
}
