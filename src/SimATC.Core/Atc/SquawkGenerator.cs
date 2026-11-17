namespace SimATC.Core.Atc;

/// <summary>
/// Código de transpondedor para una autorización IFR: cuatro dígitos octales (0-7) generados por el ATC, sin los
/// especiales (7500, 7600, 7700, 7000, 2000, 1200, 0000) ni los ya asignados. No hace falta un rango por país: en el
/// simulador basta con que sea válido y único (Marco, 2026-10-30).
/// </summary>
public sealed class SquawkGenerator(Random? random = null)
{
    private static readonly HashSet<int> Reserved = [7500, 7600, 7700, 7000, 2000, 1200, 0];
    private readonly Random _random = random ?? new Random();
    private readonly HashSet<int> _assigned = [];

    public int Next()
    {
        while (true)
        {
            var code = 0;
            for (var i = 0; i < 4; i++)
                code = code * 10 + _random.Next(8);
            // Se evitan también los que terminan en 00 (suelen ser códigos de conspicuidad o de bloque).
            if (!Reserved.Contains(code) && code % 100 != 0 && _assigned.Add(code))
                return code;
        }
    }

    public static bool IsValid(int code) => code is >= 0 and <= 7777 && code.ToString("0000").All(c => c <= '7');
}
