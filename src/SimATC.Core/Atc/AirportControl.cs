using SimATC.Core.Engine;

namespace SimATC.Core.Atc;

/// <summary>
/// El control del aeropuerto (ADR-021): Ground y Tower deciden y autorizan; la seguridad en tierra
/// (<see cref="GroundCoordinator"/>) protege por debajo. Orden de cada tick: se limpian las esperas, Ground autoriza
/// pushbacks, la seguridad fija los límites y detecta bloqueos, Ground los resuelve y Tower maneja la pista.
/// </summary>
public sealed class AirportControl
{
    public AirportControl()
    {
        Safety = new GroundCoordinator();
        Ground = new GroundControl(Safety);
        Tower = new TowerControl();
    }

    public GroundCoordinator Safety { get; }
    public GroundControl Ground { get; }
    public TowerControl Tower { get; }

    public Action<string>? Log
    {
        get => Safety.Log;
        set
        {
            Safety.Log = value;
            Tower.Log = value;
        }
    }

    public void Update(IReadOnlyList<DepartureAgent> agents, GroundObstacle? user, double dt)
    {
        for (var i = 0; i < agents.Count; i++)
        {
            agents[i].Hold = HoldKind.None;
            agents[i].HoldFor = 0;
        }
        Ground.Update(agents, user);
        Safety.Update(agents, user, dt);
        Ground.ResolveDeadlocks(agents);
        Tower.Update(agents, user, dt);
    }

    public void Remove(DepartureAgent a)
    {
        Safety.Remove(a);
        Tower.Remove(a);
    }

    public void Clear()
    {
        Safety.Clear();
        Tower.Clear();
    }
}
