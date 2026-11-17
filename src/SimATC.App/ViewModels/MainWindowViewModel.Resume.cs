using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Comms;
using SimATC.Core.Geo;
using SimATC.Core.Taxi;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Retomar el vuelo si LUAW se cerró de forma inesperada (prueba de Marco 2026-11-09): mientras hay un vuelo en curso se
/// guarda en <c>flight-resume.json</c>; al cerrar normalmente o al terminar el vuelo se borra. Si al abrir sigue ahí, se
/// ofrece retomarlo: se carga su plan y, apenas hay sesión del ATC y posición del avión, queda en la fase donde iba.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Se guarda también cada tanto (s), aunque la fase no cambie (nivel autorizado, dependencia).</summary>
    private const double ResumeSaveEveryS = 30;

    private FlightSnapshot? _pendingResume;
    private string? _resumeKey;
    private DateTime _resumeSavedUtc;
    private bool _resumeLoading;

    /// <summary>La pista de llegada del vuelo retomado (se prefiere a la del plan mientras no haya viento del destino).</summary>
    private string? _resumeRunway;

    /// <summary>Cuántos mensajes de la conversación se guardan para mostrarlos al retomar.</summary>
    private const int ResumeChatMessages = 60;

    /// <summary>Vuelo que quedó a medias en la apertura anterior (la ventana pregunta si retomarlo), o null.</summary>
    public FlightSnapshot? ResumeOffer { get; private set; } = FlightResume.Load(FlightResume.DefaultPath, DateTime.UtcNow);

    /// <summary>"LAN878 RJTT → SCEL (rodando)", para la pregunta de la ventana.</summary>
    public string ResumeText => ResumeOffer is { } s
        ? $"{s.Plan.Callsign} {s.Plan.Origin.Icao} → {s.Plan.Destination.Icao} ({PhaseText(s.Phase)})"
        : "";

    /// <summary>El piloto eligió retomar: se carga el plan y la fase se aplica cuando haya sesión y posición.</summary>
    public void AcceptResume()
    {
        if (ResumeOffer is not { } s)
            return;
        ResumeOffer = null;
        _pendingResume = s;
        Log(Tr.F("Retomar vuelo {0}: fase guardada {1}.", s.Plan.Callsign, s.Phase));
        SetPlan(s.Plan);
    }

    /// <summary>El piloto eligió empezar de nuevo: se olvida el vuelo guardado.</summary>
    public void DeclineResume()
    {
        ResumeOffer = null;
        FlightResume.Delete(FlightResume.DefaultPath);
    }

    /// <summary>Cierre normal de LUAW: no hay nada que retomar en la apertura siguiente.</summary>
    public void ForgetResume()
    {
        if (ResumeOffer is null)
            FlightResume.Delete(FlightResume.DefaultPath);
    }

    /// <summary>Del reloj de 1 s: aplica el vuelo pendiente de retomar y guarda el que está en curso.</summary>
    private void ResumeTick()
    {
        if (_pendingResume is { } pending)
        {
            ApplyPendingResume(pending);
            return; // hasta retomarlo no se guarda nada (se pisaría el archivo con un vuelo vacío)
        }
        if (ResumeOffer is not null || _session is not { } s)
            return;
        if (s.Phase == PilotPhase.Arrived)
        {
            if (_resumeKey is not null)
                FlightResume.Delete(FlightResume.DefaultPath);
            _resumeKey = null;
            return;
        }
        if (s.Phase == PilotPhase.Parked && s.Clearance is null)
            return;
        var key = $"{s.Phase}|{s.ExpectedStation?.Khz}|{s.Clearance?.Squawk}|{s.PushedBack}|{s.LandingCleared}";
        var now = DateTime.UtcNow;
        if (key == _resumeKey && (now - _resumeSavedUtc).TotalSeconds < ResumeSaveEveryS)
            return;
        try
        {
            FlightResume.Save(FlightResume.DefaultPath, s.Snapshot(now) with { Chat = ChatToSave() });
            _resumeKey = key;
            _resumeSavedUtc = now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(Tr.F("No se pudo guardar el vuelo para retomarlo ({0}).", ex.Message));
            _resumeSavedUtc = now;
        }
    }

    private void ApplyPendingResume(FlightSnapshot pending)
    {
        if (UserState is not { } u || Plan is null)
            return;
        // En vuelo lejos del origen (el aeropuerto activo es otro): la sesión se arma con los datos del origen.
        if (_session is null)
        {
            if ((!u.OnGround || pending.Phase > PilotPhase.TakeoffCleared) && !_resumeLoading)
                _ = OriginSessionAsync(Plan);
            return;
        }
        _pendingResume = null;
        // La pista que se usaba (ADR-105: al retomar volvía a la del plan, 06L en vez de 06R en RJBB).
        if (pending.ArrivalRunway is { } rw)
        {
            _resumeRunway = rw;
            _arrival = null;
        }
        // La conversación de antes, para tener a la vista lo que dijo el ATC (no se usa para deducir nada).
        foreach (var m in pending.Chat)
            Comms.Add(new CommsMessage(m.Utc, m.From, Enum.TryParse<Speaker>(m.Kind, out var kind) ? kind : Speaker.System, m.Text, m.Avatar));
        if (pending.Chat.Count > 0)
            Comms.Add(new CommsMessage(DateTime.UtcNow, "LUAW", Speaker.System, Tr.T("— Conversación hasta el cierre de LUAW —")));
        var atStand = Airport is { } ap && string.Equals(ap.Icao, Plan.Origin.Icao, StringComparison.OrdinalIgnoreCase)
                      && ap.Parkings.Any(p => GeoMath.DistanceM(p.Position, u.Position) < PushbackStandRadiusM);
        var station = _session.Resume(pending, atStand, _atcClock.Elapsed.TotalSeconds);
        Comms.Add(new CommsMessage(DateTime.UtcNow, "LUAW", Speaker.System, station is null
            ? Tr.T("No se pudo retomar el vuelo donde quedó: empieza de nuevo con Delivery.")
            : _session.AwaitingReadback
                ? Tr.F("Vuelo retomado ({0}). Colaciona la última instrucción de {1} {2}: \"{3}\".", PhaseText(_session.Phase), station.Callsign, station.FrequencyText, _session.LastInstructionText)
                : Tr.F("Vuelo retomado ({0}). Llama a {1} en {2}.", PhaseText(_session.Phase), station.Callsign, station.FrequencyText)));
        UpdateCommsView();
    }

    /// <summary>La sesión con el aeropuerto de origen cargado aparte (para retomar en vuelo con otro aeropuerto activo).</summary>
    private async Task OriginSessionAsync(Core.Plan.FlightPlan plan)
    {
        _resumeLoading = true;
        try
        {
            var (origin, _) = await _airports.GetAsync(plan.Origin.Icao, false, CancellationToken.None);
            if (_session is not null || _phrases is not { } phrases || Plan != plan)
                return;
            var atc = AirportAtcData.Load(AirportAtcData.PathFor(_dataDir, origin.Icao), Log);
            var stations = new AtcStations(origin, atc, _settings.AtcLanguage);
            _session = NewSession(plan, origin, atc, stations, TaxiGraph.Build(origin), phrases);
            Log(Tr.F("Retomar vuelo: sesión del ATC con los datos de {0} (aeropuerto activo {1}).", origin.Icao, Airport?.Icao ?? "—"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
            Log(Tr.F("Retomar vuelo: no se pudo cargar {0} ({1}).", plan.Origin.Icao, ex.Message));
            _pendingResume = null;
        }
        finally
        {
            _resumeLoading = false;
        }
    }

    /// <summary>Los últimos mensajes de la conversación (de todas las radios), para mostrarlos al retomar.</summary>
    private IReadOnlyList<SavedMessage> ChatToSave()
    {
        var all = Comms.Channels.SelectMany(c => c.History)
            .Where(m => m.Speaker != Speaker.OtherAircraft)
            .OrderBy(m => m.TimeUtc)
            .Select(m => new SavedMessage(m.TimeUtc, m.Who, m.Text, m.Speaker.ToString(), m.Avatar))
            .ToList();
        return all.Count > ResumeChatMessages ? all[^ResumeChatMessages..] : all;
    }

    private static string PhaseText(PilotPhase phase) => phase switch
    {
        PilotPhase.Parked or PilotPhase.ClearanceGiven => Tr.T("en el puesto, pidiendo la autorización"),
        PilotPhase.Cleared => Tr.T("autorizado, en el puesto"),
        PilotPhase.PushbackApproved => Tr.T("con el pushback aprobado"),
        PilotPhase.TaxiGiven or PilotPhase.Taxiing => Tr.T("rodando"),
        <= PilotPhase.TakeoffCleared => Tr.T("en el punto de espera, con la Torre"),
        PilotPhase.Airborne or PilotPhase.WithApproach => Tr.T("en la salida"),
        PilotPhase.EnRoute => Tr.T("en ruta"),
        PilotPhase.Arrival => Tr.T("en la llegada"),
        PilotPhase.ArrivalTower => Tr.T("en final, con la Torre"),
        PilotPhase.Landed or PilotPhase.TaxiIn => Tr.T("aterrizado, rodando al puesto"),
        _ => Tr.T("en el puesto de llegada"),
    };
}
