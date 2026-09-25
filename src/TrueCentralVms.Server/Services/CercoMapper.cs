using System.Text.Json.Nodes;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>Conversión de entidades de cerco a DTOs (nunca expone el PSK).</summary>
public static class CercoMapper
{
    public static CercoPanelDto ToDto(CercoPanel p, bool connected) => new(
        p.Id, p.Name, p.DeviceId, p.Site, p.Enabled, p.Status, connected,
        p.Armed, p.Siren, p.HvOk, p.FenceOk, p.ArcFault, p.Voltage, p.Rssi,
        p.Model, p.Firmware, p.Mac, p.LastSeenAt, p.LastStateAt,
        p.Zones.OrderBy(z => z.Number)
               .Select(z => new CercoZoneDto(z.Number, z.Name, z.Enabled, z.InAlarm))
               .ToList(),
        p.Arming, p.KeyOn, p.RfLearning, p.Zone0Adc, p.ConfigSynced,
        p.PowerSource, p.PowerDropPermille, p.ReturnUs);

    public static CercoEventDto ToDto(CercoEvent e) => new(
        e.Id, e.CercoPanelId, e.PanelName, e.Timestamp, e.ReceivedAt, e.Kind,
        e.Severity, e.Description, e.ZoneNumber, e.ZoneName, e.Verified);

    public static CercoRemoteDto ToDto(CercoRemote r) =>
        new(r.Slot, r.Name, r.Code, r.Bits, r.Action, r.CreatedAt);

    public static CercoConfigDto ConfigOf(CercoPanel p) =>
        new(p.HvLevel, p.SirenSeconds, p.ExitDelaySeconds, p.Chirp, p.KeyMode, p.Zone0Mode, p.Zone0BlocksArm);

    /// <summary>Error de validación, o null si la configuración es válida (mismos rangos que el firmware).</summary>
    public static string? Validate(CercoConfigDto c) =>
        c.HvLevel is < 7 or > 21 ? "El nivel de potencia debe estar entre 7 y 21." :
        c.SirenSeconds is < 10 or > 900 ? "La duración de la sirena debe estar entre 10 y 900 segundos." :
        c.ExitDelaySeconds is < 0 or > 120 ? "El retardo de salida debe estar entre 0 y 120 segundos." :
        !Enum.IsDefined(c.KeyMode) ? "Modo de llave inválido." :
        !Enum.IsDefined(c.Zone0Mode) ? "Modo de zona inválido." : null;

    /// <summary>Args del comando firmado "config" (nombres y valores = firmware).</summary>
    public static JsonObject ConfigArgs(CercoPanel p) => new()
    {
        ["level"] = p.HvLevel,
        ["siren_s"] = p.SirenSeconds,
        ["exit_s"] = p.ExitDelaySeconds,
        ["chirp"] = p.Chirp,
        ["key_mode"] = (int)p.KeyMode,
        ["z0_mode"] = (int)p.Zone0Mode,
        ["z0_block"] = p.Zone0BlocksArm,
    };

    /// <summary>¿La configuración que reporta el panel ("cfg" del estado) es la deseada?</summary>
    public static bool Matches(CercoPanel p, JsonObject cfg) =>
        (int?)cfg["level"] == p.HvLevel &&
        (int?)cfg["siren_s"] == p.SirenSeconds &&
        (int?)cfg["exit_s"] == p.ExitDelaySeconds &&
        (bool?)cfg["chirp"] == p.Chirp &&
        (int?)cfg["key_mode"] == (int)p.KeyMode &&
        (int?)cfg["z0_mode"] == (int)p.Zone0Mode &&
        (bool?)cfg["z0_block"] == p.Zone0BlocksArm;

    public static (CercoEventKind Kind, CercoSeverity Severity) MapEvent(string ev) => ev switch
    {
        "alarm"            => (CercoEventKind.Alarm,          CercoSeverity.Critical),
        "fence_cut"        => (CercoEventKind.FenceCut,       CercoSeverity.Critical),
        "hv_fault"         => (CercoEventKind.HvFault,        CercoSeverity.Critical),
        "tamper"           => (CercoEventKind.Tamper,         CercoSeverity.Critical),
        "panic"            => (CercoEventKind.Panic,          CercoSeverity.Critical),
        "arc"              => (CercoEventKind.Arc,            CercoSeverity.Warning),
        "armed"            => (CercoEventKind.Armed,          CercoSeverity.Info),
        "disarmed"         => (CercoEventKind.Disarmed,       CercoSeverity.Info),
        "siren_on"         => (CercoEventKind.SirenOn,        CercoSeverity.Warning),
        "siren_off"        => (CercoEventKind.SirenOff,       CercoSeverity.Info),
        "rf_remote"        => (CercoEventKind.RfRemote,       CercoSeverity.Info),
        "arm_failed"       => (CercoEventKind.ArmFailed,      CercoSeverity.Warning),
        "zone_restore"     => (CercoEventKind.ZoneRestore,    CercoSeverity.Info),
        "rf_learned"       => (CercoEventKind.RfLearned,      CercoSeverity.Info),
        "rf_learn_timeout" => (CercoEventKind.RfLearnTimeout, CercoSeverity.Info),
        "power_lost"       => (CercoEventKind.PowerLost,      CercoSeverity.Warning),
        "power_restored"   => (CercoEventKind.PowerRestored,  CercoSeverity.Info),
        _                  => (CercoEventKind.Boot,           CercoSeverity.Info),
    };

    // "detail" de armed/disarmed = origen (firmware ArmSource).
    private static string Source(long detail) => detail switch { 1 => " (llave)", 2 => " (control RF)", _ => "" };

    public static string ActionName(CercoRfAction a) => a switch
    {
        CercoRfAction.Arm => "Armar",
        CercoRfAction.Disarm => "Desarmar",
        CercoRfAction.Toggle => "Armar/desarmar",
        CercoRfAction.Panic => "Pánico",
        CercoRfAction.Silence => "Silenciar",
        _ => "—",
    };

    public static string Describe(CercoEventKind kind, int? zone, long detail = 0) => kind switch
    {
        CercoEventKind.Alarm          => zone is int z ? $"Alarma en zona {z}" : "Alarma",
        CercoEventKind.ZoneRestore    => zone is int zr ? $"Zona {zr} normal" : "Zona normal",
        CercoEventKind.FenceCut       => "Caída/corte del cerco",
        CercoEventKind.HvFault        => "Falla de alto voltaje",
        CercoEventKind.Tamper         => "Sabotaje del gabinete",
        CercoEventKind.Panic          => "Pánico desde control remoto",
        CercoEventKind.Arc            => "Detección de arcos",
        CercoEventKind.Armed          => "Cerco armado" + Source(detail),
        CercoEventKind.Disarmed       => "Cerco desarmado" + Source(detail),
        CercoEventKind.SirenOn        => "Sirena activada",
        CercoEventKind.SirenOff       => "Sirena silenciada",
        CercoEventKind.RfRemote       => "Sirena silenciada desde control remoto",
        CercoEventKind.ArmFailed      => detail == 1 ? "Armado rechazado: zona 0 abierta" : "Armado rechazado: cerco sin retorno",
        CercoEventKind.RfLearned      => $"Control programado: {ActionName((CercoRfAction)detail)}",
        CercoEventKind.RfLearnTimeout => detail == 1 ? "Programación de control: memoria llena" : "Programación de control sin respuesta",
        CercoEventKind.PowerLost      => detail > 0 ? $"Corte de energía: operando con batería (pulso -{detail / 10.0:0.0} %)" : "Corte de energía: operando con batería",
        CercoEventKind.PowerRestored  => "Energía restablecida",
        _                             => "Arranque del panel",
    };
}
