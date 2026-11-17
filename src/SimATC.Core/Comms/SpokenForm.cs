using System.Text;
using System.Text.RegularExpressions;
using SimATC.Core.Atc;

namespace SimATC.Core.Comms;

/// <summary>
/// Forma hablada de una frase escrita (§8.2, RF-30b): dígitos uno por uno con la pronunciación OACI (tree, fower, fife,
/// niner), "decimal" en frecuencias, letras de pista (L → left), miles y cientos en altitudes ("5000 feet" →
/// "fife thousand feet"). Los indicativos con la telefonía de la aerolínea (<see cref="AirlineTelephony"/>, hito 4e-3):
/// LAN2045 → "LAN CHILE two zero fower fife".
/// </summary>
public static partial class SpokenForm
{
    private static readonly string[] Words = ["zero", "one", "two", "tree", "fower", "fife", "six", "seven", "eight", "niner"];

    /// <summary>Dígitos en español (pronunciación OACI en español: cero, uno… nueve).</summary>
    private static readonly string[] WordsEs = ["cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve"];

    private static readonly string[] HundredsEs =
        ["", "cien", "doscientos", "trescientos", "cuatrocientos", "quinientos", "seiscientos", "setecientos", "ochocientos", "novecientos"];

    public static string Of(string written, AirlineTelephony? airlines = null, AtcLanguage lang = AtcLanguage.English)
    {
        var es = lang == AtcLanguage.Spanish;
        // Frecuencia: 118.35 → one one eight decimal tree fife / uno uno ocho decimal tres cinco.
        var s = FrequencyRe().Replace(written, m => $"{DigitByDigit(m.Groups[1].Value, es)} decimal {DigitByDigit(m.Groups[2].Value, es)}");
        // Pista: 17R → runway one seven right / pista uno siete derecha.
        s = es
            ? RunwayEsRe().Replace(s, m => $"{m.Groups[1].Value} {DigitByDigit(m.Groups[2].Value, true)}{Designator(m.Groups[3].Value, true)}")
            : RunwayRe().Replace(s, m => $"runway {DigitByDigit(m.Groups[1].Value, false)}{Designator(m.Groups[2].Value, false)}");
        // Calles de rodaje después de "via"/"vía", con el alfabeto OACI: "via Z3, P, H" → "via Zulu tree, Papa, Hotel" (en el
        // chat se escriben como en las cartas; por radio se deletrean).
        s = ViaRe().Replace(s, m => $"{m.Groups[1].Value} {TaxiwayRe().Replace(m.Groups[2].Value, t => Taxiway(t.Value, es))}");
        // Altitud en miles/cientos: 5000 feet → fife thousand feet; 11500 pies → uno uno mil quinientos pies.
        s = es
            ? FeetEsRe().Replace(s, m => $"{Thousands(int.Parse(m.Groups[1].Value), true)} pies")
            : FeetRe().Replace(s, m => $"{Thousands(int.Parse(m.Groups[1].Value), false)} feet");
        // Indicativo OACI: letras y número (LAN205 → LAN CHILE two zero fife / LAN CHILE dos cero cinco).
        s = CallsignRe().Replace(s, m => airlines is not null && m.Groups[1].Length == 3
            ? airlines.Spoken(m.Value, lang)
            : $"{m.Groups[1].Value} {DigitByDigit(m.Groups[2].Value, es)}");
        // El resto de los números, dígito por dígito (niveles, squawk, rumbos).
        s = NumberRe().Replace(s, m => DigitByDigit(m.Value, es));
        return s;
    }

    /// <summary>"Z3" → "Zulu tree" / "Zulu tres"; "AB" → "Alfa Bravo".</summary>
    private static string Taxiway(string id, bool es)
    {
        var n = id.TakeWhile(char.IsLetter).Count();
        var spoken = string.Join(' ', id[..n].Select(AtisBuilder.Phonetic));
        return n < id.Length ? $"{spoken} {DigitByDigit(id[n..], es)}" : spoken;
    }

    /// <summary>Un dígito dicho: "niner" / "nueve".</summary>
    public static string Digit(char c, AtcLanguage lang) => (lang == AtcLanguage.Spanish ? WordsEs : Words)[c - '0'];

    private static string DigitByDigit(string digits, bool es)
    {
        var words = es ? WordsEs : Words;
        var sb = new StringBuilder();
        foreach (var c in digits)
        {
            if (!char.IsDigit(c))
                continue;
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(words[c - '0']);
        }
        return sb.ToString();
    }

    private static string Designator(string d, bool es) => d.ToUpperInvariant() switch
    {
        "L" => es ? " izquierda" : " left",
        "R" => es ? " derecha" : " right",
        "C" => es ? " central" : " center",
        _ => "",
    };

    private static string Thousands(int ft, bool es)
    {
        var parts = new List<string>();
        if (ft >= 1000)
            parts.Add($"{DigitByDigit((ft / 1000).ToString(), es)} {(es ? "mil" : "thousand")}");
        if (ft % 1000 >= 100)
            parts.Add(es ? HundredsEs[ft % 1000 / 100] : $"{Words[ft % 1000 / 100]} hundred");
        return parts.Count > 0 ? string.Join(' ', parts) : DigitByDigit(ft.ToString(), es);
    }

    [GeneratedRegex(@"\b(1[1-3]\d)\.(\d{1,3})\b")]
    private static partial Regex FrequencyRe();

    [GeneratedRegex(@"\brunway (\d{1,2})([LRClrc]?)\b")]
    private static partial Regex RunwayRe();

    [GeneratedRegex(@"\b([Pp]ista) (\d{1,2})([LRClrc]?)\b")]
    private static partial Regex RunwayEsRe();

    [GeneratedRegex(@"\b(\d{3,5}) feet\b")]
    private static partial Regex FeetRe();

    [GeneratedRegex(@"\b(\d{3,5}) pies\b")]
    private static partial Regex FeetEsRe();

    [GeneratedRegex(@"\b([A-Z]{2,3})(\d{1,4}[A-Z]?)\b")]
    private static partial Regex CallsignRe();

    [GeneratedRegex(@"\b([Vv][ií]a) ((?:[A-Z]{1,2}\d{0,2}\b(?:, )?)+)")]
    private static partial Regex ViaRe();

    [GeneratedRegex(@"\b[A-Z]{1,2}\d{0,2}\b")]
    private static partial Regex TaxiwayRe();

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex NumberRe();
}
