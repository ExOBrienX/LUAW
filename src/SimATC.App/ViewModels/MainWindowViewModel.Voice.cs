using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.App.Audio;
using SimATC.Core;
using SimATC.Core.Comms;
using SimATC.Voice;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Etapa 5b: la voz. El ATC, los pilotos IA, el copiloto y el personal de tierra hablan con Piper (local, en CPU), con
/// efecto de radio y una voz estable cada uno. En la misma frecuencia nunca suenan dos a la vez, y el mensaje aparece en
/// el chat cuando empieza a sonar (el chat va al ritmo de la radio). El volumen sale de Configuración y, si se elige, de
/// la perilla de la radio del avión (<c>COM VOLUME</c>). Sin voz instalada o desactivada, todo sigue por texto.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private RadioVoice? _voice;

    /// <summary>Para bajar las voces (80 MB cada una): sin el límite de 15 s del de SimBrief.</summary>
    private static readonly HttpClient VoiceHttp = CreateVoiceHttp();

    private static HttpClient CreateVoiceHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LUAW/0.5");
        return http;
    }

    private static string VoiceRoot => Path.Combine(AppPaths.Root, "voice");

    private void InitVoice()
    {
        _voice = new RadioVoice(Log) { IsTelephonyWord = w => _airlines.IsTelephonyWord(w) };
        _voice.Load(VoiceRoot);
        ApplyVoiceSettings();
        RefreshVoiceChoices();
    }

    /// <summary>Hay que hablar por voz ahora: activada, instalada y con voces del idioma del ATC.</summary>
    private bool VoiceOn => _settings.VoiceEnabled && _voice?.ReadyFor(_settings.AtcLanguage) == true;

    private void ApplyVoiceSettings()
    {
        if (_voice is null)
            return;
        _voice.Speed = _settings.VoiceSpeed;
        _voice.UseKokoro = _settings.VoiceKokoroAtc && _settings.VoiceEnabled;
        _voice.Preferred[VoiceRole.Atc] = _settings.VoiceAtc;
        _voice.Preferred[VoiceRole.Pilot] = _settings.VoicePilots;
        _voice.Preferred[VoiceRole.Copilot] = _settings.VoiceCopilot;
        _voice.Preferred[VoiceRole.GroundCrew] = _settings.VoiceCopilot;
    }

    /// <summary>Volumen de una frecuencia: el de Configuración por el de la perilla de la radio que la escucha (si se usa).</summary>
    private float VoiceVolume(int khz)
    {
        var v = _settings.VoiceVolume / 100f;
        if (_settings.VoiceFollowComVolume && khz != RadioVoice.Interphone && _radios?.Hearing(khz) is { VolumePercent: { } knob })
            v *= (float)Math.Clamp(knob / 100, 0, 1);
        return v;
    }

    /// <summary>Lo dice por voz (si está activada) y llama a <paramref name="show"/> cuando empieza a sonar.</summary>
    private Task Speak(int khz, VoiceRole role, string who, string text, Action<double?> show)
    {
        if (!VoiceOn || _voice is null)
        {
            show(null);
            return Task.CompletedTask;
        }
        var spoken = SpokenForm.Of(text, _airlines, _settings.AtcLanguage);
        // Simrate 2 → 1,25×; 3 o más → 1,5× (ADR-077).
        _voice.RateFactor = 1 + 0.25 * Math.Max(0, (_engine?.SimRate ?? 1) - 1);
        return _voice.SpeakAsync(khz, role, who, spoken, _settings.AtcLanguage, () => VoiceVolume(khz), show);
    }

    // --- Configuración → Voz ---

    public bool VoiceEnabled
    {
        get => _settings.VoiceEnabled;
        set => Change(_settings with { VoiceEnabled = value }, nameof(VoiceEnabled));
    }

    public decimal VoiceVolumePercent
    {
        get => _settings.VoiceVolume;
        set => Change(_settings with { VoiceVolume = (int)Math.Clamp(value, 0, 100) }, nameof(VoiceVolumePercent));
    }

    public bool VoiceFollowComVolume
    {
        get => _settings.VoiceFollowComVolume;
        set => Change(_settings with { VoiceFollowComVolume = value }, nameof(VoiceFollowComVolume));
    }

    public IReadOnlyList<Choice<double>> VoiceSpeedChoices { get; } =
        [new(0.9, Tr.T("Pausada")), new(1.0, Tr.T("Normal")), new(1.15, Tr.T("Rápida (frecuencia ocupada)"))];

    public Choice<double> SelectedVoiceSpeed
    {
        get => VoiceSpeedChoices.FirstOrDefault(c => Math.Abs(c.Value - _settings.VoiceSpeed) < 0.01) ?? VoiceSpeedChoices[1];
        set
        {
            Change(_settings with { VoiceSpeed = value.Value }, nameof(SelectedVoiceSpeed));
            ApplyVoiceSettings();
        }
    }

    /// <summary>Las voces del idioma del ATC (más "Automática"), para elegir por rol.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Choice<string?>> VoiceChoices { get; set; } = [];

    public bool VoiceInstalled => _voice?.Installed == true;
    public bool VoiceNotInstalled => !VoiceInstalled;

    public string VoiceInstallText => Tr.F("Descargar e instalar la voz ({0:0} MB)", VoiceInstaller.TotalBytes / 1e6);

    [ObservableProperty]
    public partial string VoiceStatus { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallVoiceCommand))]
    public partial bool VoiceInstalling { get; set; }

    private void RefreshVoiceChoices()
    {
        var voices = _voice?.Catalog.For(_settings.AtcLanguage) ?? [];
        VoiceChoices = [new(null, Tr.T("Automática (una por dependencia y por avión)")), .. voices.Select(v => new Choice<string?>(v.Id, VoiceLabel(v)))];
        OnPropertyChanged(nameof(SelectedVoiceAtc));
        OnPropertyChanged(nameof(SelectedVoicePilots));
        OnPropertyChanged(nameof(SelectedVoiceCopilot));
        OnPropertyChanged(nameof(VoiceInstalled));
        OnPropertyChanged(nameof(VoiceNotInstalled));
        VoiceStatus = VoiceInstalled
            ? _voice!.ReadyFor(_settings.AtcLanguage)
                ? Tr.F("Instalada: {0} voces en el idioma del ATC.", voices.Count)
                : Tr.T("Instalada, pero sin voces en el idioma del ATC.")
            : Tr.T("La voz no está instalada.");
    }

    private static string VoiceLabel(PiperVoice v) => v.Speakers > 1 ? Tr.F("{0} ({1} voces)", v.Id, v.Speakers) : v.Id;

    private Choice<string?> VoiceChoiceFor(string? id) => VoiceChoices.FirstOrDefault(c => c.Value == id) ?? VoiceChoices.FirstOrDefault() ?? new(null, Tr.T("Automática"));

    public Choice<string?> SelectedVoiceAtc
    {
        get => VoiceChoiceFor(_settings.VoiceAtc);
        set
        {
            if (value is null)
                return;
            Change(_settings with { VoiceAtc = value.Value }, nameof(SelectedVoiceAtc));
            ApplyVoiceSettings();
        }
    }

    public Choice<string?> SelectedVoicePilots
    {
        get => VoiceChoiceFor(_settings.VoicePilots);
        set
        {
            if (value is null)
                return;
            Change(_settings with { VoicePilots = value.Value }, nameof(SelectedVoicePilots));
            ApplyVoiceSettings();
        }
    }

    public Choice<string?> SelectedVoiceCopilot
    {
        get => VoiceChoiceFor(_settings.VoiceCopilot);
        set
        {
            if (value is null)
                return;
            Change(_settings with { VoiceCopilot = value.Value }, nameof(SelectedVoiceCopilot));
            ApplyVoiceSettings();
        }
    }

    /// <summary>Demo: una frase de ejemplo con la voz elegida para ese rol (o la primera del idioma, si es automática).</summary>
    [RelayCommand]
    private async Task DemoVoiceAsync(string role)
    {
        if (_voice is null || !_voice.ReadyFor(_settings.AtcLanguage))
        {
            VoiceStatus = Tr.T("Instala la voz primero.");
            return;
        }
        var id = role switch { "pilots" => _settings.VoicePilots, "copilot" => _settings.VoiceCopilot, _ => _settings.VoiceAtc }
                 ?? _voice.Catalog.For(_settings.AtcLanguage)[0].Id;
        // TEST DATA: frases de ejemplo de la demo (las plantillas reales salen de data/phraseology).
        var sample = _settings.AtcLanguage == AtcLanguage.Spanish
            ? Tr.T("ABC123, ruede a punto de espera pista 09 vía Alfa, Bravo")
            : Tr.T("ABC123, taxi to holding point runway 09 via Alfa, Bravo");
        VoiceStatus = Tr.F("Probando {0}…", id);
        await _voice.DemoAsync(id, SpokenForm.Of(sample, _airlines, _settings.AtcLanguage), _settings.VoiceVolume / 100f);
        VoiceStatus = Tr.F("Voz {0}.", id);
    }

    private bool CanInstallVoice() => !VoiceInstalling;

    /// <summary>Voz Kokoro para el ATC (ADR-103): al activarla se descarga si falta (354 MB).</summary>
    public bool VoiceKokoroAtc
    {
        get => _settings.VoiceKokoroAtc;
        set
        {
            Change(_settings with { VoiceKokoroAtc = value }, nameof(VoiceKokoroAtc));
            ApplyVoiceSettings();
            OnPropertyChanged(nameof(KokoroStatus));
            if (value && _voice is { KokoroInstalled: false } && !VoiceInstalling)
                _ = InstallKokoroAsync();
        }
    }

    public string KokoroLabel => Tr.F("Voz más natural para el ATC (Kokoro): descarga de {0:0} MB, ", KokoroInstaller.TotalBytes / 1e6) +
                                 Tr.T("~550 MB de RAM y algo más de CPU mientras habla; responde unos 2 s más tarde. Los IA siguen con Piper.");

    public string KokoroStatus => !_settings.VoiceKokoroAtc ? Tr.T("Desactivado: el ATC habla con Piper y Kokoro no ocupa memoria.")
        : _voice?.KokoroInstalled == true ? Tr.T("Kokoro instalado: el ATC habla con Kokoro.")
        : Tr.T("Kokoro no está instalado todavía.");

    private async Task InstallKokoroAsync()
    {
        VoiceInstalling = true;
        try
        {
            var progress = new Progress<string>(s => VoiceStatus = s);
            await KokoroInstaller.InstallAsync(VoiceRoot, VoiceHttp, progress, CancellationToken.None);
            _voice?.Load(VoiceRoot);
            ApplyVoiceSettings();
            Log(Tr.T("Voz Kokoro instalada."));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException
                                       or UnauthorizedAccessException)
        {
            VoiceStatus = Tr.F("No se pudo instalar Kokoro: {0}", ex.Message);
            Log(Tr.F("Voz Kokoro: no se pudo instalar ({0}).", ex.Message));
        }
        finally
        {
            VoiceInstalling = false;
            OnPropertyChanged(nameof(KokoroStatus));
        }
    }

    /// <summary>Prueba la voz Kokoro del ATC.</summary>
    [RelayCommand]
    private async Task DemoKokoroAsync()
    {
        if (_voice is null)
            return;
        var sample = _settings.AtcLanguage == AtcLanguage.Spanish
            ? Tr.T("ABC123, pista 09, autorizado a despegar")
            : Tr.T("ABC123, runway 09, cleared for take-off");
        VoiceStatus = Tr.T("Probando Kokoro (la primera vez carga el modelo, unos segundos)…");
        var ok = await _voice.DemoKokoroAsync(Tr.T("Torre"), SpokenForm.Of(sample, _airlines, _settings.AtcLanguage), _settings.AtcLanguage,
            _settings.VoiceVolume / 100f);
        VoiceStatus = ok ? Tr.T("Voz Kokoro.") : Tr.T("Kokoro no está instalado o no respondió.");
    }

    /// <summary>Baja e instala Piper y las voces (lo pide el piloto con el botón; ver la lista en VoiceInstaller).</summary>
    [RelayCommand(CanExecute = nameof(CanInstallVoice))]
    private async Task InstallVoiceAsync()
    {
        VoiceInstalling = true;
        try
        {
            var progress = new Progress<string>(s => VoiceStatus = s);
            await VoiceInstaller.InstallAsync(VoiceRoot, VoiceHttp, progress, CancellationToken.None);
            _voice?.Load(VoiceRoot);
            RefreshVoiceChoices();
            if (!_settings.VoiceEnabled)
                VoiceEnabled = true;
            Log(Tr.T("Voz instalada."));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException
                                       or UnauthorizedAccessException)
        {
            VoiceStatus = Tr.F("No se pudo instalar: {0}", ex.Message);
            Log(Tr.F("Voz: no se pudo instalar ({0}).", ex.Message));
        }
        finally
        {
            VoiceInstalling = false;
        }
    }
}
