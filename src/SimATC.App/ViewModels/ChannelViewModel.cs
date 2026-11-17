using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Una frecuencia para el menú "Sintonizar" de una radio, marcada si es la recomendada para la fase.</summary>
public sealed record TuneOption(TuneTarget Target, bool Recommended)
{
    public string Label => Recommended ? Tr.F("{0} {1} · recomendada", Target.Name, Target.FrequencyText) : $"{Target.Name} {Target.FrequencyText}";
}

/// <summary>
/// El chat de una radio (COM1 a COM3): lo que se dice en la frecuencia que tiene sintonizada. Se ve aunque el avión no
/// la esté recibiendo, con un aviso (algunos aviones no informan bien la recepción). La radio por la que transmite el
/// piloto siempre se ve: ahí van sus mensajes y las respuestas de su ATC.
/// </summary>
public sealed partial class ChannelViewModel(int radio) : ObservableObject
{
    public const int MaxHistory = 300;

    public int Radio { get; } = radio;
    public string Name => $"COM{Radio}";

    public ObservableCollection<CommsMessage> History { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header), nameof(Warning))]
    public partial bool Available { get; set; }

    [ObservableProperty]
    public partial int Khz { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial string Frequency { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial string Station { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Warning), nameof(State))]
    public partial bool Receiving { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Warning), nameof(State))]
    public partial bool Transmitting { get; set; }

    /// <summary>El piloto eligió ver este chat (el de transmitir se ve siempre).</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; }

    /// <summary>La pestaña elegida en el panel angosto.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Mensajes nuevos sin ver (en el panel angosto, de las pestañas no elegidas).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int Unread { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<TuneOption> TuneOptions { get; set; } = [];

    public bool HasUnread => Unread > 0;

    /// <summary>El piloto eligió una frecuencia en el menú "Sintonizar" de esta radio.</summary>
    public event Action<TuneTarget>? TuneRequested;

    public void RequestTune(TuneTarget target) => TuneRequested?.Invoke(target with { Radio = Radio });
    public bool IsEmpty => History.Count == 0;
    public string Header => Station.Length > 0 ? $"{Name} · {Frequency} · {Station}" : $"{Name} · {Frequency}";
    public string State => Transmitting ? "TX" : Receiving ? "RX" : Tr.T("sin recibir");

    /// <summary>Aviso si el avión no recibe esta radio: el texto se ve igual, pero en el avión no se escucharía.</summary>
    public string Warning => Available && !Receiving && !Transmitting
        ? Tr.T("El avión no está recibiendo esta radio: enciende su recepción en el panel de audio (se ve igual aquí).")
        : "";

    public bool HasWarning => Warning.Length > 0;

    partial void OnReceivingChanged(bool value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnTransmittingChanged(bool value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnAvailableChanged(bool value) => OnPropertyChanged(nameof(HasWarning));

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
            Unread = 0;
    }

    /// <summary>Todo lo de esta radio como texto (para guardarlo en Diagnóstico o al terminar el vuelo), hasta 20 mil líneas.</summary>
    private readonly List<string> _transcript = [];
    public const int MaxTranscript = 20_000;

    public IReadOnlyList<string> Transcript => _transcript;

    public void Add(CommsMessage message)
    {
        if (_transcript.Count >= MaxTranscript)
            _transcript.RemoveRange(0, MaxTranscript / 10);
        _transcript.Add($"{message.TimeUtc.ToLocalTime():HH:mm:ss} [{Frequency}] {message.Who}: {message.Text}");
        History.Add(message);
        while (History.Count > MaxHistory)
            History.RemoveAt(0);
        if (!IsSelected)
            Unread++;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Marca con ✓ el último mensaje del piloto en este chat (colación aceptada).</summary>
    public void ConfirmLastPilotMessage()
    {
        for (var i = History.Count - 1; i >= 0; i--)
        {
            if (History[i].IsPilot)
            {
                History[i].Confirmed = true;
                _transcript.Add(Tr.F("{0:HH:mm:ss} [{1}]   (colación correcta)", DateTime.Now, Frequency));
                return;
            }
        }
    }
}
