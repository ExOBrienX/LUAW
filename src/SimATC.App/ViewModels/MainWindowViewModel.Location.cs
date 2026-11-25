using SimATC.Core.Airports;

using SimATC.Core.Text;

namespace SimATC.App.ViewModels;

/// <summary>
/// Aeropuerto automático (ADR-030): al conectar y cada 30 s en tierra, LUAW mira en qué aeropuerto está el piloto
/// (lista del simulador) y, si cambió, carga ese: pista, ATIS, datos ATC, radar y (si hay rutas) tráfico.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Cada cuánto se revisa, en tierra, si el piloto está en otro aeropuerto.</summary>
    public const int RelocateEveryS = 30;

    private int _relocateTick;

    /// <summary>ICAO del aeropuerto donde está el piloto, o null (en vuelo, lejos, o sin datos todavía).</summary>
    /// <summary>Aeropuertos cargados que resultaron sin pistas: no se vuelven a elegir por cercanía en esta sesión.</summary>
    private readonly HashSet<string> _noRunways = [];

    private async Task<string?> LocateAirportAsync()
    {
        // El estado del piloto llega a ~5 Hz después de conectar: se espera hasta 5 s.
        for (var i = 0; i < 20 && UserState is null; i++)
            await Task.Delay(250);
        if (UserState is not { } user || !_bridge.IsConnected)
            return null;
        try
        {
            // Dentro del aeropuerto cargado no se cambia, aunque la referencia de un vecino quede más cerca.
            if (Airport is { } loaded && (user.OnGround || user.AltitudeAglFt <= AirportLocator.MaxAglFt)
                && AirportLocator.Inside(user.Position, loaded))
                return loaded.Icao;
            var all = await _bridge.RequestNearbyAirportsAsync(CancellationToken.None);
            // Sin los que ya se vio que no tienen pistas (parques, helipuertos: RJ3KM junto a RJTT).
            var nearby = all.Where(a => !_noRunways.Contains(a.Icao)).ToList();
            // En tierra: el origen o destino del plan si estás en él (su referencia puede quedar más lejos que la de un
            // vecino chico en aeropuertos enormes como RJTT).
            // El plan cargado o, si todavía no se responde la pregunta de retomar, el del vuelo guardado (al conectar en vuelo
            // se cargaba RJNQT, un puerto pesquero junto a la ruta, en vez de RJBB: prueba de Marco 2026-11-09).
            var flightPlan = Plan ?? _pendingResume?.Plan ?? ResumeOffer?.Plan;
            if (flightPlan is { } plan && (user.OnGround || user.AltitudeAglFt <= AirportLocator.MaxAglFt))
                foreach (var end in new[] { plan.Origin, plan.Destination })
                    if (end.Position is { } pos && Core.Geo.GeoMath.DistanceM(user.Position, pos) < AirportLocator.MaxDistanceM)
                        return end.Icao;
            if (AirportLocator.WhereIs(user.Position, user.OnGround, user.AltitudeAglFt, nearby)?.Icao is { } here)
                return here;
            // En vuelo o fuera de un aeropuerto: el del plan si está cerca; si no, el más cercano (cualquier parte del mundo).
            if (user.OnGround || Airport is not null)
                return null;
            var planned = new List<(string, Core.Geo.GeoPoint)>();
            if (flightPlan is { } p)
            {
                if (p.Origin.Position is { } o)
                    planned.Add((p.Origin.Icao, o));
                if (p.Destination.Position is { } d)
                    planned.Add((p.Destination.Icao, d));
            }
            return AirportLocator.Fallback(user.Position, planned, nearby);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Log(Tr.F("No se pudo pedir la lista de aeropuertos cercanos ({0}).", ex.Message));
            return null;
        }
    }

    /// <summary>Del reloj de 1 s: en tierra y lento, cada 30 s revisa si el piloto está en otro aeropuerto.</summary>
    private void RelocateTick()
    {
        if (++_relocateTick < RelocateEveryS)
            return;
        _relocateTick = 0;
        if (!_bridge.IsConnected || _loading || _busy)
            return;
        // En vuelo hacia el destino: a menos de 150 NM pasa a ser el aeropuerto activo (ADR-076), para que al llegar ya
        // tenga estacionados, rodajes, salidas y llegadas.
        if (UserState is { OnGround: false } air && _session is { Phase: >= Core.Comms.PilotPhase.WithApproach and < Core.Comms.PilotPhase.Landed }
            && _arrivalAirport is { } dest && _arrivalGraph is not null && Airport?.Icao != dest.Icao
            && Core.Geo.GeoMath.DistanceM(air.Position, dest.Reference) < DestinationActiveNm * Core.Units.UnitConversions.MetersPerNauticalMile
            // Vuelo corto: no antes de alejarse del origen, para que nadie vea desaparecer sus aviones de tierra (ADR-085).
            // Solo si el activo es el origen: otro aeropuerto (uno chico junto a la ruta) no detiene el cambio.
            && (Airport is not { } origin || Plan is not { } fp || !string.Equals(origin.Icao, fp.Origin.Icao, StringComparison.OrdinalIgnoreCase)
                || Core.Geo.GeoMath.DistanceM(air.Position, origin.Reference) > Core.Engine.TrafficEngine.RemoveDistanceM))
        {
            _ = SwitchToDestinationAsync(dest.Icao);
            return;
        }
        if (UserState is not { OnGround: true, GroundSpeedKt: < 40 })
            return;
        // Con un vuelo en curso (despegó y aún no llega al puesto) no se cambia de aeropuerto: se perdería la sesión del ATC
        // de la llegada (prueba de Marco 2026-11-05: al frenar en SCIE se borró y no hubo rodaje ni puesto). ADR-075.
        if (_session is { Phase: >= Core.Comms.PilotPhase.TakeoffCleared and < Core.Comms.PilotPhase.Arrived })
            return;
        _ = RelocateAsync();
    }

    /// <summary>Selecciona tu avión en el radar y lo sigue (botón "Seguirme").</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void FollowMe()
    {
        if (UserState is null)
            return;
        SelectedId = Radar.RadarView.UserId;
        FollowSelected = true;
        InspectorOpen = false; // seguir sin tapar el radar; la ficha se abre con clic en tu avión
    }

    /// <summary>Inspector de tu avión: lo que ve el ATC de ti (en vivo, 5 Hz).</summary>
    private void UpdateUserInspector()
    {
        if (UserState is not { } u)
        {
            SelectedId = null;
            return;
        }
        var magVar = Weather?.MagVarDeg ?? 0;
        InspectorPhase = u.OnGround ? Tr.T("en tierra") : Tr.T("en vuelo");
        InspectorAltitude = u.OnGround ? Tr.T("en tierra") : u.AltitudeText(TransitionAltitudeFt);
        InspectorSpeed = u.OnGround ? $"{u.GroundSpeedKt:0} kt" : $"{u.IndicatedAirspeedKt:0} kt";
        InspectorWait = null;
        InspectorWaitId = null;
        var rows = new List<InspectorRow>
        {
            new(Tr.T("Altitud"), Tr.F("{0} · {1:+0;-0;0} ft/min · {2:N0} ft AGL", u.AltitudeText(TransitionAltitudeFt), u.VerticalSpeedFpm, u.AltitudeAglFt)),
            new(Tr.T("Velocidad"), Tr.F("{0:0} kt indicada · {1:0} kt sobre el suelo", u.IndicatedAirspeedKt, u.GroundSpeedKt)),
            new(Tr.T("Rumbo"), Tr.F("{0:000}° magnético ({1:000}° verdadero)", Core.Geo.GeoMath.NormalizeDeg(u.HeadingTrueDeg - magVar), u.HeadingTrueDeg)),
            new(Tr.T("Radio"), Tr.F("COM1 {0:0.000} · squawk {1}", u.Com1ActiveKhz / 1000.0, (u.TransponderCode >= 0 ? u.TransponderCode.ToString("0000") : "—"))),
        };
        if (_userLoad is { } load)
        {
            rows.Add(new(Tr.T("Peso"), Tr.F("{0:N0} kg de combustible · {1:N0} kg total", load.FuelLbs * Core.Plan.BriefingCheck.KgPerLb, load.TotalWeightLbs * Core.Plan.BriefingCheck.KgPerLb)));
            if (load.AtcId.Length > 0)
                rows.Add(new(Tr.T("Matrícula"), load.AtcId));
        }
        if (Airport is { } a)
            rows.Add(new(Tr.T("Distancia"), Tr.F("{0:0.0} NM de {1}", Core.Geo.GeoMath.DistanceM(u.Position, a.Reference) / Core.Units.UnitConversions.MetersPerNauticalMile, a.Icao)));
        InspectorTitle = Plan is { } p ? Tr.F("Tu avión · {0}", p.Callsign) : Tr.T("Tu avión");
        InspectorSubtitle = _userLoad is { Title.Length: > 0 } l ? l.Title : "";
        InspectorRows = rows;
        SelectedRoute = null;
    }

    /// <summary>Puesto reservado para el piloto en su destino (índice en el aeropuerto activo), o null.</summary>
    private int? _pilotStand;
    private string? _pilotStandIcao;

    /// <summary>
    /// ADR-085: al activar el destino en vuelo se reserva el puesto del piloto antes de estacionar a los IA (prueba de Marco
    /// en SCIE: Ground le dio el Gate 11, ocupado por un estacionado).
    /// </summary>
    private void ReservePilotStand(Core.Airports.AirportData airport)
    {
        _pilotStand = null;
        var stand = Core.Traffic.ParkedFleetPlanner.PilotStand(airport, Plan?.AirlineIcao, Core.Engine.TrafficEngine.UserWingspanM,
            PilotStandBusy, Random.Shared);
        if (stand < 0)
            return;
        _pilotStand = stand;
        _pilotStandIcao = airport.Icao;
        _director?.ReserveStand(stand, Plan?.Callsign ?? "PILOTO");
        Log(Tr.F("{0}: puesto reservado para ti: {1}.", airport.Icao, airport.Parkings[stand].DisplayName));
    }

    /// <summary>¿Ocupado (estacionado, salida en curso o reservado para una llegada IA) en el aeropuerto activo?</summary>
    private bool PilotStandBusy(int stand)
    {
        if (_graph is { } g && Airport is { } here && _arrivalAirport?.Icao == here.Icao && !g.StandReachable(stand))
            return true; // sin camino a las pistas (ADR-093)
        if (_arrivalAirport is { } dest && Airport?.Icao != dest.Icao)
            return false; // el destino todavía no está activo: no se sabe
        foreach (var p in _parked.Snapshot())
        {
            if (p.Aircraft.ParkingIndex == stand)
                return true;
            // Tapado por un fuselaje ancho en la puerta vecina (ADR-167).
            if (p.Aircraft.WideHalfSpanM > 0 && Airport is { } a && stand < a.Parkings.Count
                && Core.Geo.GeoMath.DistanceM(p.Aircraft.Parking.Position, a.Parkings[stand].Position)
                    < p.Aircraft.WideHalfSpanM + a.Parkings[stand].RadiusM + Core.Traffic.WideStands.WingMarginM)
                return true;
        }
        if (Airport is { } near && stand < near.Parkings.Count && _director?.BlockedByWide(near, stand) == true)
            return true;
        foreach (var f in _flights.Values)
        {
            if (f.Aircraft.ParkingIndex == stand)
                return true;
        }
        return stand != _pilotStand && _director?.ReservedStands.Contains(stand) == true;
    }

    /// <summary>A esta distancia del destino (NM) pasa a ser el aeropuerto activo en vuelo (ADR-076). APROXIMADO.</summary>
    public const double DestinationActiveNm = 150;

    /// <summary>
    /// En vuelo: el destino pasa a ser el aeropuerto activo (ADR-076). Se libera solo lo de tierra del anterior (lejos del
    /// piloto: nadie lo ve desaparecer); los vuelos en el aire y la sesión del ATC siguen. Después se llena el destino.
    /// </summary>
    private async Task SwitchToDestinationAsync(string icao)
    {
        if (_loading)
            return;
        _loading = true;
        try
        {
            Log(Tr.F("Vuelo hacia {0}: pasa a ser el aeropuerto activo; se libera lo de tierra de {1}.", icao, Airport?.Icao ?? "—"));
            foreach (var a in _engine?.Snapshot() ?? [])
            {
                if (!a.Airborne && !a.Virtual)
                    _engine!.RequestRemove(a.ObjectId);
            }
            _parked.Clear(_bridge);
            _flights.Clear();
            _director?.ResetAirport();
            // La Torre olvida la pista de tu despegue: en el destino otra pista puede tener el mismo índice (prueba de Marco
            // 2026-11-06: un IA esperó para siempre "separación con la salida anterior" en la 20 de SCIE).
            _engine?.Control.Tower.SetPilot(-1, Core.Atc.PilotTowerClearance.None);
            _routes = null;
            _icao = icao;
            OnPropertyChanged(nameof(AirportText));
            Stage(Tr.F("Preparando {0} (llegas en vuelo)…", icao), 20);
            var (airport, fromCache) = await _airports.GetFreshAsync(icao, CancellationToken.None);
            SetAirport(airport, fromCache);
            Change(_settings with { LastAirport = icao });
            _routes ??= Core.Traffic.RouteList.Load(Core.Traffic.RouteList.PathFor(_dataDir, icao));
            ReservePilotStand(airport);
            await StartWorldTrafficAsync();
            TrafficStatus = Tr.F("{0}: aeropuerto activo (llegas en vuelo).", icao);
            Stage($"{icao} listo", 100);
        }
        catch (Exception ex)
        {
            Log(Tr.F("No se pudo activar {0} en vuelo ({1}); se sigue con {2}.", icao, ex.Message, Airport?.Icao ?? "—"));
            Stage("", 100);
        }
        finally
        {
            _loading = false;
            NotifyCommands();
        }
    }

    private async Task RelocateAsync()
    {
        var here = await LocateAirportAsync();
        if (here is not null && here != Airport?.Icao)
            await LoadAsync();
    }
}
