using SimATC.Core.Airports;
using SimATC.Core.Fleet;
using SimATC.Core.Geo;
using SimATC.Core.Simulation;

using SimATC.Core.Text;

namespace SimATC.Core.Traffic;

/// <summary>
/// Un avión estacionado que existe en el simulador. <paramref name="IsParkedAtc"/>: creado como Parked ATC (V-15), con
/// puesto reservado y pasarela; para salir hay que hacer el relevo (<see cref="ISimBridge.HandOverAsync"/>).
/// <paramref name="ParkedAt"/>/<paramref name="ParkedHeadingDeg"/>: dónde lo dejó el simulador (en la línea de parada,
/// no siempre en el centro del puesto); el relevo y el pushback parten de ahí.
/// </summary>
public sealed record InjectedParked(uint ObjectId, ParkedAircraft Aircraft, bool IsParkedAtc = false,
    GeoPoint? ParkedAt = null, double? ParkedHeadingDeg = null)
{
    public GeoPoint Position => ParkedAt ?? Aircraft.Parking.Position;
    public double HeadingDeg => ParkedHeadingDeg ?? Aircraft.Parking.HeadingTrueDeg;
}

/// <summary>
/// Aviones estacionados de un aeropuerto (etapa 2): poblar con N aviones y limpiar (RF-44).
/// Poblar reemplaza lo que había, para que cada prueba parta de cero y sea reproducible.
/// </summary>
public sealed class ParkedTraffic
{
    private readonly Lock _lock = new();
    private List<InjectedParked> _items = [];

    public IReadOnlyList<InjectedParked> Snapshot()
    {
        lock (_lock)
            return _items.ToList();
    }

    /// <returns>Cuántos aviones quedaron creados.</returns>
    public async Task<int> PopulateAsync(ISimBridge bridge, AirportData airport, RouteList routes, ModelMatcher matcher,
        TrafficLimits limits, int count, GeoPoint? user, Action<string> log, CancellationToken ct,
        IReadOnlySet<int>? busyStands = null, int movingCount = 0, int? seed = null)
    {
        // Solo se reemplazan los estacionados: los aviones que ya están saliendo siguen (y sus puestos no se usan).
        RemoveParked(bridge);
        var wanted = Math.Min(count, limits.GroundRoom(onGround: movingCount, inAir: 0));
        // Semilla nueva cada vez (aviones, puestos y liveries cambian); fija en tests y escenarios para reproducirlos.
        var plan = ParkedFleetPlanner.Plan(airport, routes, matcher, matcher.TypesFor, wanted, user, seed ?? Random.Shared.Next(),
            busyStands is null ? null : Overlapping(airport, busyStands));
        if (plan.Count < wanted)
            log(Tr.F("Solo hay puesto para {0} de {1} aviones.", plan.Count, wanted));
        return await SpawnPlanAsync(bridge, airport, plan, user, log, ct, busyStands).ConfigureAwait(false);
    }

    /// <summary>
    /// Llena el aeropuerto según el nivel de tráfico (ADR-109): cada tipo de puesto con su ocupación, aerolíneas al azar,
    /// carga en las plataformas de carga, remotos "de noche" y aviación general de decoración. Reemplaza los estacionados.
    /// </summary>
    /// <returns>Cuántos aviones quedaron creados.</returns>
    public async Task<int> PopulateLevelAsync(ISimBridge bridge, AirportData airport, RouteList routes, ModelMatcher matcher,
        TrafficPreset preset, int keepFree, GeoPoint? user, Action<string> log, CancellationToken ct,
        IReadOnlySet<int>? busyStands = null, IReadOnlyList<GaDecorModel>? ga = null, int? seed = null,
        Action<int, int, ParkedAircraft>? progress = null)
    {
        RemoveParked(bridge);
        var plan = ParkedFleetPlanner.PlanForLevel(airport, routes, matcher, matcher.TypesFor, preset, keepFree, user, seed ?? Random.Shared.Next(),
            busyStands is null ? null : Overlapping(airport, busyStands), ga);
        var (airlineTarget, gaTarget) = StandCensus.Targets(airport, preset, keepFree);
        log(Tr.F("{0}: nivel de tráfico → {1} de aerolínea (pedidos {2}), ", airport.Icao, plan.Count(p => !p.Decor || p.Airline.Length > 0), airlineTarget)
            + $"{plan.Count(p => p.Airline.Length == 0)} de aviación general (pedidos {gaTarget}), {keepFree} puestos libres para llegadas.");
        return await SpawnPlanAsync(bridge, airport, plan, user, log, ct, busyStands, progress).ConfigureAwait(false);
    }

    /// <param name="progress">Antes de crear cada avión: cuál va (1..n), de cuántos y qué es (para la ventana de carga).</param>
    private async Task<int> SpawnPlanAsync(ISimBridge bridge, AirportData airport, List<ParkedAircraft> plan, GeoPoint? user,
        Action<string> log, CancellationToken ct, IReadOnlySet<int>? busyStands, Action<int, int, ParkedAircraft>? progress = null)
    {
        var index = 0;
        var created = new List<InjectedParked>();
        // Por geometría: los puestos superpuestos en los datos (el mismo lugar como mediano y pesado) cuentan juntos.
        var usedStands = busyStands is null ? new HashSet<int>() : Overlapping(airport, busyStands);
        foreach (var p in plan)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(++index, plan.Count, p);
            try
            {
                // 1) Parked ATC: el simulador elige el puesto (según envergadura y códigos de la aerolínea) y conecta la
                //    pasarela. Se acepta si quedó en un puesto conocido, libre y lejos del piloto.
                //    La decoración (aviación general, remotos "de noche") no usa ATC: nunca sale.
                var item = p.Decor ? null : await TryParkedAtcAsync(bridge, airport, p, usedStands, user, log, ct).ConfigureAwait(false);
                // 2) Si no: sin ATC en el puesto que eligió el planificador (si sigue libre).
                if (item is null && !usedStands.Contains(p.ParkingIndex))
                {
                    var request = new ParkedSpawnRequest(p.Match.Model.Title, p.TailNumber, p.Parking.Position,
                        airport.ElevationFt, p.Parking.HeadingTrueDeg, p.Match.Model.EngineCount);
                    item = new InjectedParked(await bridge.SpawnParkedAsync(request, ct).ConfigureAwait(false), p);
                }
                if (item is null)
                {
                    log(Tr.F("{0}: sin puesto libre.", p.TailNumber));
                    continue;
                }
                usedStands.UnionWith(Overlapping(airport, [item.Aircraft.ParkingIndex]));
                created.Add(item);
                lock (_lock)
                    _items = [.. created];
                var a = item.Aircraft;
                log(Tr.F("{0}: {1} {2} en {3}{4}{5} → {6} [{7}]", a.TailNumber, a.Airline, a.Type, a.Parking.DisplayName, (item.IsParkedAtc ? Tr.T(" (Parked ATC, pasarela)") : ""), (a.Decor ? Tr.T(" (decoración)") : ""), a.Match.Model.Title, a.Match.Kind));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un avión que falla no detiene a los demás (RNF-06).
                log(Tr.F("{0}: no se pudo crear ({1}).", p.TailNumber, ex.Message));
            }
        }
        return created.Count;
    }

    /// <summary>Los puestos dados y todos los que se tocan con alguno de ellos (círculos de puesto superpuestos).</summary>
    public static HashSet<int> Overlapping(AirportData airport, IEnumerable<int> stands)
    {
        var result = new HashSet<int>();
        foreach (var s in stands)
        {
            if (s < 0 || s >= airport.Parkings.Count)
                continue;
            var p = airport.Parkings[s];
            for (var i = 0; i < airport.Parkings.Count; i++)
            {
                var q = airport.Parkings[i];
                if (i == s || GeoMath.DistanceM(p.Position, q.Position) < p.RadiusM + q.RadiusM - 2)
                    result.Add(i);
            }
        }
        return result;
    }

    /// <summary>Distancia máxima entre donde quedó el Parked ATC y el centro de un puesto para reconocerlo (m).</summary>
    public const double ParkedAtcStandToleranceM = 12;

    private static async Task<InjectedParked?> TryParkedAtcAsync(ISimBridge bridge, AirportData airport, ParkedAircraft p,
        HashSet<int> usedStands, GeoPoint? user, Action<string> log, CancellationToken ct)
    {
        ParkedAtcResult result;
        try
        {
            result = await bridge.SpawnParkedAtcAsync(
                new ParkedAtcRequest(p.Match.Model.Title, p.TailNumber, airport.Icao, p.Match.Model.EngineCount), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log(Tr.F("{0}: Parked ATC no disponible ({1}); se crea sin ATC.", p.TailNumber, ex.Message));
            return null;
        }

        var stand = -1;
        var best = ParkedAtcStandToleranceM;
        for (var i = 0; i < airport.Parkings.Count; i++)
        {
            var d = GeoMath.DistanceM(airport.Parkings[i].Position, result.Position);
            if (d < best)
            {
                best = d;
                stand = i;
            }
        }
        var halfSpan = p.Match.Model.WingspanM / 2;
        var reason = stand < 0 ? Tr.T("fuera de un puesto conocido")
            : usedStands.Contains(stand) ? Tr.T("en un puesto ya ocupado")
            : user is { } u && GeoMath.DistanceM(airport.Parkings[stand].Position, u) < ParkedFleetPlanner.UserClearanceM + halfSpan ? Tr.T("junto al piloto")
            : null;
        if (reason is not null)
        {
            bridge.Remove(result.ObjectId);
            log(Tr.F("{0}: el simulador lo dejó {1}; se crea sin ATC.", p.TailNumber, reason));
            return null;
        }
        return new InjectedParked(result.ObjectId, p with { ParkingIndex = stand, Parking = airport.Parkings[stand] }, IsParkedAtc: true,
            result.Position, result.HeadingTrueDeg);
    }

    /// <summary>Saca un avión de la lista de estacionados (pasa a moverse con el motor). null si no estaba.</summary>
    public InjectedParked? Release(uint objectId)
    {
        lock (_lock)
        {
            var item = _items.Find(i => i.ObjectId == objectId);
            if (item is not null)
                _items = _items.Where(i => i.ObjectId != objectId).ToList();
            return item;
        }
    }

    /// <summary>Una llegada que quedó apagada en su puesto pasa a los estacionados (etapa 7; puede volver a salir).</summary>
    public void Adopt(InjectedParked item)
    {
        lock (_lock)
            _items = [.. _items.Where(i => i.ObjectId != item.ObjectId), item];
    }

    /// <summary>Borra solo los estacionados (no los que están saliendo).</summary>
    public void RemoveParked(ISimBridge bridge)
    {
        List<InjectedParked> items;
        lock (_lock)
        {
            items = _items;
            _items = [];
        }
        foreach (var item in items)
            bridge.Remove(item.ObjectId);
    }

    /// <summary>Borra todos los aviones creados (RF-44).</summary>
    public void Clear(ISimBridge bridge)
    {
        bridge.RemoveAll();
        lock (_lock)
            _items = [];
    }

    /// <summary>Al perder la conexión el simulador ya los borró (V-12).</summary>
    public void Forget()
    {
        lock (_lock)
            _items = [];
    }
}
