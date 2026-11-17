using SimATC.Core.Comms;

namespace SimATC.Core.Tests.Comms;

/// <summary>El manual del ATC (ADR-135) sale de las plantillas reales, en los dos idiomas.</summary>
public class AtcManualTests
{
    [Theory]
    [InlineData(AtcLanguage.English)]
    [InlineData(AtcLanguage.Spanish)]
    public void The_manual_is_built_from_the_templates_in_every_section(AtcLanguage language)
    {
        var phrases = Phraseology.Load(Phraseology.PathFor(Path.Combine(AppContext.BaseDirectory, "Data"), language), new Random(1));
        var manual = AtcManual.Build(phrases, null, language);
        Assert.True(manual.Count >= 20, $"solo {manual.Count} entradas");
        foreach (var section in AtcManual.Sections)
            Assert.Contains(manual, e => e.Section == section);
        // Ninguna frase con un campo sin llenar.
        Assert.All(manual, e => Assert.DoesNotContain("{", (e.Pilot ?? "") + (e.Atc ?? "")));
        Assert.Contains(manual, e => e.Pilot?.Contains("ABC123") == true && e.PilotSpoken?.Contains("one two") == (language == AtcLanguage.English));
        Assert.Contains(manual, e => e.Matches("pushback"));
        // Sin plan, nada de un aeropuerto real (antes todo era de Santiago y LAN205).
        Assert.All(manual, e => Assert.DoesNotContain("Santiago", (e.Pilot ?? "") + (e.Atc ?? "") + e.Note));
    }

    [Fact]
    public void With_a_plan_the_examples_use_the_pilot_flight()
    {
        var phrases = Phraseology.Load(Phraseology.PathFor(Path.Combine(AppContext.BaseDirectory, "Data"), AtcLanguage.English), new Random(1));
        var flight = new ManualExample("BAW123", "Heathrow", "Brussels", "EBBR", "DET1J", "KOK9A", "09R", "25L", "flight level 230");
        var manual = AtcManual.Build(phrases, null, AtcLanguage.English, flight);
        Assert.Contains(manual, e => e.Pilot?.Contains("Heathrow Delivery, BAW123") == true && e.Pilot.Contains("EBBR"));
        Assert.Contains(manual, e => e.Atc?.Contains("DET1J") == true && e.Atc.Contains("09R"));
        Assert.Contains(manual, e => e.Title == "Avisar en final" && e.Pilot!.Contains("Brussels Tower") && e.Pilot.Contains("25L"));
    }
}
