using SimATC.Core.Comms;

namespace SimATC.Core.Tests.Comms;

/// <summary>Fraseología desde data/phraseology (§8, RF-35b).</summary>
public class PhraseologyTests
{
    private static string FilePath => Phraseology.PathFor(Path.Combine(AppContext.BaseDirectory, "Data"));

    // TEST DATA
    private static readonly Dictionary<string, string> Clearance = new()
    {
        ["callsign"] = "LAN205", ["station"] = "Santiago Delivery", ["destination"] = "SCIE", ["sid"] = "GELU5B",
        ["runway"] = "17R", ["level"] = "flight level 100", ["squawk"] = "4721",
        ["after_departure"] = ", after departure contact Santiago Approach 119.7",
    };

    /// <summary>
    /// ADR-099: la capa FAA solo reemplaza frases que ya existen en OACI, con su referencia a JO 7110.65 y los mismos datos
    /// (el altímetro en pulgadas reemplaza al QNH); el resto queda igual.
    /// </summary>
    // 2026-11-15, crash en el vuelo de Marco: la radio de los IA armaba "atc.descend_qnh" solo con {qnh} y, con la capa FAA
    // (aeropuerto de EE. UU.), la plantilla pide {altimeter}. Con el QNH, el altímetro en pulgadas sale de él.
    [Fact]
    public void Faa_altimeter_is_derived_from_the_qnh_when_only_the_qnh_is_given()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Data");
        var faa = Phraseology.Load(Phraseology.PathFor(dir, AtcLanguage.English), Phraseology.PathFor(dir, "faa-en"));
        var fields = new Dictionary<string, string> { ["callsign"] = "AAL123", ["level"] = "4000 feet", ["qnh"] = "1013", ["station"] = "" };
        Assert.Contains("29.91", faa.Say("atc.descend_qnh", fields));
        Assert.Contains("29.91", faa.Canonical("pilot.readback_descend_qnh", fields));
    }

    [Fact]
    public void Faa_layer_replaces_only_its_phrases_with_the_same_data()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Data");
        var icao = Phraseology.Load(Phraseology.PathFor(dir, AtcLanguage.English));
        var faaOnly = Phraseology.Load(Phraseology.PathFor(dir, "faa-en"));
        var faa = Phraseology.Load(Phraseology.PathFor(dir, AtcLanguage.English), Phraseology.PathFor(dir, "faa-en"));
        Assert.Equal(icao.Templates.Keys.Order(), faa.Templates.Keys.Order());
        foreach (var id in faaOnly.Templates.Keys)
        {
            Assert.True(icao.Templates.ContainsKey(id), $"{id} no existe en OACI");
            Assert.Contains("7110.65", faaOnly.Templates[id].Ref ?? "");
            var a = new HashSet<string>(icao.FieldsOf(id).Select(f => f == "qnh" ? "altimeter" : f));
            // El vector de interceptación FAA dice además la posición y la altitud hasta establecerse (5-9-4): la sesión
            // da esos dos datos aunque la frase OACI no los use (ADR-124).
            if (id.EndsWith("vector_intercept_cleared", StringComparison.Ordinal))
                a.UnionWith(["distance", "level"]);
            // La FAA puede no decir un dato ("descend via (STAR)" no lleva nivel), pero nunca uno que la sesión no da.
            Assert.True(a.IsSupersetOf(faa.FieldsOf(id)), $"{id}: {string.Join(",", a)} contra {string.Join(",", faa.FieldsOf(id))}");
        }
        Assert.Equal("LAN205, climb and maintain flight level 240",
            faa.Canonical("atc.climb", new Dictionary<string, string> { ["callsign"] = "LAN205", ["level"] = "flight level 240" }));
        Assert.Equal(icao.Canonical("atc.line_up_and_wait", Clearance), faa.Canonical("atc.line_up_and_wait", Clearance));
    }

    [Theory]
    [InlineData("KJFK", true)]
    [InlineData("PANC", true)]
    [InlineData("PHNL", true)]
    [InlineData("TJSJ", true)]
    [InlineData("SCEL", false)]
    [InlineData("EGLL", false)]
    [InlineData("CYUL", false)]
    public void Faa_is_automatic_in_the_united_states_and_only_in_english(string icao, bool us)
    {
        Assert.Equal(us, AtcWords.IsUnitedStates(icao));
        Assert.Equal(us, AtcWords.UseFaa(AtcLanguage.English, PhraseologyStyle.Auto, icao));
        Assert.False(AtcWords.UseFaa(AtcLanguage.Spanish, PhraseologyStyle.Faa, icao));
        Assert.True(AtcWords.UseFaa(AtcLanguage.English, PhraseologyStyle.Faa, icao));
        Assert.False(AtcWords.UseFaa(AtcLanguage.English, PhraseologyStyle.Icao, icao));
    }

    [Fact]
    public void Altimeter_in_inches_from_qnh() => Assert.Equal("29.92", AtcWords.AltimeterInHg(1013.25));

    /// <summary>El verificador acepta las colaciones FAA ("climb and maintain…", "runway 27, taxi via…").</summary>
    [Fact]
    public void Faa_readbacks_are_understood()
    {
        var climb = MessageParser.Parse("Climb and maintain flight level 240, LAN205", ["lan205", "lan 205"]);
        var rc = ReadbackChecker.Check(climb, new ExpectedReadback("LAN205") { LevelFt = 24000 }, ReadbackStrictness.Realistic);
        Assert.True(rc.Verdict == ReadbackVerdict.Correct, $"ascenso: {rc.Verdict} {rc.Element} ({climb.Normalized}, {climb.AltitudeFt})");
        var taxi = MessageParser.Parse("Runway 27, taxi via Alpha, LAN205", ["lan205", "lan 205"]);
        var rt = ReadbackChecker.Check(taxi, new ExpectedReadback("LAN205") { Runway = "27" }, ReadbackStrictness.Realistic);
        Assert.True(rt.Verdict == ReadbackVerdict.Correct, $"rodaje: {rt.Verdict} {rt.Element} ({taxi.Normalized}, {taxi.Runway})");
    }

    /// <summary>El español (§8.6) tiene las mismas plantillas que el inglés, con los mismos datos y con su referencia.</summary>
    [Fact]
    public void Spanish_has_the_same_templates_and_data_as_english()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Data");
        var en = Phraseology.Load(Phraseology.PathFor(dir, AtcLanguage.English));
        var es = Phraseology.Load(Phraseology.PathFor(dir, AtcLanguage.Spanish));
        Assert.Equal(en.Templates.Keys.Order(), es.Templates.Keys.Order());
        foreach (var id in en.Templates.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(es.Templates[id].Ref), id);
            var a = new HashSet<string>(en.FieldsOf(id));
            var b = new HashSet<string>(es.FieldsOf(id));
            a.Remove("station");
            b.Remove("station");
            Assert.True(a.SetEquals(b), $"{id}: {string.Join(",", a)} ≠ {string.Join(",", b)}");
        }
    }

    [Fact]
    public void Every_template_has_a_reference_and_variants()
    {
        var p = Phraseology.Load(FilePath);
        Assert.True(p.Templates.Count >= 25);
        Assert.All(p.Templates, kv =>
        {
            Assert.False(string.IsNullOrWhiteSpace(kv.Value.Ref), kv.Key);
            Assert.NotEmpty(kv.Value.Variants);
        });
    }

    [Fact]
    public void Variants_of_a_template_use_the_same_data()
    {
        var p = Phraseology.Load(FilePath);
        foreach (var (id, t) in p.Templates)
        {
            // {station} es opcional entre variantes (una lo nombra y otra no); el resto de los datos debe estar en todas.
            var sets = t.Variants.Select(v => System.Text.RegularExpressions.Regex.Matches(v, @"\{([a-z_]+)\}")
                .Select(m => m.Groups[1].Value).Where(f => f != "station").ToHashSet()).ToList();
            Assert.All(sets, s => Assert.True(s.SetEquals(sets[0]), $"{id}: las variantes no llevan los mismos datos"));
        }
    }

    [Fact]
    public void Clearance_says_the_exact_data_in_any_variant()
    {
        for (var seed = 0; seed < 10; seed++)
        {
            var text = Phraseology.Load(FilePath, new Random(seed)).Say("atc.clearance", Clearance);
            foreach (var value in new[] { "LAN205", "SCIE", "GELU5B", "17R", "flight level 100", "4721", "Santiago Approach 119.7" })
                Assert.Contains(value, text);
            Assert.DoesNotContain("{", text);
        }
    }

    [Fact]
    public void Varies_between_equivalent_forms()
    {
        var p = Phraseology.Load(FilePath, new Random(1));
        var fields = new Dictionary<string, string> { ["callsign"] = "LAN205" };
        var seen = Enumerable.Range(0, 30).Select(_ => p.Say("atc.readback_correct", fields)).ToHashSet();
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void A_missing_field_is_an_error_not_a_broken_phrase()
    {
        var p = Phraseology.Load(FilePath);
        Assert.Throws<KeyNotFoundException>(() => p.Say("atc.taxi", new Dictionary<string, string> { ["callsign"] = "LAN205" }));
        Assert.Throws<KeyNotFoundException>(() => p.Say("atc.no_existe", Clearance));
    }
}
