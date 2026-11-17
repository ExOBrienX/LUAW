using SimATC.Core.Comms;

namespace SimATC.Core.Tests.Comms;

/// <summary>Reconocimiento sesgado a las frases esperadas (RF-32, ADR-126).</summary>
public class SpeechMatcherTests
{
    // TEST DATA: sugerencias como las del panel (escrita y hablada).
    private static readonly (string Text, string Spoken)[] Expected =
    [
        ("LAN205, stand 11, request pushback", "LAN CHILE two zero fife, stand one one, request pushback"),
        ("Santiago Ground, LAN205, request taxi", "Santiago Ground, LAN CHILE two zero fife, request taxi"),
        ("Say again, LAN205", "Say again, LAN CHILE two zero fife"),
    ];

    [Theory]
    [InlineData("Lan Chile 205, stand 11, request push back.", 0)]
    [InlineData("lan chile two zero five stand one one request pushback", 0)]
    [InlineData("Santiago ground, Lan Chile two zero five request taxi", 1)]
    [InlineData("say again lan chile 205", 2)]
    public void What_the_microphone_heard_matches_the_expected_phrase(string heard, int index)
    {
        var m = SpeechMatcher.Best(heard, Expected)!.Value;
        Assert.Equal(index, m.Index);
        Assert.True(m.Score >= SpeechMatcher.AcceptScore, $"parecido {m.Score:0.00}");
    }

    [Theory]
    [InlineData("thank you for watching")]
    [InlineData("request descent flight level one two zero")]
    public void Something_else_is_not_accepted(string heard)
    {
        var m = SpeechMatcher.Best(heard, Expected)!.Value;
        Assert.True(m.Score < SpeechMatcher.AcceptScore, $"parecido {m.Score:0.00} con la {m.Index}");
    }

    [Fact]
    public void Silence_or_no_expected_phrases_give_no_match()
    {
        Assert.Null(SpeechMatcher.Best("  ...  ", Expected));
        Assert.Null(SpeechMatcher.Best("request taxi", []));
    }

    [Fact]
    public void Numbers_radio_words_and_spanish_are_normalized()
    {
        Assert.Equal(["one", "two", "one", "point", "seven"], SpeechMatcher.Tokens("121.7"));
        Assert.Equal(SpeechMatcher.Tokens("niner tree fife"), SpeechMatcher.Tokens("9 3 5"));
        Assert.Equal(SpeechMatcher.Tokens("autorización pista dos cero"), SpeechMatcher.Tokens("Autorizacion pista 20."));
        Assert.Equal(["cleared", "for", "take", "off"], SpeechMatcher.Tokens("Cleared for take-off"));
        Assert.Equal(SpeechMatcher.Tokens("takeoff"), SpeechMatcher.Tokens("take off"));
    }

    [Fact]
    public void A_wrong_value_in_a_readback_still_matches_the_phrase_but_scores_lower()
    {
        // TEST DATA
        (string, string)[] expected = [("Descend to altitude 6000 feet, QNH 1015, LAN205", "")];
        var right = SpeechMatcher.Best("descend to altitude 6000 feet QNH 1015 Lan 205", expected)!.Value;
        var wrong = SpeechMatcher.Best("descend to altitude 5000 feet QNH 1015 Lan 205", expected)!.Value;
        Assert.True(right.Score > wrong.Score);
        Assert.True(right.Score >= 0.9);
    }

    [Fact]
    public void Different_numbers_are_noticed()
    {
        Assert.True(SpeechMatcher.SameNumbers("Descend to altitude 6000 feet, QNH 1015, LAN205", "descend altitude six thousand feet Q N H one zero one five lan two zero five"));
        Assert.True(SpeechMatcher.SameNumbers("Climb to altitude 2500 feet", "climb to altitude two thousand five hundred feet"));
        Assert.True(SpeechMatcher.SameNumbers("Descienda a 4000 pies", "descienda a cuatro mil pies"));
        Assert.True(SpeechMatcher.SameNumbers("Contact Tower 118.1", "contact tower one one eight decimal one"));
        Assert.False(SpeechMatcher.SameNumbers("Descend to altitude 6000 feet", "descend to altitude 5000 feet"));
        Assert.False(SpeechMatcher.SameNumbers("Runway 17L", "runway 17 left heading 240"));
    }

    [Fact]
    public void The_prompt_lists_the_expected_phrases()
    {
        var prompt = SpeechMatcher.Prompt(Expected);
        Assert.Contains("request pushback", prompt);
        Assert.Contains("Santiago Ground", prompt);
        Assert.True(SpeechMatcher.Prompt(Expected, 40).Length <= 40);
    }

    /// <summary>
    /// El vocabulario para el reconocedor sale de las plantillas del piloto (ADR-127): trozos fijos, sin campos, y primero los
    /// llamados que inicia el piloto. Prueba de Marco 2026-11-11: dijo una frase que no estaba sugerida y se entendió mal.
    /// </summary>
    [Theory]
    [InlineData(AtcLanguage.Spanish, "solicito rodaje", "listo para salida")]
    [InlineData(AtcLanguage.English, "request taxi", "ready for departure")]
    public void The_glossary_comes_from_the_pilot_templates(AtcLanguage language, string a, string b)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        var phrases = Phraseology.Load(Phraseology.PathFor(dataDir, language), new Random(1));
        var glossary = SpeechMatcher.Glossary(phrases.Templates);
        Assert.Contains(a, glossary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(b, glossary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", glossary);
        Assert.InRange(glossary.Length, 50, 320);
    }

    /// <summary>El vocabulario va primero y las frases esperadas al final: el reconocedor conserva el final del contexto.</summary>
    [Fact]
    public void The_prompt_puts_the_glossary_first_and_the_expected_phrases_last()
    {
        var prompt = SpeechMatcher.Prompt(Expected, glossary: "request taxi, ready for departure");
        Assert.StartsWith("request taxi, ready for departure. LAN205, stand 11", prompt);
        Assert.EndsWith("Say again, LAN205.", prompt);
        Assert.Equal(SpeechMatcher.Prompt(Expected), SpeechMatcher.Prompt(Expected, glossary: ""));
    }
}
