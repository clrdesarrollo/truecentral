using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Adam;

/// <summary>
/// Modelos ADAM-6000 de E/S digital: cuántas entradas y salidas tiene cada uno.
/// Todos comparten el mismo mapa Modbus (entradas desde la bobina 00001,
/// salidas desde la 00017), así que el modelo solo fija las cantidades. Las
/// variantes "W" (inalámbricas) tienen la misma E/S que su modelo base.
/// </summary>
public static class AdamModels
{
    public static readonly IoModuleModel Adam6050 = new("ADAM-6050", "ADAM-6050", 12, 6, "12 DI / 6 DO (transistor)");
    public static readonly IoModuleModel Adam6051 = new("ADAM-6051", "ADAM-6051", 12, 2, "12 DI / 2 DO (transistor)");
    public static readonly IoModuleModel Adam6052 = new("ADAM-6052", "ADAM-6052", 8, 8, "8 DI / 8 DO (transistor)");
    public static readonly IoModuleModel Adam6055 = new("ADAM-6055", "ADAM-6055", 18, 0, "18 DI");
    public static readonly IoModuleModel Adam6060 = new("ADAM-6060", "ADAM-6060", 6, 6, "6 DI / 6 relés");
    public static readonly IoModuleModel Adam6066 = new("ADAM-6066", "ADAM-6066", 6, 6, "6 DI / 6 relés de potencia");

    public static IReadOnlyList<IoModuleModel> All { get; } =
        [Adam6050, Adam6051, Adam6052, Adam6055, Adam6060, Adam6066];

    /// <summary>Modelo por su clave ("ADAM-6066"), sin distinguir mayúsculas; null si no es de la familia.</summary>
    public static IoModuleModel? Find(string? key) =>
        All.FirstOrDefault(m => string.Equals(m.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Modelo por el número que informa el equipo en el registro 40211, que
    /// viene en BCD (0x6066 → ADAM-6066). null si no es uno de la lista.
    /// </summary>
    public static IoModuleModel? FromModuleId(ushort moduleId) => Find($"ADAM-{moduleId:X4}");
}
