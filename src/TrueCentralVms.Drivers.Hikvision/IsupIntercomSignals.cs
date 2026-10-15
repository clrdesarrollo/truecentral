using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Core.Isup;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Llamadas de un frente de citofonía conectado por ISUP. Sin el enlace SDK (16032) que se usa por IP, el frente informa la
/// señalización de la llamada como un evento por el canal de alarmas de ISUP: el mismo documento CallSignal de ISAPI
/// (<c>cmdType</c> request/cancel/answer/reject/bellTimeout/hangUp/deviceOnCall) que la central le manda en sentido contrario.
/// Se lee con tolerancia (XML o JSON, con o sin envoltorio de evento) y lo que no se reconoce se ignora; el servidor deja en
/// su registro la primera alarma de cada tipo de cada equipo, para ver el formato real de un modelo nuevo.
/// </summary>
internal static class IsupIntercomSignals
{
    public static IntercomCallSignal? ToSignal(IsupAlarm alarm)
    {
        string text = alarm.Text ?? "";
        // Solo eventos de videoportero: un frente también envía eventos de control de acceso (aperturas, tarjetas…).
        if (!text.Contains("CallSignal", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("videoIntercom", StringComparison.OrdinalIgnoreCase))
            return null;
        if (IsupVideoAlarms.Fields.Parse(text) is not { } f) return null;

        IntercomSignalKind? kind = (f["cmdType"] ?? f["callStatus"] ?? f["status"])?.ToLowerInvariant() switch
        {
            "request" or "ring" or "ringing" or "call" or "calling" or "callingcenter" => IntercomSignalKind.Ringing,
            "cancel" or "canceled" or "cancelled" => IntercomSignalKind.Cancelled,
            "answer" or "answered" or "oncall" => IntercomSignalKind.Answered,
            "reject" or "rejected" or "refuse" => IntercomSignalKind.Rejected,
            "belltimeout" or "ringtimeout" or "timeout" => IntercomSignalKind.RingTimeout,
            "hangup" => IntercomSignalKind.HungUp,
            "deviceoncall" or "busy" => IntercomSignalKind.Busy,
            _ => null,
        };
        return kind is { } k ? new IntercomCallSignal(k, OriginOf(f), alarm.ReceivedAt.LocalDateTime) : null;
    }

    /// <summary>Quién llama, si el frente lo informa (comunidad/edificio/torre/piso/depto), como en el enlace SDK.</summary>
    private static string? OriginOf(IsupVideoAlarms.Fields f)
    {
        var parts = new List<string>();
        void Add(string label, string field)
        {
            if (f[field] is { } v && v != "0") parts.Add($"{label} {v}");
        }
        Add("Comunidad", "periodNumber");
        Add("Edificio", "buildingNo");
        Add("Torre", "unitNo");
        Add("Piso", "floorNo");
        Add("Depto", "roomNo");
        if (parts.Count == 0 && f["deviceName"] is { } name) parts.Add(name);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
