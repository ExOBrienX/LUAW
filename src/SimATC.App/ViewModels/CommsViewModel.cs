using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>Quién habló, para el color del mensaje en el historial (RF-34).</summary>
public enum Speaker
{
    Atc,
    Pilot,
    OtherAircraft,
    System,
}

/// <summary>
/// Un mensaje del historial de la frecuencia (RF-34): hora, quién y qué dijo. <see cref="Confirmed"/> marca con ✓ una
/// colación del piloto que el ATC dio por buena (Ground no la confirma por radio, así que la interfaz lo muestra).
/// </summary>
public sealed partial class CommsMessage(DateTime timeUtc, string who, Speaker speaker, string text, string? avatar = null) : ObservableObject
{
    public DateTime TimeUtc { get; } = timeUtc;
    public string Who { get; } = who;
    public Speaker Speaker { get; } = speaker;
    public string Text { get; } = text;

    /// <summary>Lo que va en el ícono redondo: la dependencia (DEL, GND, TWR, APP…), "TÚ" o "i" para avisos de LUAW.</summary>
    public string Avatar { get; } = avatar ?? speaker switch
    {
        Speaker.Pilot => Tr.T("TÚ"),
        Speaker.System => "i",
        _ => "?",
    };

    /// <summary>Color del ícono: uno por dependencia, para reconocerla de un vistazo.</summary>
    public IBrush AvatarBrush => AvatarBrushes.TryGetValue(Avatar, out var b) ? b
        : Speaker == Speaker.OtherAircraft ? AircraftBrushes[(int)((uint)StableHash(Avatar) % AircraftBrushes.Length)]
        : AvatarBrushes["?"];

    /// <summary>Colores para los aviones IA: uno por aerolínea, siempre el mismo (hash estable de sus letras).</summary>
    private static readonly IBrush[] AircraftBrushes =
    [
        new SolidColorBrush(Color.Parse("#8E5BB5")), new SolidColorBrush(Color.Parse("#B5486E")), new SolidColorBrush(Color.Parse("#4E8F8A")),
        new SolidColorBrush(Color.Parse("#8A7A3C")), new SolidColorBrush(Color.Parse("#5A6FB5")), new SolidColorBrush(Color.Parse("#A35F3D")),
    ];

    private static int StableHash(string s)
    {
        var h = 17;
        foreach (var c in s)
            h = h * 31 + c;
        return h;
    }

    private static readonly Dictionary<string, IBrush> AvatarBrushes = new()
    {
        ["DEL"] = new SolidColorBrush(Color.Parse("#7C5CD6")),
        ["GND"] = new SolidColorBrush(Color.Parse("#C98A2B")),
        ["TWR"] = new SolidColorBrush(Color.Parse("#2F8F5B")),
        ["APP"] = new SolidColorBrush(Color.Parse("#2E7BC4")),
        ["DEP"] = new SolidColorBrush(Color.Parse("#2E7BC4")),
        ["CTR"] = new SolidColorBrush(Color.Parse("#3A5F8F")),
        ["ATIS"] = new SolidColorBrush(Color.Parse("#5B6B7D")),
        ["TIE"] = new SolidColorBrush(Color.Parse("#B5651D")),
        ["TÚ"] = new SolidColorBrush(Color.Parse("#1F7A4A")),
        ["CP"] = new SolidColorBrush(Color.Parse("#2E8F6A")), // el copiloto (hito 4f), de tu lado
        ["i"] = new SolidColorBrush(Color.Parse("#4A5566")),
        ["?"] = new SolidColorBrush(Color.Parse("#4A5566")),
    };

    [ObservableProperty]
    public partial bool Confirmed { get; set; }

    /// <summary>El ATC le habla al piloto (se resalta en el chat y suena un aviso).</summary>
    public bool ForMe { get; init; }

    /// <summary>Barra "mensajes nuevos" encima de este mensaje: el primero que llegó mientras el piloto leía más arriba.</summary>
    [ObservableProperty]
    public partial bool NewDivider { get; set; }

    /// <summary>La barra ya se vio: queda tenue hasta la próxima.</summary>
    [ObservableProperty]
    public partial bool DividerSeen { get; set; }

    public string Time => TimeUtc.ToString("HH:mm:ss");
    public bool IsAtc => Speaker == Speaker.Atc;
    public bool IsPilot => Speaker == Speaker.Pilot;
    public bool IsOther => Speaker is Speaker.OtherAircraft or Speaker.System;
    /// <summary>Aviso de LUAW (cambio de frecuencia, pushback terminado…): va centrado, sin globo.</summary>
    public bool IsSystem => Speaker == Speaker.System;
    /// <summary>ATC, ATIS y otros aviones: globo a la izquierda.</summary>
    public bool IsLeft => !IsPilot && !IsSystem;
}

/// <summary>Una frase sugerida (RF-30b): el texto y su forma hablada (§8.2).</summary>
public sealed record Suggestion(string Text, string Spoken);

/// <summary>Un grupo del menú "Qué puedo pedir" ("Siempre", "En esta fase", "Si algo no avanza").</summary>
public sealed record RequestGroup(string Name, IReadOnlyList<SimATC.Core.Comms.PilotRequest> Items);

/// <summary>Sugerencia para mostrar, con si es la elegida (para resaltarla).</summary>
public sealed record SuggestionItem(Suggestion Suggestion, bool IsSelected)
{
    public string Text => Suggestion.Text;
    public string Spoken => Suggestion.Spoken;
    public bool HasSpoken => Spoken.Length > 0 && Spoken != Text;
}

/// <summary>
/// Una frecuencia que se puede sintonizar con un clic: "Santiago Ground 122.2". <see cref="Radio"/>: a qué radio va
/// (null: la de Configuración).
/// </summary>
public sealed record TuneTarget(string Name, int Khz, string FrequencyText, int? Radio = null)
{
    public string Label => Radio is { } r ? $"{Name} {FrequencyText} (COM{r})" : $"{Name} {FrequencyText}";
}

/// <summary>Lo autorizado por Delivery, para la ficha "Tu autorización" (siempre a la vista, sin buscar en el chat).</summary>
public sealed record ClearanceCard(string Runway, string? Sid, string? Level, int Squawk, TuneTarget? Next)
{
    public string SquawkText => Squawk.ToString("0000");
    public string Route => string.Join(" · ", new[] { Tr.F("Pista {0}", Runway), Sid is null ? null : $"SID {Sid}", Level is null ? null : Tr.F("Nivel {0}", Level) }
        .Where(x => x is not null));

    /// <summary>Todo en una línea, para el panel compacto: "17R · ALBA7C · FL210 · 2042".</summary>
    public string Line => string.Join(" · ", new[] { Runway, Sid, Level?.Replace("flight level ", "FL"), SquawkText }.Where(x => x is not null));
}

/// <summary>
/// Panel de comunicaciones (RF-40, RF-30b, RF-30c): frecuencia actual, ficha de la autorización, historial, "qué decir
/// ahora" con sugerencias y el campo de texto con texto fantasma. TAB completa la sugerencia elegida, ↑/↓ cambian de
/// sugerencia, Enter envía y Esc borra. Lo que se envía lo procesa el ATC (hito 4c) a través de <see cref="Sent"/>; los
/// botones de sintonizar y de squawk llegan al simulador por <see cref="TuneRequested"/> y <see cref="SquawkRequested"/>.
/// </summary>
public sealed partial class CommsViewModel : ObservableObject
{
    public CommsViewModel()
    {
        Channels = [new ChannelViewModel(1) { IsShown = true, IsSelected = true }, new ChannelViewModel(2), new ChannelViewModel(3)];
        foreach (var c in Channels)
            c.TuneRequested += t => TuneRequested?.Invoke(t);
        UpdateShown();
    }

    /// <summary>Un chat por radio (COM1 a COM3).</summary>
    public IReadOnlyList<ChannelViewModel> Channels { get; }

    /// <summary>Los chats que se ven: el de transmitir y los que eligió el piloto (en columnas o en pestañas).</summary>
    public ObservableCollection<ChannelViewModel> ShownChannels { get; } = [];

    /// <summary>El chat de la pestaña elegida (panel angosto).</summary>
    [ObservableProperty]
    public partial ChannelViewModel? SelectedChannel { get; set; }

    /// <summary>La radio por la que transmite el piloto (sus mensajes y las respuestas de su ATC van a ese chat).</summary>
    public ChannelViewModel TransmitChannel => Channels.FirstOrDefault(c => c.Transmitting) ?? Channels[0];

    public bool HasManyShown => ShownChannels.Count > 1;

    /// <summary>Las radios que tiene el avión (para los botones de ver cada chat).</summary>
    public IEnumerable<ChannelViewModel> AvailableChannels => Channels.Where(c => c.Available || c.Radio == 1);

    public bool HasRadios => Channels.Count(c => c.Available) > 1;

    /// <summary>"Se envía por COM1 · 136.7 · Santiago Delivery".</summary>
    public string SendVia => Tr.F("Se envía por {0}", TransmitChannel.Header);

    /// <summary>Muestra u oculta el chat de una radio (el de transmitir no se oculta).</summary>
    [RelayCommand]
    private void ToggleChannel(ChannelViewModel channel)
    {
        if (channel.Transmitting && channel.IsShown)
        {
            SelectChannel(channel);
            return;
        }
        channel.IsShown = !channel.IsShown;
        UpdateShown();
        if (channel.IsShown)
            SelectChannel(channel);
    }

    /// <summary>Elige la pestaña (panel angosto).</summary>
    [RelayCommand]
    private void SelectChannel(ChannelViewModel channel)
    {
        foreach (var c in Channels)
            c.IsSelected = ReferenceEquals(c, channel);
        SelectedChannel = channel;
    }

    /// <summary>Después de cambiar qué radio transmite o qué existe: el de transmitir siempre se ve.</summary>
    public void UpdateShown()
    {
        foreach (var c in Channels)
        {
            if (c.Transmitting)
                c.IsShown = true;
            if (!c.Available && c.Radio != 1)
                c.IsShown = c.IsShown && c.Transmitting;
        }
        var shown = Channels.Where(c => c.IsShown).ToList();
        if (!shown.SequenceEqual(ShownChannels))
        {
            ShownChannels.Clear();
            foreach (var c in shown)
                ShownChannels.Add(c);
        }
        if (SelectedChannel is null || !SelectedChannel.IsShown)
            SelectChannel(TransmitChannel);
        OnPropertyChanged(nameof(HasManyShown));
        OnPropertyChanged(nameof(AvailableChannels));
        OnPropertyChanged(nameof(HasRadios));
        OnPropertyChanged(nameof(TransmitChannel));
        OnPropertyChanged(nameof(SendVia));
    }

    [ObservableProperty]
    public partial string StationName { get; set; } = Tr.T("Sin estación");

    [ObservableProperty]
    public partial string Frequency { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestions), nameof(SuggestionItems), nameof(Current), nameof(Ghost))]
    public partial IReadOnlyList<Suggestion> Suggestions { get; private set; } = [];

    [ObservableProperty]
    public partial int SelectedSuggestion { get; set; }

    [ObservableProperty]
    public partial string InputText { get; set; } = "";

    /// <summary>Qué le toca hacer al piloto, en español (null: nada que mostrar).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStep))]
    public partial string? StepText { get; set; }

    /// <summary>La dependencia con la que hay que hablar, si COM1 no está en su frecuencia (botón "Sintonizar").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStepTune))]
    public partial TuneTarget? StepTune { get; set; }

    /// <summary>El ATIS, para escucharlo antes de pedir la autorización (botón aparte).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAtisTune))]
    public partial TuneTarget? AtisTune { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClearance), nameof(SquawkSet), nameof(SquawkPending), nameof(TransponderText))]
    public partial ClearanceCard? Clearance { get; set; }

    /// <summary>El código que tiene puesto el transponder del avión (null: no se sabe).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SquawkSet), nameof(SquawkPending), nameof(TransponderText))]
    public partial int? TransponderCode { get; set; }

    /// <summary>
    /// El botón "Sintonizar" de la frecuencia de después del despegue: solo cuando corresponde (en el aire o cuando la
    /// Torre pase al piloto). Antes es solo un dato, para no cambiar de frecuencia mientras se rueda.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowNextTune { get; set; }

    /// <summary>"Santiago Delivery transmitiendo…" mientras el ATC está por hablar (null: nadie).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransmitting))]
    public partial string? Transmitting { get; set; }

    /// <summary>Se envió un mensaje del piloto (para el ATC del hito 4c).</summary>
    public event Action<string>? Sent;

    /// <summary>El piloto pidió sintonizar una frecuencia (en la radio que diga el destino o la de Configuración).</summary>
    public event Action<TuneTarget>? TuneRequested;



    /// <summary>El piloto pidió poner un squawk en el transponder.</summary>
    public event Action<int>? SquawkRequested;

    /// <summary>El piloto quiere planear el pushback (RF-21c).</summary>
    public event Action? PushbackPlanRequested;

    /// <summary>En un puesto, detenido y sin pushback en curso: se ofrece el planificador.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanPushbackLabel))]
    public partial bool CanPlanPushback { get; set; }

    /// <summary>El pushback automático, si está aprobado y se encontró uno: "Cola a la izquierda, terminas mirando…".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoPushback), nameof(PlanPushbackLabel))]
    public partial string? AutoPushbackText { get; set; }

    public bool HasAutoPushback => AutoPushbackText is not null;

    public string PlanPushbackLabel => HasAutoPushback ? Tr.T("Planear manual") : Tr.T("Planear pushback");

    /// <summary>El piloto eligió el pushback automático.</summary>
    public event Action? AutoPushbackRequested;

    [RelayCommand]
    private void PlanPushback() => PushbackPlanRequested?.Invoke();

    [RelayCommand]
    private void AutoPushback() => AutoPushbackRequested?.Invoke();

    public bool HasStep => StepText is not null || HasOrders;

    /// <summary>Instrucciones vigentes del ATC (altitud, QNH, velocidad, aproximación, frustrada, senda; ADR-105).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOrders), nameof(HasStep))]
    public partial IReadOnlyList<SimATC.Core.Comms.PilotOrder> Orders { get; private set; } = [];

    public bool HasOrders => Orders.Count > 0;

    /// <summary>Cambia las instrucciones vigentes solo si cambió algo (se recalculan cada segundo).</summary>
    public void SetOrders(IReadOnlyList<SimATC.Core.Comms.PilotOrder> orders)
    {
        if (!orders.SequenceEqual(Orders))
            Orders = orders;
    }
    public bool HasStepTune => StepTune is not null;
    public bool HasAtisTune => AtisTune is not null;
    public bool HasClearance => Clearance is not null;

    /// <summary>La autorización en la tarjeta del radar: desde que la dan hasta despegar (ADR-115); en el aire ya no hace falta.</summary>
    [ObservableProperty]
    public partial bool ClearanceOnGround { get; set; }

    /// <summary>En el panel del radar la autorización va en una línea; "Detalles" la despliega (prueba de Marco, 2026-10-31).</summary>
    [ObservableProperty]
    public partial bool ClearanceExpanded { get; set; }

    /// <summary>"Qué decir ahora" plegado: solo el paso, sin botones ni sugerencias (el texto fantasma del campo sigue).</summary>
    [ObservableProperty]
    public partial bool StepCollapsed { get; set; }

    /// <summary>El squawk autorizado ya está en el transponder del avión.</summary>
    public bool SquawkSet => Clearance is { } c && TransponderCode == c.Squawk;

    public bool SquawkPending => Clearance is not null && !SquawkSet;

    /// <summary>"✓ en el transponder" o "tu transponder: 7000".</summary>
    public string TransponderText => SquawkSet ? Tr.T("✓ en el transponder")
        : TransponderCode is { } t and >= 0 ? Tr.F("tu transponder: {0:0000}", t) : "";
    public bool IsTransmitting => Transmitting is not null;

    /// <summary>Estado del micrófono para mostrar junto al campo de texto ("Escuchando…", "Transcribiendo…"), o null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMicState))]
    public partial string? MicState { get; set; }

    /// <summary>El micrófono está abierto y grabando ahora (punto rojo).</summary>
    [ObservableProperty]
    public partial bool MicListening { get; set; }

    public bool HasMicState => MicState is not null;

    /// <summary>La sugerencia elegida, o null si no hay.</summary>
    public Suggestion? Current => SelectedSuggestion >= 0 && SelectedSuggestion < Suggestions.Count ? Suggestions[SelectedSuggestion] : null;

    /// <summary>
    /// Texto fantasma (RF-30c): la sugerencia elegida, si lo escrito es su comienzo (sin distinguir mayúsculas). Se dibuja
    /// en gris detrás del campo, así que lo ya escrito queda tapado y se ve solo lo que falta.
    /// </summary>
    public string Ghost =>
        Current is { } s && s.Text.StartsWith(InputText, StringComparison.OrdinalIgnoreCase) ? InputText + s.Text[InputText.Length..] : "";

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>Las sugerencias con la elegida marcada.</summary>
    public IReadOnlyList<SuggestionItem> SuggestionItems =>
        Suggestions.Select((s, i) => new SuggestionItem(s, i == SelectedSuggestion)).ToList();

    /// <summary>
    /// Cambia las sugerencias solo si son otras: así la que eligió el piloto no vuelve a la primera cada vez que la app
    /// revisa el estado. Si la elegida sigue en la lista nueva, se mantiene.
    /// </summary>
    public void SetSuggestions(IReadOnlyList<Suggestion> list)
    {
        if (list.SequenceEqual(Suggestions))
            return;
        var keep = Current;
        Suggestions = list;
        var i = keep is null ? -1 : list.ToList().IndexOf(keep);
        SelectedSuggestion = Math.Max(i, 0);
    }

    partial void OnInputTextChanged(string value) => OnPropertyChanged(nameof(Ghost));

    partial void OnSelectedSuggestionChanged(int value)
    {
        OnPropertyChanged(nameof(SuggestionItems));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Ghost));
    }

    /// <summary>TAB: completa con la sugerencia elegida (o lo que falta de ella). true si completó algo.</summary>
    public bool Complete()
    {
        if (Current is not { } s || !s.Text.StartsWith(InputText, StringComparison.OrdinalIgnoreCase) || InputText.Length == s.Text.Length)
            return false;
        InputText = s.Text;
        return true;
    }

    /// <summary>↑ / ↓: cambia de sugerencia (da la vuelta).</summary>
    public void Cycle(int step)
    {
        if (Suggestions.Count == 0)
            return;
        SelectedSuggestion = ((SelectedSuggestion + step) % Suggestions.Count + Suggestions.Count) % Suggestions.Count;
    }

    [RelayCommand]
    private void Send()
    {
        var text = InputText.Trim();
        if (text.Length == 0)
            return;
        Add(new CommsMessage(DateTime.UtcNow, Tr.T("Tú"), Speaker.Pilot, text));
        InputText = "";
        Sent?.Invoke(text);
    }

    [RelayCommand]
    private void Clear() => InputText = "";

    /// <summary>"Qué puedo pedir" (ADR-132): lo de siempre, lo de esta fase y qué decir si algo no avanza, por grupos.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequestMenu))]
    public partial IReadOnlyList<RequestGroup> RequestMenu { get; private set; } = [];

    public bool HasRequestMenu => RequestMenu.Count > 0;

    /// <summary>El menú desplegado (se recuerda mientras la app está abierta).</summary>
    [ObservableProperty]
    public partial bool RequestMenuOpen { get; set; }

    public void SetRequestMenu(IReadOnlyList<SimATC.Core.Comms.PilotRequest> requests)
    {
        var groups = requests.GroupBy(r => r.Group).Select(g => new RequestGroup(g.Key, [.. g])).ToList();
        if (groups.Count == RequestMenu.Count && groups.Zip(RequestMenu).All(x => x.First.Name == x.Second.Name && x.First.Items.SequenceEqual(x.Second.Items)))
            return;
        RequestMenu = groups;
    }

    /// <summary>Clic en un pedido del menú: queda escrito en el campo, listo para enviar con Enter o corregir.</summary>
    [RelayCommand]
    private void PickRequest(SimATC.Core.Comms.PilotRequest request) => InputText = request.Text;

    /// <summary>Clic en una sugerencia: la elige y la deja escrita en el campo, lista para enviar con Enter.</summary>
    [RelayCommand]
    private void PickSuggestion(SuggestionItem item)
    {
        var i = Suggestions.ToList().IndexOf(item.Suggestion);
        if (i < 0)
            return;
        SelectedSuggestion = i;
        InputText = item.Text;
    }

    [RelayCommand]
    private void Tune(TuneTarget? target)
    {
        if (target is not null)
            TuneRequested?.Invoke(target);
    }

    [RelayCommand]
    private void SetSquawk()
    {
        if (Clearance is { } c)
            SquawkRequested?.Invoke(c.Squawk);
    }

    /// <summary>Marca con ✓ el último mensaje del piloto (colación aceptada).</summary>
    public void ConfirmLastPilotMessage() => TransmitChannel.ConfirmLastPilotMessage();

    /// <summary>Un mensaje del piloto, de su ATC o de LUAW: al chat de la radio por la que transmite.</summary>
    public void Add(CommsMessage message) => TransmitChannel.Add(message);

    /// <summary>Lo dicho en una frecuencia: a los chats de las radios sintonizadas en ella (±2 kHz). false si ninguna.</summary>
    public bool AddHeard(int khz, CommsMessage message)
    {
        var any = false;
        foreach (var c in Channels)
        {
            if (c.Available && c.Khz > 0 && Math.Abs(c.Khz - khz) <= 2)
            {
                c.Add(message);
                any = true;
            }
        }
        return any;
    }
}
