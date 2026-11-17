namespace SimATC.Core.Traffic;

/// <summary>Límites de aviones inyectados (§5.3, configurables con RF-41).</summary>
public sealed record TrafficLimits(int MaxGround = 25, int MaxAir = 15, int MaxTotal = 40)
{
    public static TrafficLimits Default { get; } = new();

    /// <summary>Cuántos aviones más se pueden inyectar en tierra con los que ya hay.</summary>
    public int GroundRoom(int onGround, int inAir) =>
        Math.Max(0, Math.Min(MaxGround - onGround, MaxTotal - onGround - inAir));
}
