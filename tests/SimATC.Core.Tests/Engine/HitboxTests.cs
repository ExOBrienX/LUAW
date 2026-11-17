using SimATC.Core.Engine;
using SimATC.Core.Geo;

namespace SimATC.Core.Tests.Engine;

/// <summary>
/// La caja de cada avión en tierra (ADR-134, vuelo KMIA-KRSW de Marco): un IA que cede el paso se detiene antes de que su
/// caja (largo × envergadura) toque la zona que barre el piloto, con margen, y no en la unión delante de él.
/// </summary>
public class HitboxTests
{
    // TEST DATA: una calle recta hacia el este, 300 m.
    private static readonly Polyline East = new([new LocalPoint(0, 0), new LocalPoint(300, 0)]);

    private const double AiSpan = 34;

    [Fact]
    public void An_ai_stops_before_a_pilot_crossing_ahead_with_room_for_both_wings()
    {
        // El piloto viene del sur hacia el norte por una calle que cruza en x = 150, a 60 m de la unión y a 12 kt.
        var pilot = new GroundObstacle(default, 0, 36, SpeedKt: 12);
        var stop = GroundCoordinator.HitboxStop(East, 0, AiSpan, new LocalPoint(150, -60), pilot);
        Assert.NotNull(stop);
        // La nariz del IA (medio largo por delante del centro) queda fuera de la ala del piloto, con el margen.
        var nose = stop!.Value + GroundCoordinator.LengthFor(AiSpan) / 2;
        Assert.True(nose <= 150 - 36 / 2.0 - GroundCoordinator.HitboxMarginM + GroundCoordinator.HitboxStepM,
            $"la nariz queda en x = {nose:0}");
        // Y no se detiene exageradamente lejos.
        Assert.True(nose >= 150 - 36 / 2.0 - GroundCoordinator.HitboxMarginM - 3 * GroundCoordinator.HitboxStepM, $"la nariz queda en x = {nose:0}");
    }

    [Fact]
    public void A_parked_pilot_well_to_the_side_does_not_stop_the_ai()
    {
        // Quieto, mirando al norte, a 60 m al costado de la calle.
        var pilot = new GroundObstacle(default, 0, 36);
        Assert.Null(GroundCoordinator.HitboxStop(East, 0, AiSpan, new LocalPoint(150, -60), pilot));
    }

    [Fact]
    public void A_parked_pilot_whose_wing_reaches_the_taxiway_stops_the_ai_before_it()
    {
        // Quieto a 30 m del eje: su ala (18 m) y la del IA (17 m) no caben con el margen.
        var pilot = new GroundObstacle(default, 0, 36);
        var stop = GroundCoordinator.HitboxStop(East, 0, AiSpan, new LocalPoint(150, -30), pilot);
        Assert.NotNull(stop);
        Assert.True(stop < 150);
    }

    [Fact]
    public void A_pilot_following_the_ai_does_not_freeze_it()
    {
        // El piloto viene detrás, por la misma calle y en el mismo sentido: la zona que barre alcanza al IA, pero el IA sigue.
        var pilot = new GroundObstacle(default, 90, 36, SpeedKt: 15);
        Assert.Null(GroundCoordinator.HitboxStop(East, 60, AiSpan, new LocalPoint(10, 0), pilot));
    }
}
