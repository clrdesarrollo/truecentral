using System.Runtime.InteropServices;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Actividad del operador en el PC (teclado o mouse en cualquier ventana), por GetLastInputInfo. Sirve para cortar el video de
/// los vehículos cuando nadie lo está mirando: los clics sobre el video caen en las ventanas de Flyleaf, no en las de WPF.
/// </summary>
public static class UserInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Tiempo desde la última tecla o movimiento del mouse (cero si no se puede leer).</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // Ambos en milisegundos desde el arranque de Windows, en 32 bits (la resta sin signo sobrevive al desborde de 49 días).
        uint elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }
}
