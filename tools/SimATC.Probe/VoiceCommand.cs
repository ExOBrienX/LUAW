using System.Diagnostics;
using SimATC.Core;
using SimATC.Core.Comms;
using SimATC.Voice;

/// <summary>
/// <c>--voice [es]</c>: prueba la voz sin el simulador (etapa 5b). Cada voz instalada del idioma dice una frase del ATC
/// (forma hablada, §8.2) con el efecto de radio, y se mide cuánto tarda Piper en generarla.
/// </summary>
internal static class VoiceCommand
{
    public static async Task<int> RunAsync(bool spanish)
    {
        var root = Path.Combine(AppPaths.Root, "voice");
        var lang = spanish ? AtcLanguage.Spanish : AtcLanguage.English;
        var tts = new PiperTts(VoiceInstaller.PiperExe(root), Console.WriteLine);
        var catalog = VoiceCatalog.Scan(VoiceInstaller.VoicesFolder(root), Console.WriteLine);
        if (!tts.Available || catalog.For(lang).Count == 0)
        {
            Console.WriteLine($"La voz no está instalada en {root}.");
            return 1;
        }
        // TEST DATA: frase de ejemplo.
        var text = spanish
            ? "LAN205, ruede a punto de espera pista 17R vía G, Q, H"
            : "LAN205, taxi to holding point runway 17R via G, Q, H";
        var airlines = Airlines();
        var spoken = TtsText.Prepare(SpokenForm.Of(text, airlines, lang), lang, airlines is null ? null : airlines.IsTelephonyWord);
        Console.WriteLine($"Frase: {spoken}");
        var player = new AudioPlayer();
        foreach (var v in catalog.For(lang))
        {
            var speaker = v.Speakers > 1 ? v.Speakers / 3 : 0;
            var sw = Stopwatch.StartNew();
            var clip = await tts.SynthesizeAsync(spoken, new VoiceChoice(v, speaker), 1.0, CancellationToken.None);
            if (clip is null)
            {
                Console.WriteLine($"{v.Id}: no se pudo generar.");
                continue;
            }
            var radio = RadioFilter.Apply(clip);
            Console.WriteLine($"{v.Id} (hablante {speaker} de {v.Speakers}): {clip.DurationS:0.0} s de audio en {sw.Elapsed.TotalSeconds:0.00} s");
            await player.PlayAsync(radio, 0.8f, CancellationToken.None);
        }
        return 0;
    }

    private static AirlineTelephony? _airlines;

    /// <summary>La telefonía de <c>data/airlines.json</c> (LAN205 → "LAN CHILE dos cero cinco"), como en la app; el Probe corre desde la raíz del repo.</summary>
    public static AirlineTelephony? Airlines()
    {
        var path = AirlineTelephony.PathFor("data");
        return _airlines ??= File.Exists(path) ? AirlineTelephony.Load(path) : null;
    }
}
