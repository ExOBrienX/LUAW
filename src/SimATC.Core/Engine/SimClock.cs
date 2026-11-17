namespace SimATC.Core.Engine;

/// <summary>
/// Reloj del motor (§5.2): convierte el tiempo real de cada cuadro en ticks fijos de simulación, respetando la
/// pausa y la velocidad de simulación del simulador (V-06). Sin asignaciones.
/// </summary>
public sealed class SimClock
{
    /// <summary>Duración de un tick de simulación (10 Hz).</summary>
    public const double TickSeconds = 0.1;

    /// <summary>Tope de ticks por cuadro, para no "ponerse al día" de golpe tras un tirón largo.</summary>
    public const int MaxTicksPerFrame = 50;

    private double _accumulator;

    /// <summary>Tiempo de simulación transcurrido, en segundos.</summary>
    public double SimTime { get; private set; }

    /// <summary>Fracción [0, 1) entre el último tick y el siguiente, para interpolar lo visual.</summary>
    public double Alpha => _accumulator / TickSeconds;

    /// <summary>
    /// Avanza con el tiempo real de un cuadro y devuelve cuántos ticks hay que ejecutar.
    /// </summary>
    /// <param name="realSeconds">Tiempo real desde el cuadro anterior.</param>
    /// <param name="simRate">Velocidad de simulación (1 = normal, 2 = x2…).</param>
    /// <param name="paused">Pausa del simulador: no avanza nada.</param>
    public int Advance(double realSeconds, double simRate, bool paused)
    {
        if (paused || realSeconds <= 0 || simRate <= 0)
            return 0;
        _accumulator += realSeconds * simRate;
        var ticks = (int)(_accumulator / TickSeconds);
        if (ticks > MaxTicksPerFrame)
        {
            ticks = MaxTicksPerFrame;
            _accumulator = 0;
        }
        else
        {
            _accumulator -= ticks * TickSeconds;
        }
        SimTime += ticks * TickSeconds;
        return ticks;
    }
}
