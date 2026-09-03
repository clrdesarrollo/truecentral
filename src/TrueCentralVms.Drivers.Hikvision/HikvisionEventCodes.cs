using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Códigos de evento PROPIOS de Hikvision ("Original Code" de la guía del Hik
/// IP Receiver Pro, Apéndice D.1). Los paneles Hikvision envían estos números
/// —no el Contact-ID estándar— así que muchos no coinciden con
/// <see cref="ContactIdCatalog"/> (p. ej. 1822 = "Armado fallido", que en
/// Contact-ID sería 454). Este catálogo se consulta ANTES del genérico y solo
/// cubre los códigos específicos de Hikvision; los estándar (1130, 1301, 1570,
/// 1601...) siguen resolviéndose con <see cref="ContactIdCatalog"/>.
///
/// La clave es el código de 3 dígitos; el primer dígito del código de 4 es el
/// calificador (1 = nuevo/activo, 3 = restaurado, 6 = estado), igual que en
/// Contact-ID.
/// </summary>
public static class HikvisionEventCodes
{
    private sealed record Entry(AlarmEventKind Kind, AlarmSeverity Severity, string Text);

    private static readonly Dictionary<int, Entry> Codes = new()
    {
        // ---- Alarmas de zona (1xx). Hikvision usa códigos propios (p. ej. 103
        // = intrusión), distintos del Contact-ID estándar (130).
        [103] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Intrusión (robo)"),
        [114] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de incendio"),
        [121] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Coacción (clave de asalto)"),
        [122] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Pánico silencioso"),
        [123] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Pánico audible"),
        [124] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma 24 h"),
        [125] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de vibración 24 h"),
        [126] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma por tiempo excedido"),
        [127] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Pánico silencioso"),
        [129] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de pánico"),
        [131] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma perimetral"),
        [132] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Intrusión interior"),
        [133] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma 24 h"),
        [134] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de entrada/salida"),
        [139] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma confirmada"),
        [141] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Circuito abierto del bus"),
        [142] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Cortocircuito del bus"),
        [144] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Sonda externa desconectada"),
        [148] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de movimiento del dispositivo"),
        [149] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de enmascaramiento"),
        [151] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Fuga de gas"),
        [154] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Fuga de agua"),
        [207] = new(AlarmEventKind.Alarm, AlarmSeverity.Warning, "Preaviso de zona"),

        // ---- Fallas del sistema / alimentación / módulos (3xx)
        [305] = new(AlarmEventKind.System, AlarmSeverity.Info, "Reinicio del panel"),
        [312] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Protección por sobrecorriente"),
        [318] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Agotamiento de energía"),
        [319] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Protección por sobrevoltaje"),
        [328] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Cortocircuito de salida de energía"),
        [313] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla de batería"),
        [314] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Batería ausente"),
        [330] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla del expansor"),
        [333] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Excepción del expansor"),
        [336] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Impresora desconectada"),
        [337] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Batería baja del repetidor"),
        [338] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Bajo voltaje del expansor"),
        [339] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Pérdida de alimentación de red"),
        [340] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Batería del repetidor desconectada"),
        [341] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Tapa abierta (sabotaje del chasis)"),
        [342] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Pérdida de corriente AC del expansor"),
        [343] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Sabotaje del repetidor inalámbrico"),
        [344] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Sabotaje de la sirena inalámbrica"),
        [345] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Sirena inalámbrica desconectada"),
        [346] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Sabotaje de dispositivo inalámbrico"),
        [347] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Batería baja de dispositivo inalámbrico"),
        [348] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Dispositivo inalámbrico desconectado"),
        [349] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla de alimentación externa"),
        [351] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla del canal de comunicación principal (IP)"),
        [352] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla del canal de comunicación de respaldo (celular)"),
        [354] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Línea telefónica desconectada"),
        [359] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Fallo al subir el reporte"),
        [380] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla del sensor del detector"),
        [382] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla de supervisión del bus"),
        [383] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Detector saboteado"),
        [386] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Zona en circuito abierto"),
        [387] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Zona en cortocircuito"),

        // ---- Apertura/cierre, salidas, fallos de armado (4xx)
        [409] = new(AlarmEventKind.Disarm, AlarmSeverity.Info, "Desarmado por zona de llave"),
        [443] = new(AlarmEventKind.Info, AlarmSeverity.Info, "Salida activada por horario"),
        [452] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Desarmado tardío"),
        [455] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Armado automático fallido"),
        [460] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Fallo al activar la salida"),
        [461] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Fallo al desactivar la salida"),
        [462] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Desarmado automático fallido"),
        [467] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Contraseña incorrecta"),

        // ---- Red / anulaciones (5xx)
        [556] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Cambio de red"),
        [571] = new(AlarmEventKind.Bypass, AlarmSeverity.Warning, "Zona desactivada temporalmente"),
        [572] = new(AlarmEventKind.Bypass, AlarmSeverity.Warning, "Tapa desactivada temporalmente"),
        [573] = new(AlarmEventKind.Bypass, AlarmSeverity.Warning, "Zona desactivada"),
        [574] = new(AlarmEventKind.Bypass, AlarmSeverity.Warning, "Anulación por grupo"),

        // ---- Programación / pruebas (6xx)
        [627] = new(AlarmEventKind.System, AlarmSeverity.Info, "Entró a programación"),
        [628] = new(AlarmEventKind.System, AlarmSeverity.Info, "Salió de programación"),

        // ---- Alarmas y eventos técnicos (7xx)
        [750] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de barreno (perforación)"),
        [753] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de alta temperatura"),
        [754] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de baja temperatura"),
        [755] = new(AlarmEventKind.Bypass, AlarmSeverity.Warning, "Desactivación permanente (solo alarma)"),
        [756] = new(AlarmEventKind.Bypass, AlarmSeverity.Warning, "Desactivación por única vez (solo alarma)"),
        [759] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de zona técnica"),
        [761] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de intrusión"),
        [762] = new(AlarmEventKind.System, AlarmSeverity.Info, "Entró en prueba (soak test)"),
        [763] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Bucle del bus desconectado"),
        [766] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Excepción de periférico"),
        [770] = new(AlarmEventKind.System, AlarmSeverity.Info, "Auto-armado con retardo"),
        [773] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Reemplazar detector/periférico"),
        [774] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de cruce de zonas"),
        [775] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma PIR / aumento brusco de sonido"),
        [776] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Descenso brusco de sonido"),
        [777] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla de entrada de audio"),
        [778] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de cruce de línea"),
        [779] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Detección de entrada a región"),
        [780] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de fuente de fuego"),
        [781] = new(AlarmEventKind.Alarm, AlarmSeverity.Warning, "Prealarma de alta temperatura"),
        [782] = new(AlarmEventKind.Alarm, AlarmSeverity.Warning, "Prealarma de baja temperatura"),
        [783] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de alta temperatura"),
        [784] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de baja temperatura"),
        [785] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Detección de salida de región"),
        [786] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de temperatura"),

        // ---- Teclado / llavero / tarjetas (8xx)
        [810] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Pánico desde teclado/llavero"),
        [811] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Incendio desde teclado/llavero"),
        [812] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Intrusión desde teclado/llavero"),
        [822] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Armado fallido"),
        [847] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Emergencia médica desde teclado/llavero"),
        [862] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Teclado bloqueado"),
        [863] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de ausencia"),
        [864] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Lector de tarjetas bloqueado"),
        [865] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Tarjeta no registrada"),

        // ---- Módulos, red, video (9xx)
        [910] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Teclado desconectado"),
        [911] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Relé KBUS desconectado"),
        [912] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "KBUS GP/K desconectado"),
        [913] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "KBUS MN/K desconectado"),
        [914] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Detector inalámbrico desconectado"),
        [915] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Batería baja del detector inalámbrico"),
        [916] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Expansor desconectado"),
        [917] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Repetidor inalámbrico desconectado"),
        [918] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Módulo externo desconectado"),
        [919] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Batería baja de la sirena inalámbrica"),
        [920] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Red de datos celular desconectada"),
        [921] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Excepción de la SIM"),
        [922] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Falla de comunicación Wi-Fi"),
        [923] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Excepción de señal RF"),
        [924] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Flujo de red excedido"),
        [925] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Batería baja del llavero"),
        [926] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Conflicto de número de la SIM"),
        [930] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Conflicto de dirección IP"),
        [931] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Excepción de red cableada"),
        [940] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Detección de movimiento iniciada"),
        [941] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma de enmascaramiento"),
        [942] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Pérdida de señal de video"),
        [943] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Formato de entrada/salida no coincide"),
        [944] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Excepción de entrada de video"),
        [945] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Disco lleno"),
        [946] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Excepción de disco"),
        [947] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Fallo al subir imagen"),
        [948] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Fallo al enviar correo"),
        [949] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Cámara de red desconectada"),
        [973] = new(AlarmEventKind.Disarm, AlarmSeverity.Info, "Desarmado de zona individual"),
        [974] = new(AlarmEventKind.Restore, AlarmSeverity.Info, "Alarma de zona individual borrada"),
        [975] = new(AlarmEventKind.System, AlarmSeverity.Info, "Detector eliminado"),
        [981] = new(AlarmEventKind.System, AlarmSeverity.Info, "Dispositivo eliminado"),
        [982] = new(AlarmEventKind.System, AlarmSeverity.Warning, "Panel restaurado de fábrica"),
        [983] = new(AlarmEventKind.System, AlarmSeverity.Info, "Actualizando firmware"),
        [984] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Fallo al actualizar firmware"),
        [985] = new(AlarmEventKind.System, AlarmSeverity.Info, "Usuario eliminado"),
        [991] = new(AlarmEventKind.Alarm, AlarmSeverity.Critical, "Alarma médica silenciosa"),
        [992] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Sobrecarga del bus"),
        [993] = new(AlarmEventKind.Trouble, AlarmSeverity.Warning, "Error de tarjeta de memoria"),
    };

    /// <summary>
    /// Traduce un código propio de Hikvision ("1822", "3341", 1571...).
    /// Devuelve null si no es uno de estos códigos (el llamador debe entonces
    /// probar <see cref="ContactIdCatalog"/>).
    /// </summary>
    public static (AlarmEventKind Kind, AlarmSeverity Severity, string Description)? Translate(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim();
        if (code.Length != 4 || !int.TryParse(code, out int value)) return null;
        int qualifier = value / 1000;
        int eventCode = value % 1000;
        if (qualifier is not (1 or 3 or 6)) return null;
        if (!Codes.TryGetValue(eventCode, out var entry)) return null;

        if (qualifier == 3)
        {
            bool restorable = entry.Kind is AlarmEventKind.Alarm or AlarmEventKind.Trouble or AlarmEventKind.Bypass;
            return restorable
                ? (AlarmEventKind.Restore, AlarmSeverity.Info, entry.Text + " (restaurado)")
                : (entry.Kind, entry.Severity, entry.Text + " (restaurado)");
        }
        if (qualifier == 6)
            return (AlarmEventKind.Info, AlarmSeverity.Info, entry.Text + " (estado)");
        return (entry.Kind, entry.Severity, entry.Text);
    }
}
