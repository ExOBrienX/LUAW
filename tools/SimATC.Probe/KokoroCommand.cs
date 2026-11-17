using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SimATC.Core;
using SimATC.Core.Comms;
using SimATC.Voice;

/// <summary>
/// <c>--kokoro [en] [--threads N]</c>: spike de Kokoro-82M (etapa 5b) para compararlo con Piper. Lee la misma frase con
/// cada voz del idioma y mide cuánto tarda y cuánta CPU usa. Archivos en <c>voice/kokoro</c> (kokoro-onnx v1.0, fp16).
/// Fonemas con el espeak-ng que trae Piper, igual que kokoro-onnx (IPA con acentos, puntuación conservada).
/// </summary>
internal static class KokoroCommand
{
    private const int SampleRate = 24000; // kokoro-onnx config.py
    private const int MaxPhonemes = 510;

    /// <summary><c>--save carpeta</c>: guarda cada frase en WAV en vez de reproducirla.</summary>
    public static string? SaveDir { get; set; }
    private static int _saved;

    /// <summary>WAV PCM de 16 bits mono.</summary>
    public static void WriteWav(string path, AudioClip clip)
    {
        using var w = new BinaryWriter(File.Create(path));
        var data = clip.Samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(clip.SampleRate); w.Write(clip.SampleRate * 2);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (var s in clip.Samples)
            w.Write((short)Math.Clamp(s * 32767, -32768, 32767));
    }

    public static int Run(bool english, int threads, bool fp16)
    {
        var root = Path.Combine(AppPaths.Root, "voice");
        var dir = Path.Combine(root, "kokoro");
        // fp32 por defecto: en CPU el fp16 no tiene kernels propios y sale más lento, sin ganar calidad.
        var model = Path.Combine(dir, fp16 ? "kokoro-v1.0.fp16.onnx" : "kokoro-v1.0.onnx");
        var voicesPath = Path.Combine(dir, "voices-v1.0.bin");
        var piperDir = Path.Combine(root, "piper");
        if (!File.Exists(model) || !File.Exists(voicesPath) || !File.Exists(Path.Combine(dir, "config.json")))
        {
            Console.WriteLine($"Kokoro no está en {dir}.");
            return 1;
        }

        var vocab = LoadVocab(Path.Combine(dir, "config.json"));
        // es-419: español latinoamericano (seseo), no el castellano de "es" (θ en "cinco").
        if (!Espeak.Init(piperDir, english ? "en-us" : "es-419"))
        {
            Console.WriteLine("No se pudo iniciar espeak-ng.");
            return 1;
        }

        var lang = english ? AtcLanguage.English : AtcLanguage.Spanish;
        // TEST DATA: las mismas frases que --voice, más una más larga.
        string[] texts = english
            ? ["LAN205, taxi to holding point runway 17R via G, Q, H",
               "LAN205, cleared to Lima via the ANKUR 1 departure, climb via SID to 6000 feet, squawk 4521"]
            : ["LAN205, ruede a punto de espera pista 17R vía G, Q, H",
               "LAN205, autorizado a Lima vía salida ANKUR 1, ascienda según SID a 6000 pies, transpondedor 4521"];
        string[] voiceNames = english ? ["af_heart", "am_michael", "bf_emma", "bm_george"] : ["ef_dora", "em_alex", "em_santa"];

        using var opts = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        Console.WriteLine($"Modelo: {Path.GetFileName(model)}");
        var proc = Process.GetCurrentProcess();
        var ramBefore = proc.WorkingSet64;
        var sw = Stopwatch.StartNew();
        using var session = new InferenceSession(model, opts);
        proc.Refresh();
        Console.WriteLine($"Modelo cargado en {sw.Elapsed.TotalSeconds:0.00} s, +{(proc.WorkingSet64 - ramBefore) / 1048576} MB de RAM, {threads} hilos");
        foreach (var (name, meta) in session.InputMetadata)
            Console.WriteLine($"  entrada {name}: {meta.ElementDataType} [{string.Join(",", meta.Dimensions)}]");
        var tokensInput = session.InputMetadata.ContainsKey("input_ids") ? "input_ids" : "tokens";

        var player = new AudioPlayer();
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; // como Piper: que no le quite CPU al simulador
        foreach (var text in texts)
        {
            var airlines = VoiceCommand.Airlines();
            var spoken = TtsText.Prepare(SpokenForm.Of(text, airlines, lang), lang, airlines is null ? null : airlines.IsTelephonyWord);
            var phonemes = Phonemize(spoken);
            var tokens = Tokenize(phonemes, vocab);
            Console.WriteLine($"\nFrase: {spoken}\nFonemas ({tokens.Count}): {phonemes}");
            foreach (var voice in voiceNames)
            {
                var style = LoadStyle(voicesPath, voice, tokens.Count);
                var cpu0 = proc.TotalProcessorTime;
                sw.Restart();
                var clip = Synthesize(session, tokensInput, tokens, style, 1.0f);
                var wall = sw.Elapsed.TotalSeconds;
                proc.Refresh();
                var cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds;
                Console.WriteLine($"{voice}: {clip.DurationS:0.0} s de audio en {wall:0.00} s (RTF {wall / clip.DurationS:0.00}), " +
                                  $"CPU {cpu:0.00} s = {cpu / wall / Environment.ProcessorCount * 100:0}% del PC durante la frase");
                if (SaveDir is { } save)
                {
                    // Muestras para escuchar (bloque C), sin el filtro de radio, para comparar con Piper.
                    Directory.CreateDirectory(save);
                    WriteWav(Path.Combine(save, $"kokoro-{voice}-{++_saved}.wav"), clip); // limpio, para comparar con Piper
                }
                else
                {
                    player.PlayAsync(RadioFilter.Apply(clip), 0.8f, CancellationToken.None).GetAwaiter().GetResult();
                }
            }
        }
        proc.Refresh();
        Console.WriteLine($"\nRAM del proceso al final: {proc.WorkingSet64 / 1048576} MB");
        return 0;
    }

    private static AudioClip Synthesize(InferenceSession session, string tokensInput, List<int> tokens, float[] style, float speed)
    {
        var ids = new long[tokens.Count + 2]; // 0 al inicio y al final, como kokoro-onnx
        for (var i = 0; i < tokens.Count; i++)
            ids[i + 1] = tokens[i];
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(tokensInput, new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("style", new DenseTensor<float>(style, [1, style.Length])),
            NamedOnnxValue.CreateFromTensor("speed", new DenseTensor<float>(new[] { speed }, [1])),
        };
        using var results = session.Run(inputs);
        var audio = results[0].AsEnumerable<float>().ToArray();
        return new AudioClip(audio, SampleRate);
    }

    /// <summary>Como <c>phonemizer</c> con <c>preserve_punctuation</c>: se fonemiza cada trozo y se reponen los signos.</summary>
    private static string Phonemize(string text)
    {
        var sb = new StringBuilder();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && ",.;:!?".IndexOf(text[i]) < 0)
                continue;
            var part = text[start..i].Trim();
            if (part.Length > 0)
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
                sb.Append(Espeak.ToIpa(part));
            }
            if (i < text.Length)
                sb.Append(text[i]).Append(' ');
            start = i + 1;
        }
        return sb.ToString().Trim();
    }

    private static List<int> Tokenize(string phonemes, Dictionary<string, int> vocab)
    {
        var tokens = new List<int>();
        foreach (var rune in phonemes.EnumerateRunes())
            if (vocab.TryGetValue(rune.ToString(), out var id) && tokens.Count < MaxPhonemes)
                tokens.Add(id); // lo que no está en el vocabulario se descarta, igual que kokoro-onnx
        return tokens;
    }

    private static Dictionary<string, int> LoadVocab(string configPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var vocab = new Dictionary<string, int>();
        foreach (var p in doc.RootElement.GetProperty("vocab").EnumerateObject())
            vocab[p.Name] = p.Value.GetInt32();
        return vocab;
    }

    /// <summary>Las voces son un .npz (zip sin comprimir) con un .npy float32 de (510, 1, 256) por voz; fila n-1 para n fonemas.</summary>
    private static float[] LoadStyle(string voicesPath, string voice, int phonemeCount)
    {
        using var zip = ZipFile.OpenRead(voicesPath);
        using var s = zip.GetEntry(voice + ".npy")!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        var headerLen = 10 + (bytes[8] | bytes[9] << 8); // formato .npy v1
        var all = MemoryMarshal.Cast<byte, float>(bytes[headerLen..]);
        var rows = all.Length / 256;
        var row = Math.Clamp(phonemeCount, 1, rows) - 1;
        return all.Slice(row * 256, 256).ToArray();
    }

    /// <summary>El espeak-ng.dll que trae Piper (API de speak_lib.h).</summary>
    private static class Espeak
    {
        private const int AudioOutputRetrieval = 1;
        private const int CharsUtf8 = 1;
        private const int PhonemesIpa = 0x02;
        private static nint _lib;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitializeFn(int output, int buflength, nint path, int options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetVoiceByNameFn(nint name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint TextToPhonemesFn(ref nint textptr, int textmode, int phonememode);

        private static TextToPhonemesFn? _toPhonemes;

        public static bool Init(string piperDir, string voice)
        {
            if (!NativeLibrary.TryLoad(Path.Combine(piperDir, "espeak-ng.dll"), out _lib))
                return false;
            var init = Marshal.GetDelegateForFunctionPointer<InitializeFn>(NativeLibrary.GetExport(_lib, "espeak_Initialize"));
            var setVoice = Marshal.GetDelegateForFunctionPointer<SetVoiceByNameFn>(NativeLibrary.GetExport(_lib, "espeak_SetVoiceByName"));
            _toPhonemes = Marshal.GetDelegateForFunctionPointer<TextToPhonemesFn>(NativeLibrary.GetExport(_lib, "espeak_TextToPhonemes"));
            var path = Marshal.StringToCoTaskMemUTF8(Path.Combine(piperDir, "espeak-ng-data"));
            var name = Marshal.StringToCoTaskMemUTF8(voice);
            try
            {
                return init(AudioOutputRetrieval, 0, path, 0) > 0 && setVoice(name) == 0;
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
                Marshal.FreeCoTaskMem(name);
            }
        }

        /// <summary>espeak_TextToPhonemes devuelve una cláusula por llamada y avanza el puntero hasta dejarlo en null.</summary>
        public static string ToIpa(string text)
        {
            var buf = Marshal.StringToCoTaskMemUTF8(text);
            try
            {
                var ptr = buf;
                var sb = new StringBuilder();
                while (ptr != 0)
                {
                    var r = _toPhonemes!(ref ptr, CharsUtf8, PhonemesIpa);
                    if (r == 0)
                        break;
                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(Marshal.PtrToStringUTF8(r));
                }
                return sb.ToString().Trim();
            }
            finally
            {
                Marshal.FreeCoTaskMem(buf);
            }
        }
    }
}
