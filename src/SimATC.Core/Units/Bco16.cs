namespace SimATC.Core.Units;

/// <summary>
/// Unidad "Bco16" de SimConnect: 4 dígitos octales, uno por nibble (ej. 0x4721 → código 4721).
/// Fuente: SDK MSFS 2024, "Simulation Variable Units".
/// </summary>
public static class Bco16
{
    /// <summary>Convierte un valor Bco16 al código de 4 dígitos. Devuelve false si algún nibble no es octal.</summary>
    public static bool TryDecode(uint value, out int code)
    {
        code = 0;
        if (value > 0xFFFF)
            return false;

        for (var shift = 12; shift >= 0; shift -= 4)
        {
            var digit = (int)((value >> shift) & 0xF);
            if (digit > 7)
            {
                code = 0;
                return false;
            }
            code = code * 10 + digit;
        }
        return true;
    }

    /// <summary>Código de 4 dígitos (4721) → valor Bco16 (0x4721), para el evento XPNDR_SET.</summary>
    public static uint Encode(int code)
    {
        if (code is < 0 or > 7777)
            throw new ArgumentOutOfRangeException(nameof(code), code, "Un squawk tiene 4 dígitos.");
        var value = 0u;
        for (var div = 1000; div > 0; div /= 10)
        {
            var digit = code / div % 10;
            if (digit > 7)
                throw new ArgumentOutOfRangeException(nameof(code), code, "Los dígitos de un squawk van de 0 a 7.");
            value = value << 4 | (uint)digit;
        }
        return value;
    }
}
