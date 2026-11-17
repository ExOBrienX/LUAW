using SimATC.Core.Comms;
using SimATC.Voice;

namespace SimATC.Core.Tests.Voice;

/// <summary>Voz (etapa 5b): filtro de radio, catálogo de voces de Piper, asignación estable y caché.</summary>
public class VoiceTests
{
    private static AudioClip Tone(double hz, int rate = 22050, double seconds = 0.5)
    {
        var s = new float[(int)(rate * seconds)];
        for (var i = 0; i < s.Length; i++)
            s[i] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate));
        return new AudioClip(s, rate);
    }

    /// <summary>Energía en la parte central del audio (sin el clic ni el squelch, ya asentado el filtro).</summary>
    private static double Rms(AudioClip c)
    {
        var from = c.Samples.Length / 3;
        var to = 2 * c.Samples.Length / 3;
        var sum = 0.0;
        for (var i = from; i < to; i++)
            sum += c.Samples[i] * c.Samples[i];
        return Math.Sqrt(sum / (to - from));
    }

    private static readonly RadioFilterOptions Clean = new(NoiseLevel: 0, Drive: 1.0001);

    [Fact]
    public void Radio_filter_keeps_the_voice_band_and_cuts_lows_and_highs()
    {
        var voice = Rms(RadioFilter.Apply(Tone(1000), Clean));
        var low = Rms(RadioFilter.Apply(Tone(80), Clean));
        var high = Rms(RadioFilter.Apply(Tone(8000), Clean));
        Assert.True(voice > 0.25, $"1 kHz: {voice:F3}");
        Assert.True(low < voice / 8, $"80 Hz: {low:F3}");
        Assert.True(high < voice / 8, $"8 kHz: {high:F3}");
    }

    [Fact]
    public void Radio_filter_adds_the_mic_clicks_and_the_squelch_tail_and_is_deterministic()
    {
        var input = Tone(1000, seconds: 1);
        var a = RadioFilter.Apply(input, seed: 7);
        var b = RadioFilter.Apply(input, seed: 7);
        Assert.Equal(a.Samples, b.Samples);
        var o = RadioFilterOptions.Default;
        Assert.Equal(input.DurationS + 2 * o.ClickS + o.SquelchS, a.DurationS, 2);
        Assert.All(a.Samples, x => Assert.InRange(x, -1.01f, 1.01f));
    }

    [Fact]
    public void Pcm16_becomes_samples_between_minus_one_and_one()
    {
        var clip = AudioClip.FromPcm16([0x00, 0x80, 0xFF, 0x7F, 0x00, 0x00], 16000);
        Assert.Equal([-1f, 32767 / 32768f, 0f], clip.Samples);
        Assert.Equal(3 / 16000.0, clip.DurationS, 6);
    }

    private static string VoicesFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"simatc-voices-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        void Voice(string id, string lang, int rate, int speakers)
        {
            File.WriteAllBytes(Path.Combine(dir, id + ".onnx"), [0]);
            File.WriteAllText(Path.Combine(dir, id + ".onnx.json"),
                $$"""{"audio":{"sample_rate":{{rate}}},"language":{"code":"{{lang}}"},"num_speakers":{{speakers}}}""");
        }
        // TEST DATA: la forma de los .onnx.json de Piper.
        Voice("en_US-joe-medium", "en_US", 22050, 1);
        Voice("en_US-libritts_r-medium", "en_US", 22050, 2306);
        Voice("es_ES-davefx-medium", "es_ES", 22050, 1);
        Voice("es_MX-ald-medium", "es_MX", 22050, 1);
        File.WriteAllBytes(Path.Combine(dir, "sin-config.onnx"), [0]); // sin .json: se salta
        File.WriteAllText(Path.Combine(dir, "rota.onnx.json"), "{ no");  // sin .onnx: no se mira
        return dir;
    }

    [Fact]
    public void Catalog_reads_language_rate_and_speakers_of_the_installed_voices()
    {
        var dir = VoicesFolder();
        try
        {
            var catalog = VoiceCatalog.Scan(dir);
            Assert.Equal(4, catalog.Voices.Count);
            Assert.Equal(2, catalog.For(AtcLanguage.Spanish).Count);
            Assert.Equal(2306, catalog.Find("en_US-libritts_r-medium")!.Speakers);
            Assert.Equal(22050, catalog.Find("es_MX-ald-medium")!.SampleRate);
            Assert.Empty(VoiceCatalog.Scan(Path.Combine(dir, "no-existe")).Voices);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Each_station_and_aircraft_keeps_its_voice_in_its_language()
    {
        var dir = VoicesFolder();
        try
        {
            var assigner = new VoiceAssigner(VoiceCatalog.Scan(dir));
            var tower = assigner.For(VoiceRole.Atc, "Santiago Torre", AtcLanguage.Spanish)!.Value;
            Assert.Equal(AtcLanguage.Spanish, tower.Voice.Language);
            Assert.Equal(tower, assigner.For(VoiceRole.Atc, "Santiago Torre", AtcLanguage.Spanish));
            // Español: 2 voces. La Torre y el copiloto tienen una cada uno; los pilotos repiten con otro tono, nunca igual.
            var copilot = assigner.For(VoiceRole.Copilot, "copilot", AtcLanguage.Spanish)!.Value;
            Assert.NotEqual(tower.Key, copilot.Key);
            for (var n = 100; n < 120; n++)
            {
                var p = assigner.For(VoiceRole.Pilot, $"SKU{n}", AtcLanguage.Spanish)!.Value;
                Assert.False(p.Key == tower.Key && p.Pitch == tower.Pitch, $"SKU{n} suena igual que la Torre");
                Assert.False(p.Key == copilot.Key && p.Pitch == copilot.Pitch, $"SKU{n} suena igual que el copiloto");
            }
            // Muchos aviones: varias voces distintas (no todos iguales).
            var pilots = Enumerable.Range(100, 40).Select(n => assigner.For(VoiceRole.Pilot, $"LAN{n}", AtcLanguage.English)!.Value.Key).ToHashSet();
            Assert.True(pilots.Count >= 10, $"{pilots.Count} voces distintas");
            // La voz elegida en Configuración manda.
            assigner.Preferred[VoiceRole.Atc] = "en_US-joe-medium";
            assigner.Reset();
            Assert.Equal("en_US-joe-medium", assigner.For(VoiceRole.Atc, "Santiago Tower", AtcLanguage.English)!.Value.Voice.Id);
            // Sin voces del idioma: nada (la app sigue en texto).
            Assert.Null(new VoiceAssigner(new VoiceCatalog([])).For(VoiceRole.Atc, "x", AtcLanguage.English));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>V-31: las voces que se instalan permiten uso comercial, y cada modelo trae su configuración y su MD5.</summary>
    [Fact]
    public void Installer_list_has_only_commercial_friendly_voices_with_their_config()
    {
        var voices = VoiceInstaller.Voices;
        Assert.All(voices, f => Assert.DoesNotContain("NC", f.License));
        Assert.All(voices, f => Assert.True(f.Md5 is not null || f.Sha256 is not null, f.Name));
        var models = voices.Where(f => f.RelativePath.EndsWith(".onnx", StringComparison.Ordinal)).ToList();
        Assert.Equal(7, models.Count);
        Assert.All(models, m => Assert.Contains(voices, f => f.RelativePath == m.RelativePath + ".json"));
        Assert.Equal(3, models.Count(m => m.Name.StartsWith("en", StringComparison.Ordinal)));
        Assert.Equal(4, models.Count(m => m.Name.StartsWith("es", StringComparison.Ordinal)));
        Assert.InRange(VoiceInstaller.TotalBytes / 1e6, 450, 560);
    }

    /// <summary>
    /// Comentario de un usuario en flightsim.to (2026-11-17): la voz se quedaba en 4 % y decía "no se pudo instalar".
    /// Hugging Face reentrenó en noviembre en <c>main</c> voces que usamos (joe, davefx, sharvard): cambiaron de tamaño y la
    /// verificación las rechazaba. Todo lo que se baja de Hugging Face va fijado a un commit (no cambia nunca).
    /// </summary>
    [Fact]
    public void Every_hugging_face_download_is_pinned_to_a_commit()
    {
        IEnumerable<VoiceFile> all = [.. VoiceInstaller.AllFiles, .. KokoroInstaller.Files, SttInstaller.Model];
        Assert.All(all.Where(f => f.Url.Contains("huggingface.co", StringComparison.Ordinal)),
            f => Assert.Matches(@"/resolve/[0-9a-f]{40}/", f.Url));
    }

    [Fact]
    public void Tts_text_does_not_spell_telephonies_and_fixes_spanish_phonetics()
    {
        bool Tel(string w) => w is "LAN" or "CHILE" or "SPEEDBIRD" or "UPS";
        Assert.Equal("Lan Chile dos cero cinco, autorizado a SCIE", TtsText.Prepare("LAN CHILE dos cero cinco, autorizado a SCIE", AtcLanguage.Spanish, Tel));
        Assert.Equal("UPS one two", TtsText.Prepare("UPS one two", AtcLanguage.English, Tel)); // se deletrea de verdad
        Assert.Equal("vía Yuliet, Eco, Güiski, Golf", TtsText.Prepare("vía Juliett, Echo, Whiskey, Golf", AtcLanguage.Spanish, Tel));
        Assert.Equal("via Juliett, Echo", TtsText.Prepare("via Juliett, Echo", AtcLanguage.English, Tel));
    }

    [Fact]
    public void Pitch_changes_the_length_proportionally()
    {
        var clip = new AudioClip(new float[1000], 1000);
        Assert.Equal(1.0, clip.WithPitch(1.0).DurationS, 3);
        Assert.Equal(1 / 1.1, clip.WithPitch(1.1).DurationS, 2);
    }

    [Fact]
    public void Audio_cache_keeps_the_most_recent()
    {
        var cache = new AudioCache(2);
        var clip = new AudioClip([0f], 1);
        cache.Add("a", clip);
        cache.Add("b", clip);
        Assert.True(cache.TryGet("a", out _)); // "a" pasa a ser la más reciente
        cache.Add("c", clip);
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out _));
        Assert.Equal(2, cache.Count);
    }
}
