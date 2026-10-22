using System.Runtime.InteropServices;

namespace TrueCentralVms.Client.Services;

/// <summary>Lectura de un joystick: ejes normalizados a −1..1 (Y negativo = arriba) y botones apretados.</summary>
public sealed record JoystickState(string Id, string Name, double[] Axes, bool[] Buttons);

/// <summary>
/// Joystick PTZ por la API multimedia de Windows (winmm, joyGetPosEx): sirve
/// cualquiera que Windows reconozca como mando de juego (el del panel web es
/// el mismo, leído por la Gamepad API). Sin dependencias ni complemento. Usa
/// el primero conectado con al menos dos ejes; si se desconecta, lo vuelve a
/// buscar cada 2 s (buscar en las 16 ranuras en cada lectura sería lento).
/// </summary>
public static class Joystick
{
    private const int MaxSlots = 16;
    private const uint JoyReturnAll = 0xFF;
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOYCAPSW
    {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax;
        public uint wNumButtons, wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax;
        public uint wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
        public uint dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
    }

    [DllImport("winmm.dll")]
    private static extern uint joyGetPosEx(uint uJoyID, ref JOYINFOEX pji);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint joyGetDevCapsW(UIntPtr uJoyID, ref JOYCAPSW pjc, uint cbjc);

    private static int _slot = -1;
    private static JOYCAPSW _caps;
    private static DateTime _nextScan = DateTime.MinValue;

    /// <summary>Estado del joystick en uso, o null si no hay ninguno conectado.</summary>
    public static JoystickState? Read()
    {
        if (_slot < 0 && !Scan()) return null;
        var info = NewInfo();
        if (joyGetPosEx((uint)_slot, ref info) != 0)
        {
            _slot = -1; // desconectado: se vuelve a buscar
            return null;
        }
        var c = _caps;
        uint[] raw = [info.dwXpos, info.dwYpos, info.dwZpos, info.dwRpos, info.dwUpos, info.dwVpos];
        (uint Min, uint Max)[] ranges = [(c.wXmin, c.wXmax), (c.wYmin, c.wYmax), (c.wZmin, c.wZmax),
            (c.wRmin, c.wRmax), (c.wUmin, c.wUmax), (c.wVmin, c.wVmax)];
        int axisCount = (int)Math.Clamp(c.wNumAxes, 0, 6);
        var axes = new double[axisCount];
        for (int i = 0; i < axisCount; i++)
        {
            double span = (double)ranges[i].Max - ranges[i].Min;
            axes[i] = span <= 0 ? 0 : Math.Clamp(((double)raw[i] - ranges[i].Min) / span * 2 - 1, -1, 1);
        }
        int buttonCount = (int)Math.Clamp(c.wNumButtons, 0, 32);
        var buttons = new bool[buttonCount];
        for (int i = 0; i < buttonCount; i++) buttons[i] = (info.dwButtons & (1u << i)) != 0;
        return new JoystickState($"VID_{c.wMid:X4}&PID_{c.wPid:X4}",
            $"Joystick USB VID {c.wMid:X4} / PID {c.wPid:X4} ({axisCount} ejes, {buttonCount} botones)", axes, buttons);
    }

    private static bool Scan()
    {
        if (DateTime.UtcNow < _nextScan) return false;
        _nextScan = DateTime.UtcNow + RescanInterval;
        for (uint slot = 0; slot < MaxSlots; slot++)
        {
            var info = NewInfo();
            if (joyGetPosEx(slot, ref info) != 0) continue;
            var caps = new JOYCAPSW();
            if (joyGetDevCapsW((UIntPtr)slot, ref caps, (uint)Marshal.SizeOf<JOYCAPSW>()) != 0 || caps.wNumAxes < 2) continue;
            _slot = (int)slot;
            _caps = caps;
            return true;
        }
        return false;
    }

    private static JOYINFOEX NewInfo() => new() { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JoyReturnAll };
}
