using System.Collections.Concurrent;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Receptor ÚNICO de mensajes de alarma de HCNetSDK. El SDK admite un solo
/// callback por proceso (<c>NET_DVR_SetDVRMessageCallBack_V30</c>): instalarlo
/// dos veces deja al segundo módulo sordo. Aquí se instala una vez y se
/// enruta cada mensaje a quien abrió la sesión (por el <c>lUserID</c> que el
/// SDK entrega en <c>NET_DVR_ALARMER</c>): patentes (<see cref="HikvisionAnpr"/>)
/// y eventos de analítica (<see cref="HikvisionEvents"/>).
/// </summary>
internal static class HikvisionAlarmChannel
{
    private static readonly Lock Sync = new();

    /// <summary>El delegado vive en un campo estático: si el GC lo recolecta,
    /// el SDK llama a memoria liberada y el proceso cae.</summary>
    private static CHCNetSDK.MSGCallBack? _callback;

    /// <summary>Manejador por sesión de login (lUserID): (comando, datos, largo).</summary>
    private static readonly ConcurrentDictionary<int, Action<int, IntPtr, uint>> Routes = new();

    public static void EnsureInstalled()
    {
        lock (Sync)
        {
            if (_callback is not null) return;
            _callback = OnSdkMessage;
            if (!CHCNetSDK.NET_DVR_SetDVRMessageCallBack_V30(_callback, IntPtr.Zero))
            {
                _callback = null;
                uint code = CHCNetSDK.NET_DVR_GetLastError();
                throw new DriverException(
                    $"No se pudo instalar el receptor de eventos del SDK Hikvision (error {code}).");
            }
        }
    }

    public static void Register(int userId, Action<int, IntPtr, uint> handler) => Routes[userId] = handler;

    public static void Unregister(int userId) => Routes.TryRemove(userId, out _);

    /// <summary>Hilo del SDK: enrutar y salir rápido. Una excepción aquí viaja por la pila del SDK y jamás debe escapar.</summary>
    private static void OnSdkMessage(int command, ref CHCNetSDK.NET_DVR_ALARMER alarmer, IntPtr info, uint length, IntPtr user)
    {
        try
        {
            if (alarmer.byUserIDValid == 0 || !Routes.TryGetValue(alarmer.lUserID, out var handler)) return;
            if (info == IntPtr.Zero || length == 0) return;
            handler(command, info, length);
        }
        catch
        {
            // nunca hacia el SDK
        }
    }
}
