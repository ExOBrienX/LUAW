using SimATC.Core.Airports;
using SimATC.Core.Atc;
using SimATC.Core.Flight;
using SimATC.Core.Geo;
using SimATC.Core.Traffic;

namespace SimATC.Core.Tests.Traffic;

/// <summary>
/// ADR-089, prueba de Marco SCEL→SCIE (2026-11-06): SCIE tiene 4 puertas de 18 m y el destino se activa en vuelo, sin viento
/// del aeropuerto. Pista provisional, puertas donde cabe un A320, puesto remoto de respaldo y rutas sin medias vueltas.
/// </summary>
public class SmallAirportTests
{
    private static AirportData Load(string icao) =>
        SimAirports.Load(icao + ".json");

    private static readonly Lazy<AirportData> Scie = new(() => Load("SCIE"));

    [Fact]
    public void Without_wind_the_destination_gets_a_provisional_runway_at_once()
    {
        var planned = new ActiveRunwaySelector(Scie.Value);
        Assert.Null(planned.Current);
        Assert.True(planned.SetProvisional("20"));
        Assert.Equal("20", planned.Current!.Ends[0]);
        Assert.Contains("provisional", planned.Current.Reason);
        Assert.False(planned.SetProvisional("02")); // una sola vez: después manda el viento real

        // Sin plan (o con una pista que no existe): la principal.
        var none = new ActiveRunwaySelector(Scie.Value);
        Assert.True(none.SetProvisional("09"));
        Assert.NotNull(none.Current);
    }

    [Fact]
    public void An_a320_fits_the_18_m_gates_and_falls_back_to_a_remote_stand()
    {
        var gates = Enumerable.Range(0, Scie.Value.Parkings.Count).Where(i => ParkedFleetPlanner.Gate(Scie.Value.Parkings[i])).ToList();
        Assert.Equal(4, gates.Count); // Gate 11 a 14 (datos del simulador)
        // Con las puertas libres, una puerta (antes: ninguna, por pedir 1 m de más).
        var stand = ParkedFleetPlanner.PilotStand(Scie.Value, "LAN", 36, _ => false, new Random(1));
        Assert.Contains(stand, gates);
        // Con las 4 ocupadas, un puesto remoto de plataforma (no una de aviación general chica).
        var remote = ParkedFleetPlanner.PilotStand(Scie.Value, "LAN", 36, i => gates.Contains(i), new Random(1));
        Assert.True(remote >= 0, "sin puesto remoto");
        Assert.True(ParkedFleetPlanner.RemoteStand(Scie.Value.Parkings[remote]));
    }

    [Fact]
    public void Few_stands_keep_room_for_the_next_hour_of_arrivals()
    {
        // 4 puertas en SCIE (sus plataformas no son de aerolínea): sin reserva se estacionaban 2; con 2 llegadas por hora
        // y el piloto, 1.
        Assert.Equal(2, ParkedFleetPlanner.FillTarget(Scie.Value, 20));
        Assert.Equal(1, ParkedFleetPlanner.FillTarget(Scie.Value, 20, keepFree: 3));
        Assert.Equal(0, ParkedFleetPlanner.FillTarget(Scie.Value, 20, keepFree: 9));
    }

    [Fact]
    public void A_route_that_overshoots_and_comes_back_drops_that_fix()
    {
        // TEST DATA: de SCEL hacia el sur, un fijo que pasa de largo el destino y vuelve (como NIA en SCEL-SCTC).
        var origin = new GeoPoint(-33.39, -70.79);
        var dest = new GeoPoint(-38.77, -72.64);
        var e = new FlightEnds("SCEL", origin, 1500, null, null, "SCTC", dest, 300, null, null);
        List<RouteFix> fixes =
        [
            new("AAAAA", "SC", new GeoPoint(-35.0, -71.5), "UQ803"),
            new("BBBBB", "SC", new GeoPoint(-37.0, -72.2), "UQ803"),
            new("CCCCC", "SC", new GeoPoint(-39.6, -73.0), "UT115"),
            new("DDDDD", "SC", new GeoPoint(-38.3, -72.4), "UQ802"),
        ];
        var clean = FlightPlanBuilder.WithoutReversals(fixes, e);
        Assert.DoesNotContain(clean, f => f.Ident == "CCCCC");
        Assert.Equal(["AAAAA", "BBBBB", "DDDDD"], clean.Select(f => f.Ident));
        Assert.Equal("", clean[^1].Airway); // de ahí, directo
    }
}
