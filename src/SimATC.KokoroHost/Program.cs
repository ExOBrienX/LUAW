using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// Voz Kokoro-82M (kokoro-onnx v1.0, Apache-2.0) como proceso persistente (ADR-053, ADR-103).
//
// Uso: SimATC.KokoroHost <carpeta de kokoro> <carpeta de piper> [hilos]
// Protocolo: por cada línea de la entrada estándar "voz<TAB>idioma espeak<TAB>velocidad<TAB>texto" responde por la salida
// estándar un entero de 32 bits con la cantidad de muestras (PCM de 16 bits, mono, 24 kHz) y las muestras; -1 si falló
// (el motivo, por la salida de errores). Termina cuando se cierra la entrada.

var kokoroDir = args.Length > 0 ? args[0] : throw new ArgumentException("Falta la carpeta de Kokoro.");
var piperDir = args.Length > 1 ? args[1] : throw new ArgumentException("Falta la carpeta de Piper (espeak-ng).");
var threads = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 2;

using (var self = Process.GetCurrentProcess())
    self.PriorityClass = ProcessPriorityClass.BelowNormal; // que no le quite CPU al simulador

var engine = new KokoroEngine(kokoroDir, piperDir, threads);
var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
using var stdout = new BinaryWriter(Console.OpenStandardOutput());
string? line;
while ((line = stdin.ReadLine()) is not null)
{
    var parts = line.Split('\t', 4);
    try
    {
        if (parts.Length < 4)
            throw new FormatException("línea sin 4 campos");
        var speed = float.Parse(parts[2], CultureInfo.InvariantCulture);
        var samples = engine.Synthesize(parts[3], parts[0], parts[1], speed);
        stdout.Write(samples.Length);
        foreach (var s in samples)
            stdout.Write((short)Math.Clamp(s * 32767, -32768, 32767));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Kokoro: {ex.Message}");
        stdout.Write(-1);
    }
    stdout.Flush();
}
return 0;

/// <summary>Kokoro-82M en ONNX: fonemas con espeak-ng (IPA), tokens con el vocabulario de config.json, estilo de la voz.</summary>
internal sealed class KokoroEngine
{
    public const int SampleRate = 24000; // kokoro-onnx config.py
    private const int MaxPhonemes = 510;

    private readonly InferenceSession _session;
    private readonly string _tokensInput;
    private readonly Dictionary<string, int> _vocab;
    private readonly ZipArchive _voices;
    private readonly Dictionary<string, float[]> _styles = [];
    private string? _espeakVoice;

    public KokoroEngine(string kokoroDir, string piperDir, int threads)
    {
        var opts = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        _session = new InferenceSession(Path.Combine(kokoroDir, "kokoro-v1.0.onnx"), opts);
        _tokensInput = _session.InputMetadata.ContainsKey("input_ids") ? "input_ids" : "tokens";
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(kokoroDir, "config.json")));
        _vocab = [];
        foreach (var p in doc.RootElement.GetProperty("vocab").EnumerateObject())
            _vocab[p.Name] = p.Value.GetInt32();
        _voices = ZipFile.OpenRead(Path.Combine(kokoroDir, "voices-v1.0.bin"));
        if (!Espeak.Load(piperDir))
            throw new InvalidOperationException("No se pudo cargar el espeak-ng de Piper.");
    }

    public float[] Synthesize(string text, string voice, string espeakVoice, float speed)
    {
        if (_espeakVoice != espeakVoice)
        {
            if (!Espeak.SetVoice(espeakVoice))
                throw new InvalidOperationException($"espeak-ng no tiene la voz {espeakVoice}");
            _espeakVoice = espeakVoice;
        }
        var tokens = Tokenize(Phonemize(text));
        if (tokens.Count == 0)
            return [];
        var ids = new long[tokens.Count + 2]; // 0 al inicio y al final, como kokoro-onnx
        for (var i = 0; i < tokens.Count; i++)
            ids[i + 1] = tokens[i];
        var style = Style(voice, tokens.Count);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_tokensInput, new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("style", new DenseTensor<float>(style, [1, style.Length])),
            NamedOnnxValue.CreateFromTensor("speed", new DenseTensor<float>(new[] { speed }, [1])),
        };
        using var results = _session.Run(inputs);
        return results[0].AsEnumerable<float>().ToArray();
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

    private List<int> Tokenize(string phonemes)
    {
        var tokens = new List<int>();
        foreach (var rune in phonemes.EnumerateRunes())
        {
            if (_vocab.TryGetValue(rune.ToString(), out var id) && tokens.Count < MaxPhonemes)
                tokens.Add(id); // lo que no está en el vocabulario se descarta, igual que kokoro-onnx
        }
        return tokens;
    }

    /// <summary>Las voces son un .npz (zip sin comprimir) con un .npy float32 de (510, 1, 256) por voz; fila n-1 para n fonemas.</summary>
    private float[] Style(string voice, int phonemeCount)
    {
        if (!_styles.TryGetValue(voice, out var all))
        {
            using var s = (_voices.GetEntry(voice + ".npy") ?? throw new InvalidOperationException($"no existe la voz {voice}")).Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            var headerLen = 10 + (bytes[8] | bytes[9] << 8); // formato .npy v1
            all = MemoryMarshal.Cast<byte, float>(bytes[headerLen..]).ToArray();
            _styles[voice] = all;
        }
        var rows = all.Length / 256;
        var row = Math.Clamp(phonemeCount, 1, rows) - 1;
        return all.AsSpan(row * 256, 256).ToArray();
    }
}

/// <summary>El espeak-ng.dll que trae Piper (API de speak_lib.h), cargado solo en este proceso.</summary>
internal static class Espeak
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

    private static SetVoiceByNameFn? _setVoice;
    private static TextToPhonemesFn? _toPhonemes;

    public static bool Load(string piperDir)
    {
        if (!NativeLibrary.TryLoad(Path.Combine(piperDir, "espeak-ng.dll"), out _lib))
            return false;
        var init = Marshal.GetDelegateForFunctionPointer<InitializeFn>(NativeLibrary.GetExport(_lib, "espeak_Initialize"));
        _setVoice = Marshal.GetDelegateForFunctionPointer<SetVoiceByNameFn>(NativeLibrary.GetExport(_lib, "espeak_SetVoiceByName"));
        _toPhonemes = Marshal.GetDelegateForFunctionPointer<TextToPhonemesFn>(NativeLibrary.GetExport(_lib, "espeak_TextToPhonemes"));
        var path = Marshal.StringToCoTaskMemUTF8(Path.Combine(piperDir, "espeak-ng-data"));
        try
        {
            return init(AudioOutputRetrieval, 0, path, 0) > 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    public static bool SetVoice(string voice)
    {
        var name = Marshal.StringToCoTaskMemUTF8(voice);
        try
        {
            return _setVoice!(name) == 0;
        }
        finally
        {
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
