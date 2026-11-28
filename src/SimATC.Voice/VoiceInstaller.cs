using System.IO.Compression;
using System.Security.Cryptography;

using SimATC.Core.Text;

namespace SimATC.Voice;

/// <summary>Un archivo de la instalación de voz: de dónde se baja, cuánto pesa, cómo se verifica y su licencia.</summary>
public sealed record VoiceFile(string Name, string Url, long SizeBytes, string? Md5, string License, string RelativePath, string? Sha256 = null);

/// <summary>
/// Instala la voz (etapa 5b, ADR-051) en <c>%LOCALAPPDATA%\LUAW\voice</c>: Piper para Windows (proceso aparte) y las
/// voces elegidas, todas con licencia que permite uso comercial (V-31). Solo descarga cuando el piloto lo pide desde
/// Configuración. Cada archivo se verifica (MD5 del índice oficial de voces o, en Piper, el tamaño exacto) y se escribe
/// primero a un temporal: una descarga cortada no deja un modelo roto.
/// </summary>
public static class VoiceInstaller
{
    /// <summary>
    /// rhasspy/piper-voices fijado al commit del 2026-10-30, el revisado el 2026-11-01 (ADR-159): en <c>main</c> se reentrenan
    /// voces (en noviembre cambiaron joe, davefx y sharvard) y la verificación de tamaño las rechazaba.
    /// </summary>
    private const string Hf = "https://huggingface.co/rhasspy/piper-voices/resolve/b4e358ffcb232591bf73a540e70f7de528e4174a/";

    /// <summary>Piper 2023.11.14-2 para Windows (rhasspy/piper, GitHub; ejecutable autónomo, sin Python).</summary>
    public static readonly VoiceFile Piper = new("Piper 2023.11.14-2 (Windows x64)",
        "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip", 22_477_236, null,
        "MIT (incluye espeak-ng, GPL-3.0: se usa como proceso aparte)", "piper_windows_amd64.zip");

    /// <summary>Voces (índice oficial <c>rhasspy/piper-voices/voices.json</c>, revisado el 2026-11-01).</summary>
    public static readonly IReadOnlyList<VoiceFile> Voices =
    [
        .. V("en/en_US/joe/medium/en_US-joe-medium", 63_201_294, "74fd6a4dc39e0aa9dce145d7f5acd4f6", 4_794, "811036b9c1451545f9495fdc1baa0754", "CC0"),
        // El modelo y su configuración se actualizaron en Hugging Face (904 hablantes) y voices.json quedó con los tamaños y MD5
        // anteriores: el modelo se verifica con el SHA-256 que publica el servidor (X-Linked-ETag) y la configuración, con el
        // SHA-256 de la bajada del 2026-11-01.
        .. V("en/en_US/libritts_r/medium/en_US-libritts_r-medium", 78_580_914, null, 20_123, null, "CC BY 4.0 (LibriTTS-R, 904 hablantes)",
            "10bb85e071d616fcf4071f369f1799d0491492ab3c5d552ec19fb548fac13195", "b471dc60d2d8335e819c393d196d6fbf792817f40051257b269878505bc9afb3"),
        .. V("en/en_GB/vctk/medium/en_GB-vctk-medium", 76_952_753, "573025290fdc68812543b7438ace0c29", 6_637, "68e82dee4c9a723e804f9fc0b14f1802", "CC BY 4.0 (VCTK, 109 hablantes)"),
        .. V("es/es_ES/davefx/medium/es_ES-davefx-medium", 63_201_294, "dc515cd4ecc5f6f72fe14a941188fc9c", 4_817, "dd157b5eaf6930bf949cf416d9a9307a", "CC0"),
        .. V("es/es_ES/sharvard/medium/es_ES-sharvard-medium", 76_733_615, "77e6f9c26e92799fb04bb90b46bf1834", 4_903, "e49cab310ce362145f7d94b347c291ff", "CC BY 3.0 (2 hablantes)"),
        .. V("es/es_MX/ald/medium/es_MX-ald-medium", 63_201_294, "86374058e59b41ac3b7fe4181e1daad6", 4_878, "aedc07cd37e61360144f04aebb05f7dd", "Unlicense"),
        .. V("es/es_MX/claude/high/es_MX-claude-high", 63_122_309, "cb1966e0ff20ca3aa010f6c9a0ce296a", 4_963, "36882201922fabb409f18d284af3eddd", "Apache-2.0"),
    ];

    // Cada voz son dos archivos: el modelo (.onnx) y su configuración (.onnx.json).
    private static VoiceFile[] V(string path, long size, string? md5, long jsonSize, string? jsonMd5, string license, string? sha256 = null,
        string? jsonSha256 = null)
    {
        var name = Path.GetFileName(path);
        return
        [
            new VoiceFile(name, $"{Hf}{path}.onnx", size, md5, license, $"voices/{name}.onnx", sha256),
            new VoiceFile(name + " (config)", $"{Hf}{path}.onnx.json", jsonSize, jsonMd5, license, $"voices/{name}.onnx.json", jsonSha256),
        ];
    }

    public static IReadOnlyList<VoiceFile> AllFiles => [Piper, .. Voices];

    public static long TotalBytes => AllFiles.Sum(f => f.SizeBytes);

    public static string PiperExe(string root) => Path.Combine(root, "piper", "piper.exe");

    public static string VoicesFolder(string root) => Path.Combine(root, "voices");

    /// <summary>Ya está todo instalado (Piper y al menos una voz).</summary>
    public static bool IsInstalled(string root) => File.Exists(PiperExe(root)) && Directory.Exists(VoicesFolder(root))
        && Directory.EnumerateFiles(VoicesFolder(root), "*.onnx").Any();

    /// <summary>Baja e instala lo que falte. <paramref name="progress"/> recibe un texto para la interfaz.</summary>
    public static async Task InstallAsync(string root, HttpClient http, IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(VoicesFolder(root));
        var files = AllFiles;
        long done = 0;
        var total = TotalBytes;
        foreach (var f in files)
        {
            var target = Path.Combine(root, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var installed = f == Piper ? File.Exists(PiperExe(root)) : File.Exists(target) && new FileInfo(target).Length == f.SizeBytes;
            if (!installed)
            {
                progress.Report(Tr.F("Bajando {0} ({1:0.0} MB)… {2} %", f.Name, f.SizeBytes / 1e6, done * 100 / total));
                var temp = target + ".part";
                await DownloadAsync(http, f.Url, temp, ct);
                Verify(f, temp);
                File.Move(temp, target, overwrite: true);
                if (f == Piper)
                {
                    // El zip trae la carpeta "piper/" con piper.exe, sus bibliotecas y los datos de espeak-ng.
                    ZipFile.ExtractToDirectory(target, root, overwriteFiles: true);
                    File.Delete(target);
                }
            }
            done += f.SizeBytes;
        }
        if (!File.Exists(PiperExe(root)))
            throw new InvalidDataException(Tr.T("El zip de Piper no trajo piper.exe donde se esperaba."));
        progress.Report(Tr.T("Voz instalada."));
    }

    internal static async Task DownloadAsync(HttpClient http, string url, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(path);
        await input.CopyToAsync(output, ct);
    }

    internal static void Verify(VoiceFile f, string path)
    {
        var size = new FileInfo(path).Length;
        if (size != f.SizeBytes)
        {
            File.Delete(path);
            throw new InvalidDataException(Tr.F("{0}: llegaron {1} bytes y se esperaban {2}.", f.Name, size, f.SizeBytes));
        }
        if (f.Sha256 is { } sha)
        {
            using var stream = File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(stream)) != sha)
            {
                stream.Dispose();
                File.Delete(path);
                throw new InvalidDataException(Tr.F("{0}: el archivo no coincide con el oficial (SHA-256).", f.Name));
            }
        }
        if (f.Md5 is { } md5)
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(MD5.HashData(stream));
            if (hash != md5)
            {
                stream.Dispose();
                File.Delete(path);
                throw new InvalidDataException(Tr.F("{0}: el archivo no coincide con el del índice oficial (MD5).", f.Name));
            }
        }
    }
}

/// <summary>
/// Kokoro-82M para la voz del ATC (ADR-103), opcional: kokoro-onnx v1.0 (Apache-2.0), versión <c>model-files-v1.0</c> de
/// GitHub, con los SHA-256 calculados el 2026-11-09 (GitHub no los publica); config.json fijado a un commit. Necesita el
/// espeak-ng de Piper (se instala con la voz).
/// </summary>
public static class KokoroInstaller
{
    private const string Release = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/";

    public static readonly IReadOnlyList<VoiceFile> Files =
    [
        new("Kokoro v1.0 (modelo)", Release + "kokoro-v1.0.onnx", 325_532_387, null, "Apache-2.0", "kokoro/kokoro-v1.0.onnx",
            "7d5df8ecf7d4b1878015a32686053fd0eebe2bc377234608764cc0ef3636a6c5"),
        new("Kokoro v1.0 (voces)", Release + "voices-v1.0.bin", 28_214_398, null, "Apache-2.0", "kokoro/voices-v1.0.bin",
            "bca610b8308e8d99f32e6fe4197e7ec01679264efed0cac9140fe9c29f1fbf7d"),
        new(Tr.T("Kokoro v1.0 (configuración)"),
            "https://raw.githubusercontent.com/thewh1teagle/kokoro-onnx/46417b406956d55b5e3ffff2db6cf39e86cb897f/src/kokoro_onnx/config.json",
            2_351, null, "MIT", "kokoro/config.json", "5abb01e2403b072bf03d04fde160443e209d7a0dad49a423be15196b9b43c17f"),
    ];

    public static long TotalBytes => Files.Sum(f => f.SizeBytes);

    public static string Folder(string root) => Path.Combine(root, "kokoro");

    /// <summary>Todos los archivos con su tamaño exacto (los de una prueba anterior con otro tamaño no cuentan).</summary>
    public static bool IsInstalled(string root) => Files.All(f =>
    {
        var path = Path.Combine(root, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) && new FileInfo(path).Length == f.SizeBytes;
    });

    public static async Task InstallAsync(string root, HttpClient http, IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder(root));
        long done = 0;
        foreach (var f in Files)
        {
            var target = Path.Combine(root, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!(File.Exists(target) && new FileInfo(target).Length == f.SizeBytes))
            {
                progress.Report(Tr.F("Bajando {0} ({1:0.0} MB)… {2} %", f.Name, f.SizeBytes / 1e6, done * 100 / TotalBytes));
                var temp = target + ".part";
                await VoiceInstaller.DownloadAsync(http, f.Url, temp, ct);
                VoiceInstaller.Verify(f, temp);
                File.Move(temp, target, overwrite: true);
            }
            done += f.SizeBytes;
        }
        progress.Report(Tr.T("Voz Kokoro instalada."));
    }
}

/// <summary>
/// El modelo del reconocimiento de voz (etapa 5c, ADR-126): whisper.cpp "base" multilingüe (MIT), del repositorio oficial
/// de modelos de whisper.cpp en Hugging Face, fijado a un commit; tamaño y SHA-256 son los que publica ese repositorio
/// (consultados el 2026-11-11).
/// </summary>
public static class SttInstaller
{
    public static readonly VoiceFile Model = new(Tr.T("Whisper base (reconocimiento de voz)"),
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin",
        147_951_465, null, "MIT", "whisper/ggml-base.bin", "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe");

    public static string ModelPath(string root) => Path.Combine(root, Model.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static bool IsInstalled(string root) => File.Exists(ModelPath(root)) && new FileInfo(ModelPath(root)).Length == Model.SizeBytes;

    public static async Task InstallAsync(string root, HttpClient http, IProgress<string> progress, CancellationToken ct)
    {
        var target = ModelPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!IsInstalled(root))
        {
            progress.Report(Tr.F("Bajando {0} ({1:0} MB)…", Model.Name, Model.SizeBytes / 1e6));
            var temp = target + ".part";
            await VoiceInstaller.DownloadAsync(http, Model.Url, temp, ct);
            VoiceInstaller.Verify(Model, temp);
            File.Move(temp, target, overwrite: true);
        }
        progress.Report(Tr.T("Reconocimiento de voz instalado."));
    }
}

