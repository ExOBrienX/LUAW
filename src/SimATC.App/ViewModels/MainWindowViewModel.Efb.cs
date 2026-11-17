using SimATC.App.Efb;
using SimATC.Core.Efb;
using SimATC.Core.Engine;
using SimATC.Core.Traffic;

namespace SimATC.App.ViewModels;

/// <summary>
/// Radar de tráfico en el EFB del simulador (ADR-073): con el tráfico que ya se junta para el radar de la app (5 Hz), cada
/// 0,5 s se arma el mensaje (<see cref="EfbRadarFeed"/>) y se publica en el servidor local. Sin tablet conectada no se arma.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const double EfbIntervalS = 0.5;

    private EfbRadarServer? _efbServer;
    private readonly EfbRadarFeed _efbFeed = new();
    private readonly List<EfbTarget> _efbTargets = [];
    private double _efbLastS = double.NegativeInfinity;
    private object? _efbRouteFrom;
    private List<(SimATC.Core.Geo.GeoPoint Position, string Name)>? _efbRoute;

    private void InitEfb()
    {
        _efbServer = new EfbRadarServer(Log);
        _efbServer.Start();
    }

    private void PublishEfb(IReadOnlyList<InjectedParked> parked, AgentSnapshot[] moving)
    {
        if (_efbServer is not { Running: true, Clients: > 0 } server)
            return;
        var now = _atcClock.Elapsed.TotalSeconds;
        if (now - _efbLastS < EfbIntervalS)
            return;
        _efbLastS = now;
        _efbTargets.Clear();
        foreach (var p in parked)
        {
            // Estacionados: en tierra, a la elevación del aeropuerto (la tablet puede ocultarlos).
            _efbTargets.Add(new EfbTarget(p.ObjectId, p.Aircraft.TailNumber, p.Aircraft.Type, p.Position, p.HeadingDeg,
                Airport?.ElevationFt ?? 0, 0, true, true));
        }
        foreach (var a in moving)
        {
            _efbTargets.Add(new EfbTarget(a.ObjectId, a.Callsign, a.Type, a.Position, a.HeadingDeg,
                a.Airborne ? a.AltitudeFt : (Airport?.ElevationFt ?? 0) + a.AglFt, a.SpeedKt, !a.Airborne, false, a.Origin, a.Destination));
        }
        EfbOwnship? me = UserState is { } u
            ? new EfbOwnship(u.Position, u.HeadingTrueDeg, u.AltitudeMslFt, u.GroundSpeedKt, u.OnGround)
            : null;
        if (!ReferenceEquals(_efbRouteFrom, PlanRoute))
        {
            _efbRouteFrom = PlanRoute;
            _efbRoute = PlanRoute?.Select(pp => (pp.Position, pp.Name)).ToList();
        }
        server.Publish(_efbFeed.Build(me, _efbTargets, now, _efbRoute));
    }
}
