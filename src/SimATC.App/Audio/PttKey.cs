using System.Runtime.InteropServices;
using System.Text;

namespace SimATC.App.Audio;

/// <summary>
/// La tecla para hablar (etapa 5c, ADR-126). Se consulta el estado de la tecla con <c>GetAsyncKeyState</c> de Windows
/// (user32), que funciona aunque la ventana activa sea el simulador y no instala ganchos de teclado. La tecla se guarda
/// como código de tecla virtual. En otros sistemas, nunca está apretada. El botón del joystick queda pendiente (V-16).
/// </summary>
public static class PttKey
{
    /// <summary>¿Está apretada ahora la tecla <paramref name="virtualKey"/>?</summary>
    public static bool IsDown(int virtualKey)
    {
        if (virtualKey <= 0 || !OperatingSystem.IsWindows())
            return false;
        try
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// La primera tecla apretada ahora, o 0: para elegir la tecla ("presiona la tecla que quieras usar"). No cuenta los
    /// botones del mouse ni Shift/Ctrl/Alt genéricos (sí sus variantes izquierda y derecha).
    /// </summary>
    public static int FirstPressed()
    {
        for (var vk = 0x08; vk <= 0xFE; vk++)
        {
            if (vk is 0x10 or 0x11 or 0x12)
                continue;
            if (IsDown(vk))
                return vk;
        }
        return 0;
    }

    /// <summary>El nombre de la tecla como lo llama Windows en el idioma del teclado ("Ctrl derecha", "Bloq Despl", "F13").</summary>
    public static string Name(int virtualKey)
    {
        if (virtualKey <= 0)
            return Core.Text.Tr.T("sin tecla");
        if (!OperatingSystem.IsWindows())
            return Core.Text.Tr.F("tecla {0}", virtualKey);
        try
        {
            var scan = MapVirtualKeyW((uint)virtualKey, 0);
            if (scan != 0)
            {
                var lParam = (int)(scan << 16);
                // Teclas "extendidas" (bit 24): Ctrl y Alt derechos, flechas, Insert, Supr, Inicio, Fin, Re/Av Pág, / del teclado numérico.
                if (virtualKey is 0xA3 or 0xA5 or >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x6F or 0x90)
                    lParam |= 1 << 24;
                var sb = new StringBuilder(64);
                if (GetKeyNameTextW(lParam, sb, sb.Capacity) > 0)
                    return sb.ToString();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Sin user32: el código.
        }
        return $"tecla {virtualKey}";
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameTextW(int lParam, StringBuilder lpString, int cchSize);
}
