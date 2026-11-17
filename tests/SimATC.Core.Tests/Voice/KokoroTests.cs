using SimATC.Core;
using SimATC.Core.Comms;
using SimATC.Voice;

namespace SimATC.Core.Tests.Voice;

/// <summary>Voz Kokoro para el ATC (ADR-103).</summary>
public class KokoroTests
{
    [Fact]
    public void Each_station_keeps_its_voice_and_spanish_uses_spanish_voices()
    {
        Assert.Equal(KokoroTts.VoiceFor("Santiago Tower", AtcLanguage.English), KokoroTts.VoiceFor("Santiago Tower", AtcLanguage.English));
        Assert.Contains(KokoroTts.VoiceFor("Santiago Torre", AtcLanguage.Spanish), KokoroTts.SpanishVoices);
        Assert.Contains(KokoroTts.VoiceFor("Santiago Tower", AtcLanguage.English), KokoroTts.EnglishVoices);
        Assert.Equal("es-419", KokoroTts.EspeakVoice(AtcLanguage.Spanish)); // seseo, no castellano (ADR-053)
    }

    [Fact]
    public void Installer_lists_the_official_files_with_their_sha256()
    {
        Assert.Equal(3, KokoroInstaller.Files.Count);
        Assert.All(KokoroInstaller.Files, f => Assert.Equal(64, f.Sha256!.Length));
        Assert.All(KokoroInstaller.Files, f => Assert.DoesNotContain("/main/", f.Url)); // fijados, no la rama que cambia
        Assert.InRange(KokoroInstaller.TotalBytes / 1e6, 350, 360);
    }

    /// <summary>
    /// Con Kokoro instalado en este PC (como el de Marco): el proceso aparte genera la frase, la segunda vez sale de la
    /// caché y al cerrarlo se libera. Sin Kokoro instalado (otro PC), no hay nada que probar.
    /// </summary>
    [Fact]
    public async Task The_host_process_speaks_and_caches()
    {
        var root = Path.Combine(AppPaths.Root, "voice");
        var host = Path.Combine(AppContext.BaseDirectory, "SimATC.KokoroHost.exe");
        if (!KokoroInstaller.IsInstalled(root) || !File.Exists(Path.Combine(root, "piper", "espeak-ng.dll")))
            return;
        using var tts = new KokoroTts(host, KokoroInstaller.Folder(root), Path.Combine(root, "piper"));
        Assert.True(tts.Available);
        var clip = await tts.SynthesizeAsync("Lan Chile two zero fife, runway one seven left, cleared for take-off.", "am_michael",
            AtcLanguage.English, 1.0, CancellationToken.None);
        Assert.NotNull(clip);
        Assert.InRange(clip!.DurationS, 2, 12);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var again = await tts.SynthesizeAsync("Lan Chile two zero fife, runway one seven left, cleared for take-off.", "am_michael",
            AtcLanguage.English, 1.0, CancellationToken.None);
        Assert.Same(clip, again);
        Assert.True(sw.Elapsed.TotalSeconds < 0.5, "la frase repetida no salió de la caché");
        var es = await tts.SynthesizeAsync("Lan Chile dos cero cinco, autorizado a despegar.", "em_alex", AtcLanguage.Spanish, 1.0,
            CancellationToken.None);
        Assert.NotNull(es);
    }
}
