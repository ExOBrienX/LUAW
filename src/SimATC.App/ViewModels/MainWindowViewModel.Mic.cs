using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimATC.App.Audio;
using SimATC.Core;
using SimATC.Core.Comms;
using SimATC.Voice;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Hablarle al ATC por el micrófono (RF-32, etapa 5c, ADR-126): tecla para hablar elegida por el piloto, o el micrófono
/// se abre solo (con un pitido) cuando hay que colacionar y se cierra al dejar de hablar. Lo grabado va a whisper local,
/// sesgado a las frases esperadas; si calza con una, se envía; si no, queda en el campo para corregirlo. El micrófono solo
/// está abierto al hablar, al probarlo o con Configuración a la vista (para la barra de nivel); el modelo se carga la
/// primera vez que se usa.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Silencio (ms) después de hablar con el que se da por terminada la frase en modo automático.</summary>
    public const int MicSilenceMs = 1100;

    /// <summary>Sin oír nada en este tiempo (ms), el micrófono automático se cierra sin transcribir.</summary>
    public const int MicNoSpeechMs = 8000;

    /// <summary>Duración máxima (ms) de una transmisión del piloto.</summary>
    public const int MicMaxMs = 25000;

    /// <summary>Cada cuánto (ms, desde que terminó la anterior) se reconoce lo que se lleva dicho para mostrarlo en vivo.</summary>
    public const int MicPreviewEveryMs = 700;

    private readonly DispatcherTimer _micTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private MicCapture? _mic;
    private WhisperStt? _stt;
    private float _micPeak;
    private bool _listening;
    private bool _listenAuto;
    private bool _heardSpeech;
    private long _listenStartMs;
    private long _lastVoiceMs;
    private bool _pttDown;
    private bool _transcribing;
    private bool _testingMic;
    private bool _capturingKey;
    private int _autoListenedFor = -1;
    // Vista previa en vivo (ADR-127): lo que se va entendiendo aparece en el campo de texto mientras se habla.
    private bool _previewing;
    private bool _previewShown;
    private long _lastPreviewMs;
    private CancellationTokenSource? _previewCts;
    private string? _glossary;
    private Phraseology? _glossaryFor;

    private void InitMic()
    {
        _micTimer.Tick += (_, _) => MicTick();
        RefreshMicDevices();
        UpdateMicTimer();
    }

    /// <summary>Al cerrar la app: suelta el micrófono y el modelo.</summary>
    public void DisposeMic()
    {
        _micTimer.Stop();
        _mic?.Dispose();
        _mic = null;
        _stt?.Dispose();
        _stt = null;
    }

    // ---------- Ajustes ----------

    public bool MicEnabled
    {
        get => _settings.MicEnabled;
        set
        {
            Change(_settings with { MicEnabled = value }, nameof(MicEnabled));
            if (value && !SttInstalled && !SttInstalling)
                _ = InstallSttAsync();
            if (!value)
                CancelListening();
            UpdateMicTimer();
        }
    }

    public static readonly string DefaultMic = Tr.T("Predeterminado de Windows");

    [ObservableProperty]
    public partial IReadOnlyList<string> MicDevices { get; set; } = [DefaultMic];

    public string SelectedMicDevice
    {
        get => _settings.MicDevice is { } d && MicDevices.Contains(d) ? d : DefaultMic;
        set
        {
            if (value is null)
                return;
            Change(_settings with { MicDevice = value == DefaultMic ? null : value }, nameof(SelectedMicDevice));
            // Se vuelve a abrir con el nuevo.
            _mic?.Close();
        }
    }

    [RelayCommand]
    private void RefreshMicDevices()
    {
        MicDevices = [DefaultMic, .. MicCapture.Devices()];
        OnPropertyChanged(nameof(SelectedMicDevice));
    }

    public double MicGainPercent
    {
        get => _settings.MicGain;
        set
        {
            Change(_settings with { MicGain = (int)Math.Clamp(value, 10, 300) }, nameof(MicGainPercent), nameof(MicGainText));
            if (_mic is not null)
                _mic.Gain = _settings.MicGain / 100f;
        }
    }

    public string MicGainText => $"{_settings.MicGain} %";

    public double MicThresholdPercent
    {
        get => _settings.MicThreshold;
        set => Change(_settings with { MicThreshold = (int)Math.Clamp(value, 5, 95) }, nameof(MicThresholdPercent));
    }

    public bool MicAutoListen
    {
        get => _settings.MicAutoListen;
        set => Change(_settings with { MicAutoListen = value }, nameof(MicAutoListen));
    }

    public bool MicBeepOn
    {
        get => _settings.MicBeep;
        set => Change(_settings with { MicBeep = value }, nameof(MicBeepOn));
    }

    public bool MicAutoSend
    {
        get => _settings.MicAutoSend;
        set => Change(_settings with { MicAutoSend = value }, nameof(MicAutoSend));
    }

    public bool MicLivePreview
    {
        get => _settings.MicLivePreview;
        set => Change(_settings with { MicLivePreview = value }, nameof(MicLivePreview));
    }

    public string PttKeyText => _capturingKey ? Tr.T("Presiona la tecla que quieras usar…") : PttKey.Name(_settings.MicPttKey);

    public bool HasPttKey => _settings.MicPttKey > 0;

    /// <summary>"Elegir tecla": la próxima tecla que se apriete (en cualquier ventana) queda como tecla para hablar.</summary>
    [RelayCommand]
    private void ChoosePttKey()
    {
        _capturingKey = true;
        OnPropertyChanged(nameof(PttKeyText));
        UpdateMicTimer();
    }

    [RelayCommand]
    private void ClearPttKey()
    {
        _capturingKey = false;
        Change(_settings with { MicPttKey = 0 }, nameof(PttKeyText), nameof(HasPttKey));
    }

    /// <summary>Nivel del micrófono para la barra (0 a 100, la misma escala que el umbral).</summary>
    [ObservableProperty]
    public partial double MicLevel { get; set; }

    /// <summary>El nivel pasa el umbral: cuenta como voz (la barra cambia de color).</summary>
    [ObservableProperty]
    public partial bool MicSpeaking { get; set; }

    [ObservableProperty]
    public partial string MicStatus { get; set; } = "";

    /// <summary>Lo último que entendió el reconocedor (para verlo en Configuración).</summary>
    [ObservableProperty]
    public partial string LastTranscript { get; set; } = "";

    // ---------- Instalación del reconocimiento ----------

    public bool SttInstalled => SttInstaller.IsInstalled(VoiceRoot);

    public bool SttNotInstalled => !SttInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SttInstallText))]
    public partial bool SttInstalling { get; set; }

    public string SttInstallText => SttInstalling ? Tr.T("Instalando…") : Tr.F("Descargar reconocimiento de voz ({0:0} MB)", SttInstaller.Model.SizeBytes / 1e6);

    [RelayCommand]
    private async Task InstallSttAsync()
    {
        if (SttInstalling || SttInstalled)
            return;
        SttInstalling = true;
        try
        {
            var progress = new Progress<string>(m => MicStatus = m);
            await SttInstaller.InstallAsync(VoiceRoot, VoiceHttp, progress, CancellationToken.None);
            Log(Tr.T("Reconocimiento de voz instalado (whisper base)."));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            MicStatus = Tr.F("No se pudo instalar el reconocimiento de voz: {0}", ex.Message);
            Log(Tr.F("Reconocimiento de voz: no se pudo instalar ({0}).", ex.Message));
        }
        finally
        {
            SttInstalling = false;
            OnPropertyChanged(nameof(SttInstalled));
            OnPropertyChanged(nameof(SttNotInstalled));
        }
    }

    // ---------- Micrófono abierto o cerrado ----------

    private void UpdateMicTimer()
    {
        var needed = _settings.MicEnabled || _capturingKey || _testingMic;
        if (needed && !_micTimer.IsEnabled)
            _micTimer.Start();
        else if (!needed && _micTimer.IsEnabled)
        {
            _micTimer.Stop();
            CloseMic();
        }
    }

    /// <summary>¿Tiene que estar abierto ahora? Al hablar o probar, y con Configuración a la vista o en modo Voz si está activado.</summary>
    private bool MicWanted => _listening || _testingMic || _settings.MicEnabled && (IsSettings || ModeVoice);

    private bool OpenMic()
    {
        if (_mic is { IsOpen: true })
            return true;
        try
        {
            _mic ??= NewMic();
            _mic.Gain = _settings.MicGain / 100f;
            _mic.Open(_settings.MicDevice);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            MicStatus = ex.Message;
            return false;
        }
    }

    private MicCapture NewMic()
    {
        var mic = new MicCapture();
        // Llega desde el hilo del audio: solo se guarda el pico; la interfaz lo lee en su reloj.
        mic.Level += peak =>
        {
            if (peak > _micPeak)
                _micPeak = peak;
        };
        return mic;
    }

    private void CloseMic()
    {
        _mic?.Close();
        MicLevel = 0;
        MicSpeaking = false;
    }

    // ---------- Reloj del micrófono (25 veces por segundo, solo con el micrófono activado) ----------

    private void MicTick()
    {
        if (_capturingKey)
        {
            if (PttKey.FirstPressed() is > 0 and var vk)
            {
                _capturingKey = false;
                _pttDown = true; // esa pulsación no cuenta como "hablar"
                Change(_settings with { MicPttKey = vk }, nameof(PttKeyText), nameof(HasPttKey));
                OnPropertyChanged(nameof(PttKeyText));
                UpdateMicTimer();
            }
            return;
        }
        if (MicWanted)
        {
            if (_mic is not { IsOpen: true } && !OpenMic())
                return;
        }
        else if (_mic is { IsOpen: true })
        {
            CloseMic();
        }

        // Nivel: sube al instante y baja de a poco, como un vúmetro.
        var peak = _micPeak;
        _micPeak = 0;
        var meter = _mic is { IsOpen: true } ? MicCapture.Meter(peak) : 0;
        MicLevel = Math.Max(meter, MicLevel - 5);
        MicSpeaking = MicLevel >= _settings.MicThreshold;

        if (!_settings.MicEnabled || _testingMic)
            return;
        var now = Environment.TickCount64;
        var down = PttKey.IsDown(_settings.MicPttKey);
        var canListen = SttInstalled && !_transcribing;

        // Tecla para hablar: graba mientras está apretada.
        if (down && !_pttDown)
        {
            if (_listening)
                _listenAuto = false; // estaba en automático: pasa a manual (termina al soltar)
            else if (canListen)
                StartListening(auto: false);
        }
        else if (!down && _pttDown && _listening && !_listenAuto)
        {
            _ = StopListeningAsync(transcribe: true);
        }
        _pttDown = down;

        if (_listening)
        {
            if (meter >= _settings.MicThreshold)
            {
                _heardSpeech = true;
                _lastVoiceMs = now;
            }
            // En vivo: se reconoce lo que va grabado y se muestra en el campo de texto (la versión final llega al soltar).
            if (_settings.MicLivePreview && _heardSpeech && !_previewing && canListen && now - _lastPreviewMs >= MicPreviewEveryMs
                && _mic is { RecordedS: >= 1.0 })
                _ = PreviewAsync();
            if (now - _listenStartMs > MicMaxMs)
                _ = StopListeningAsync(transcribe: true);
            else if (_listenAuto && _heardSpeech && now - _lastVoiceMs > MicSilenceMs)
                _ = StopListeningAsync(transcribe: true);
            else if (_listenAuto && !_heardSpeech && now - _listenStartMs > MicNoSpeechMs)
                _ = StopListeningAsync(transcribe: false);
            return;
        }

        // Automático: hay una colación pendiente, el ATC terminó de hablar y ya se ven las sugerencias.
        if (canListen && _settings.MicAutoListen && ShouldAutoListen(out var instruction))
        {
            _autoListenedFor = instruction;
            StartListening(auto: true);
        }
    }

    /// <summary>
    /// ¿Abrir el micrófono solo? Si el piloto tiene que colacionar (no el copiloto), el ATC ya terminó (misma espera que usa
    /// el copiloto) y no se abrió ya para esta instrucción.
    /// </summary>
    private bool ShouldAutoListen(out int instruction)
    {
        instruction = -1;
        if (_session is not { AwaitingReadback: true } s || s.InstructionNumber == _autoListenedFor)
            return false;
        if (_settings.Mode == CommsMode.Copilot || _settings.CopilotReadbacks != CopilotReadbacks.Never)
            return false;
        if (_transmitting || _onAir.Count > 0 || _atcClock.Elapsed.TotalSeconds < _quietUntilS || Comms.Suggestions.Count == 0)
            return false;
        instruction = s.InstructionNumber;
        return true;
    }

    private void StartListening(bool auto)
    {
        if (!OpenMic() || _mic is null)
            return;
        _listening = true;
        _listenAuto = auto;
        _heardSpeech = false;
        _listenStartMs = Environment.TickCount64;
        _lastVoiceMs = _listenStartMs;
        _lastPreviewMs = _listenStartMs;
        _previewShown = false;
        if (_settings.MicBeep)
            MicBeep.On();
        _mic.BeginRecording();
        Comms.MicListening = true;
        Comms.MicState = auto
            ? Tr.T("Micrófono abierto: colaciona ahora (se cierra solo al terminar)")
            : Tr.F("Micrófono abierto: habla y suelta {0}", PttKey.Name(_settings.MicPttKey));
    }

    private void CancelListening()
    {
        if (!_listening)
            return;
        _listening = false;
        _previewCts?.Cancel();
        _mic?.EndRecording();
        Comms.MicListening = false;
        Comms.MicState = null;
        ClearPreview();
    }

    /// <summary>Lo que se escribió en vivo y no llegó a ser una transmisión: se borra del campo.</summary>
    private void ClearPreview()
    {
        if (_previewShown)
            Comms.InputText = "";
        _previewShown = false;
    }

    /// <summary>El contexto para el reconocedor: el vocabulario de la fraseología y, al final, las frases esperadas ahora.</summary>
    private string MicPrompt(IReadOnlyList<(string Text, string Spoken)> expected)
    {
        if (!ReferenceEquals(_glossaryFor, _phrases))
        {
            _glossaryFor = _phrases;
            _glossary = _phrases is { } ph ? SpeechMatcher.Glossary(ph.Templates) : null;
        }
        return SpeechMatcher.Prompt(expected, glossary: _glossary);
    }

    /// <summary>
    /// Reconoce lo grabado hasta ahora, sin cerrar el micrófono, y lo deja en el campo de texto. Es solo una vista previa
    /// (rápida y menos precisa): si falla o se cancela al soltar la tecla, no pasa nada.
    /// </summary>
    private async Task PreviewAsync()
    {
        if (_mic is null)
            return;
        _previewing = true;
        var cts = _previewCts = new CancellationTokenSource();
        try
        {
            var samples = _mic.Snapshot();
            var expected = Comms.Suggestions.Select(x => (x.Text, x.Spoken)).ToList();
            _stt ??= new WhisperStt(SttInstaller.ModelPath(VoiceRoot));
            var language = _settings.AtcLanguage == AtcLanguage.Spanish ? "es" : "en";
            var result = await _stt.TranscribeAsync(samples, language, MicPrompt(expected), cts.Token, quick: true);
            if (_listening && !cts.IsCancellationRequested && result.Text.Length > 0)
            {
                Comms.InputText = result.Text;
                _previewShown = true;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException or DllNotFoundException
                                       or BadImageFormatException or Whisper.net.WhisperModelLoadException or Whisper.net.WhisperProcessingException)
        {
            // Vista previa: la transcripción final avisa si el reconocimiento no funciona.
        }
        finally
        {
            _previewing = false;
            _lastPreviewMs = Environment.TickCount64;
            if (ReferenceEquals(_previewCts, cts))
                _previewCts = null;
            cts.Dispose();
        }
    }

    private async Task StopListeningAsync(bool transcribe)
    {
        if (!_listening || _mic is null)
            return;
        _listening = false;
        _previewCts?.Cancel(); // la vista previa en curso no retrasa la transcripción final
        var samples = _mic.EndRecording();
        Comms.MicListening = false;
        if (_settings.MicBeep)
            MicBeep.Off();
        if (!transcribe || samples.Length < MicCapture.SampleRate * 3 / 10)
        {
            ClearPreview();
            ShowMicNote(transcribe ? Tr.T("Muy corto: mantén la tecla mientras hablas.") : Tr.T("No te oí. Usa la tecla para hablar o escribe la colación."));
            return;
        }
        _transcribing = true;
        Comms.MicState = Tr.T("Transcribiendo…");
        try
        {
            var expected = Comms.Suggestions.Select(x => (x.Text, x.Spoken)).ToList();
            _stt ??= new WhisperStt(SttInstaller.ModelPath(VoiceRoot));
            var language = _settings.AtcLanguage == AtcLanguage.Spanish ? "es" : "en";
            var result = await _stt.TranscribeAsync(samples, language, MicPrompt(expected), CancellationToken.None);
            _previewShown = false; // lo que queda en el campo ya es la versión final
            OnTranscribed(result, expected, samples.Length / (double)MicCapture.SampleRate);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or DllNotFoundException or BadImageFormatException
                                       or Whisper.net.WhisperModelLoadException or Whisper.net.WhisperProcessingException)
        {
            Log(Tr.F("Reconocimiento de voz: falló ({0}).", ex.Message));
            ShowMicNote(Tr.F("El reconocimiento falló: {0}", ex.Message));
        }
        finally
        {
            _transcribing = false;
        }
    }

    /// <summary>
    /// Qué se hace con lo reconocido: si calza con una frase esperada y con los mismos números, se envía esa frase; si calza
    /// pero dijo otro número, se envía lo que dijo (el ATC lo corrige); si no calza, queda en el campo para corregirlo.
    /// </summary>
    private void OnTranscribed(SttResult result, IReadOnlyList<(string Text, string Spoken)> expected, double audioS)
    {
        LastTranscript = result.Text;
        Log(Tr.F("Micrófono: «{0}» (confianza {1:0.00}, {2:0.0} s de audio, {3:0.0} s en reconocer).", result.Text, result.Probability, audioS, result.Seconds));
        if (result.Text.Length == 0)
        {
            Comms.InputText = ""; // lo que hubiera de la vista previa no era nada
            ShowMicNote(Tr.T("No se entendió nada. Repite o escribe."));
            return;
        }
        var match = SpeechMatcher.Best(result.Text, expected);
        var text = result.Text;
        var sure = false;
        if (match is { Score: >= SpeechMatcher.AcceptScore } m)
        {
            var phrase = expected[m.Index];
            sure = true;
            if (SpeechMatcher.SameNumbers(result.Text, phrase.Text) || phrase.Spoken.Length > 0 && SpeechMatcher.SameNumbers(result.Text, phrase.Spoken))
                text = phrase.Text;
        }
        else if (expected.Count == 0 && result.Probability >= 0.6 && result.Text.Contains(' '))
        {
            sure = true; // sin frases esperadas (texto libre) y el reconocedor está seguro
        }
        Comms.InputText = text;
        if (sure && _settings.MicAutoSend)
        {
            Comms.MicState = null;
            Comms.SendCommand.Execute(null);
        }
        else
        {
            ShowMicNote(sure ? Tr.T("Revisa lo que entendí y presiona Enter.") : Tr.T("No estoy seguro de lo que entendí: corrígelo y presiona Enter, o habla de nuevo."), 8);
        }
    }

    private int _micNote;

    /// <summary>Un aviso corto junto al campo de texto, que se borra solo.</summary>
    private void ShowMicNote(string text, int seconds = 5)
    {
        var id = ++_micNote;
        Comms.MicState = text;
        DispatcherTimer.RunOnce(() =>
        {
            if (id == _micNote && !_listening && !_transcribing)
                Comms.MicState = null;
        }, TimeSpan.FromSeconds(seconds));
    }

    // ---------- Probar el micrófono ----------

    /// <summary>"Probar": graba 4 s, te lo hace escuchar y, si el reconocimiento está instalado, muestra lo que entendió.</summary>
    [RelayCommand]
    private async Task TestMicAsync()
    {
        if (_testingMic || _listening)
            return;
        _testingMic = true;
        UpdateMicTimer();
        try
        {
            if (!OpenMic() || _mic is null)
                return;
            _mic.BeginRecording();
            for (var left = 4; left > 0; left--)
            {
                MicStatus = Tr.F("Grabando: di algo… {0}", left);
                await Task.Delay(1000);
            }
            var samples = _mic.EndRecording();
            MicStatus = Tr.T("Así te escuchas…");
            await new AudioPlayer().PlayAsync(new AudioClip(samples, MicCapture.SampleRate), 1f, CancellationToken.None);
            if (!SttInstalled)
            {
                MicStatus = Tr.T("Micrófono probado. Falta instalar el reconocimiento de voz.");
                return;
            }
            MicStatus = Tr.T("Reconociendo…");
            _stt ??= new WhisperStt(SttInstaller.ModelPath(VoiceRoot));
            var language = _settings.AtcLanguage == AtcLanguage.Spanish ? "es" : "en";
            var result = await _stt.TranscribeAsync(samples, language, null, CancellationToken.None);
            LastTranscript = result.Text;
            MicStatus = result.Text.Length > 0
                ? Tr.F("Entendí: «{0}» (en {1:0.0} s; idioma del ATC: {2})", result.Text, result.Seconds, (language == "es" ? Tr.T("español") : Tr.T("inglés")))
                : Tr.T("No entendí nada: sube la ganancia o acércate al micrófono.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or DllNotFoundException or BadImageFormatException
                                       or NAudio.MmException or Whisper.net.WhisperModelLoadException or Whisper.net.WhisperProcessingException)
        {
            MicStatus = Tr.F("La prueba falló: {0}", ex.Message);
        }
        finally
        {
            _testingMic = false;
            UpdateMicTimer();
        }
    }
}
