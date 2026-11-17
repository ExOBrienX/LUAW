using System.Text;

namespace SimATC.Core.Comms;

/// <summary>
/// Paso 1 de la canalización de entrada (§8.8): deja el texto (escrito o transcrito de voz) en una forma única para
/// extraer valores. Función pura.
/// <list type="bullet">
/// <item>minúsculas, sin puntuación (el punto decimal entre dígitos se conserva);</item>
/// <item>números dichos a dígitos, incluida la pronunciación OACI (§8.2: tree, fower, fife, niner):
///   "one two tree" → "123", "decimal" → ".", "five thousand" → "5000", "one one thousand five hundred" → "11500";</item>
/// <item>alfabeto fonético a letras ("lima" → "l"), y "left/right/center" pegados a un número de pista ("17 left" → "17l");</item>
/// <item>muletillas fuera ("eh", "uh", "please", "por favor"…);</item>
/// <item>en español también (§8.6): sin tildes, "uno dos tres" → "123", "decimal"/"coma" → ".", "cinco mil" → "5000",
///   "uno uno mil quinientos" → "11500", "izquierda/derecha/central" pegados a la pista;</item>
/// <item>errores de tipeo: una palabra a una letra de distancia de una del vocabulario aeronáutico se corrige.</item>
/// </list>
/// </summary>
public static class InputNormalizer
{
    private static readonly Dictionary<string, string> Digits = new()
    {
        ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["tree"] = "3", ["four"] = "4", ["fower"] = "4",
        ["five"] = "5", ["fife"] = "5", ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9", ["niner"] = "9",
        // Español (sin tildes: se quitan antes).
        ["cero"] = "0", ["uno"] = "1", ["dos"] = "2", ["tres"] = "3", ["cuatro"] = "4", ["cinco"] = "5", ["seis"] = "6",
        ["siete"] = "7", ["ocho"] = "8", ["nueve"] = "9",
    };

    /// <summary>Centenas dichas en español ("quinientos" = 500).</summary>
    private static readonly Dictionary<string, int> HundredsEs = new()
    {
        ["cien"] = 100, ["ciento"] = 100, ["doscientos"] = 200, ["trescientos"] = 300, ["cuatrocientos"] = 400, ["quinientos"] = 500,
        ["seiscientos"] = 600, ["setecientos"] = 700, ["ochocientos"] = 800, ["novecientos"] = 900,
    };

    private static readonly Dictionary<string, string> Phonetic = new()
    {
        ["alfa"] = "a", ["alpha"] = "a", ["bravo"] = "b", ["charlie"] = "c", ["delta"] = "d", ["echo"] = "e",
        ["foxtrot"] = "f", ["golf"] = "g", ["hotel"] = "h", ["india"] = "i", ["juliett"] = "j", ["juliet"] = "j",
        ["kilo"] = "k", ["lima"] = "l", ["mike"] = "m", ["november"] = "n", ["oscar"] = "o", ["papa"] = "p",
        ["quebec"] = "q", ["romeo"] = "r", ["sierra"] = "s", ["tango"] = "t", ["uniform"] = "u", ["victor"] = "v",
        ["whiskey"] = "w", ["xray"] = "x", ["x-ray"] = "x", ["yankee"] = "y", ["zulu"] = "z",
    };

    private static readonly HashSet<string> Fillers =
        ["eh", "uh", "um", "uhm", "erm", "ah", "please", "ok", "okay", "so", "like", "yeah", "the", "and", "em", "este", "por", "favor", "porfavor", "bueno"];

    /// <summary>Palabras aeronáuticas contra las que se corrigen errores de tipeo (una letra de distancia, desde 5 letras).</summary>
    private static readonly string[] Vocabulary =
    [
        "cleared", "clearance", "request", "requesting", "runway", "squawk", "departure", "approach", "ground", "tower",
        "delivery", "holding", "position", "taxi", "pushback", "startup", "start", "climb", "descend", "maintain", "heading",
        "contact", "frequency", "information", "ready", "unable", "again", "readback", "correct", "affirm", "negative",
        "wilco", "roger", "line", "wait", "takeoff", "take-off", "short", "level", "flight", "altitude", "destination",
        "via", "approved", "monitor", "identified", "passing", "climbing", "stand", "gate", "transition", "decimal",
        "thousand", "hundred", "knots", "left", "right", "center", "centre",
        // Información de tráfico (CAP 413 §5.27): "sight" no debe corregirse a "right".
        "traffic", "sight", "sighted",
        // Llegada vigilada (ADR-105): "around" se corregía a "ground" y "going around" nunca se entendía.
        "around", "going", "missed", "published", "expedite", "expediting", "reduce", "reducing", "speed", "sixty", "three",
        // Español (sin tildes).
        "autorizacion", "autorizado", "solicito", "rodaje", "retroceso", "salida", "transponder", "espera", "listo",
        "posicion", "contacte", "frecuencia", "informacion", "nivel", "vuelo", "ascienda", "ascendiendo", "manteniendo",
        "mantengo", "recibido", "comprendido", "negativo", "afirmo", "repita", "despegar", "despegue", "aproximacion",
        "terrestre", "detras", "izquierda", "derecha", "central", "decimal", "imposible",
        "frustrada", "publicada", "reduciendo", "velocidad", "nudos", "acelerando", "descenso", "viraje", "sesenta", "trescientos",
        "rumbo",
    ];

    /// <summary>Las palabras ya normalizadas.</summary>
    public static IReadOnlyList<string> Tokens(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static string Normalize(string text)
    {
        // 1. Minúsculas y separar la puntuación (el punto entre dígitos queda: 118.1).
        var sb = new StringBuilder(text.Length + 8);
        var lower = WithoutAccents(text.ToLowerInvariant()).Replace("x-ray", "xray", StringComparison.Ordinal);
        for (var i = 0; i < lower.Length; i++)
        {
            var c = lower[i];
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
            else if (c == '.' && i > 0 && i + 1 < lower.Length && char.IsDigit(lower[i - 1]) && char.IsDigit(lower[i + 1]))
                sb.Append('.');
            else if (c == '/' && i > 0 && char.IsLetterOrDigit(lower[i - 1]))
                sb.Append(' ');
            else
                sb.Append(' ');
        }
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // 2. Muletillas fuera y tipeo corregido.
        var cleaned = new List<string>(words.Count);
        foreach (var w in words)
        {
            if (Fillers.Contains(w))
                continue;
            cleaned.Add(Correct(w));
        }

        // 3. Números dichos → dígitos, "decimal" → ".", miles y cientos; fonético → letras.
        var output = new List<string>(cleaned.Count);
        var i2 = 0;
        while (i2 < cleaned.Count)
        {
            var w = cleaned[i2];
            if (Digits.ContainsKey(w) || IsDigitToken(w) && i2 + 1 < cleaned.Count && Digits.ContainsKey(cleaned[i2 + 1]))
            {
                // Secuencia de dígitos dichos (o mezclados con dígitos escritos), con "decimal", "thousand", "hundred".
                var number = new StringBuilder();
                long thousands = 0;
                var hasThousand = false;
                while (i2 < cleaned.Count)
                {
                    var t = cleaned[i2];
                    if (Digits.TryGetValue(t, out var d))
                        number.Append(d);
                    else if (IsDigitToken(t) && number.Length > 0)
                        number.Append(t);
                    else if (t is "decimal" or "point" or "coma" or "punto" && number.Length > 0 && i2 + 1 < cleaned.Count && Digits.ContainsKey(cleaned[i2 + 1]))
                        number.Append('.');
                    else if (HundredsEs.TryGetValue(t, out var h) && (hasThousand || number.Length > 0))
                    {
                        // "uno uno mil quinientos": las centenas en español van después de "mil".
                        thousands += (number.Length > 0 ? long.Parse(number.ToString()) * 1000 : 0) + h;
                        hasThousand = true;
                        number.Clear();
                    }
                    else if (t is "thousand" or "mil" && number.Length > 0)
                    {
                        thousands = long.Parse(number.ToString()) * 1000;
                        hasThousand = true;
                        number.Clear();
                    }
                    else if (t == "hundred" && number.Length > 0)
                    {
                        thousands += long.Parse(number.ToString()) * 100;
                        hasThousand = true;
                        number.Clear();
                    }
                    else
                        break;
                    i2++;
                }
                if (hasThousand)
                    output.Add((thousands + (number.Length > 0 ? long.Parse(number.ToString()) : 0)).ToString());
                else
                    output.Add(number.ToString());
                continue;
            }
            if (IsDigitToken(w) && !w.Contains('.') && i2 + 1 < cleaned.Count && cleaned[i2 + 1] is "thousand" or "hundred" or "mil")
            {
                var value = long.Parse(w) * (cleaned[i2 + 1] == "hundred" ? 100 : 1000);
                i2 += 2;
                if (i2 < cleaned.Count && HundredsEs.TryGetValue(cleaned[i2], out var hs))
                {
                    value += hs;
                    i2++;
                }
                if (i2 + 1 < cleaned.Count && IsDigitToken(cleaned[i2]) && cleaned[i2 + 1] == "hundred")
                {
                    value += long.Parse(cleaned[i2]) * 100;
                    i2 += 2;
                }
                output.Add(value.ToString());
                continue;
            }
            if (Phonetic.TryGetValue(w, out var letter))
                output.Add(letter);
            else
                output.Add(w);
            i2++;
        }

        // 4. Pista: "17 left" / "17 l" → "17l".
        for (var k = 0; k + 1 < output.Count; k++)
        {
            if (IsRunwayNumber(output[k]) && Designator(output[k + 1]) is { } des)
            {
                output[k] += des;
                output.RemoveAt(k + 1);
            }
        }
        return string.Join(' ', output);
    }

    private static bool IsDigitToken(string w) => w.Length > 0 && w.All(c => char.IsDigit(c) || c == '.');

    private static bool IsRunwayNumber(string w) => w.Length is 1 or 2 && w.All(char.IsDigit) && int.Parse(w) is >= 1 and <= 36;

    private static string? Designator(string w) => w switch
    {
        "left" or "l" or "izquierda" => "l",
        "right" or "r" or "derecha" => "r",
        "center" or "centre" or "c" or "central" or "centro" => "c",
        _ => null,
    };

    /// <summary>"autorización" → "autorizacion", "ñ" → "n": el español se compara sin tildes.</summary>
    private static string WithoutAccents(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Corrige una palabra si está a una letra de distancia de una del vocabulario (y no es un número ni muy corta).</summary>
    private static string Correct(string w)
    {
        if (w.Length < 5 || w.Any(char.IsDigit) || Vocabulary.Contains(w) || Digits.ContainsKey(w) || Phonetic.ContainsKey(w))
            return w;
        foreach (var v in Vocabulary)
        {
            if (Math.Abs(v.Length - w.Length) <= 1 && EditDistanceAtMostOne(v, w))
                return v;
        }
        return w;
    }

    internal static bool EditDistanceAtMostOne(string a, string b)
    {
        if (a == b)
            return true;
        if (a.Length == b.Length)
        {
            var diff = 0;
            for (var i = 0; i < a.Length && diff <= 1; i++)
            {
                if (a[i] != b[i])
                    diff++;
            }
            return diff <= 1;
        }
        var (s, l) = a.Length < b.Length ? (a, b) : (b, a);
        if (l.Length - s.Length != 1)
            return false;
        var si = 0;
        var li = 0;
        var skipped = false;
        while (si < s.Length && li < l.Length)
        {
            if (s[si] == l[li])
            {
                si++;
                li++;
            }
            else
            {
                if (skipped)
                    return false;
                skipped = true;
                li++;
            }
        }
        return true;
    }
}
