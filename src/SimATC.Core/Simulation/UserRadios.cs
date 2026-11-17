namespace SimATC.Core.Simulation;

/// <summary>
/// Una radio COM del avión del piloto: si existe (<c>COM AVAILABLE</c>), su frecuencia activa (<c>COM ACTIVE FREQUENCY</c>,
/// kHz), si se escucha (<c>COM RECEIVE</c>), si es por la que transmite (<c>COM TRANSMIT</c>) y si tiene energía
/// (<c>COM STATUS</c>: 2 = sin electricidad, 3 = falla) y su volumen (<c>COM VOLUME</c>, %; null si el avión no lo
/// informa). SimVars de "Aircraft Radio Navigation Variables" (índices 1 a 3).
/// </summary>
public readonly record struct ComRadio(int Index, bool Available, int ActiveKhz, bool Receive, bool Transmit, bool Powered = true,
    double? VolumePercent = null);

/// <summary>Las radios COM del piloto (COM1 a COM3).</summary>
public sealed record UserRadios(IReadOnlyList<ComRadio> Coms)
{
    /// <summary>La radio por la que transmite el piloto (la marcada en el panel de audio; si ninguna, COM1).</summary>
    public ComRadio? Transmitting =>
        Coms.FirstOrDefault(c => c.Available && c.Powered && c.Transmit && c.ActiveKhz > 0) is { Index: > 0 } tx ? tx
        : Coms.FirstOrDefault(c => c.Index == 1 && c.Powered && c.ActiveKhz > 0) is { Index: > 0 } com1 ? com1 : null;

    /// <summary>Las radios que se escuchan: las que reciben, más la que transmite (siempre se escucha). Sin energía, ninguna.</summary>
    public IEnumerable<ComRadio> Listening =>
        Coms.Where(c => c.Available && c.Powered && c.ActiveKhz > 0 && (c.Receive || c.Index == Transmitting?.Index));

    /// <summary>Alguna radio tiene energía: el avión está encendido (baterías, APU o tierra) y la radio funciona.</summary>
    public bool HasPower => Coms.Any(c => c.Available && c.Powered);

    /// <summary>La radio que escucha esa frecuencia (±2 kHz), o null.</summary>
    public ComRadio? Hearing(int khz) => Listening.FirstOrDefault(c => Math.Abs(c.ActiveKhz - khz) <= 2) is { Index: > 0 } c ? c : null;
}
