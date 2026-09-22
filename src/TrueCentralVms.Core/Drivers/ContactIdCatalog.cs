using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Catálogo SIA Contact-ID (Ademco): traduce el código de 4 dígitos que
/// reportan casi todos los paneles de alarma (calificador + evento) a la
/// naturaleza, la severidad y una descripción en español. El calificador es
/// el primer dígito: 1 = evento nuevo / apertura, 3 = restauración / cierre,
/// 6 = reporte de estado. Los tres restantes identifican el evento.
///
/// Es compartido entre drivers: cualquier marca que hable Contact-ID (o que
/// lo emule en su API, como Hikvision) obtiene el mismo texto.
/// </summary>
public static class ContactIdCatalog
{
    /// <summary>Familia del evento según su código de 3 dígitos.</summary>
    private enum Family { Alarm, Supervisory, Trouble, OpenClose, Bypass, Test, System }

    private sealed record Entry(Family Family, string New, string? Restored = null, AlarmSeverity Severity = AlarmSeverity.Critical);

    private static readonly Dictionary<int, Entry> Codes = new()
    {
        // ---- Alarmas médicas (100)
        [100] = new(Family.Alarm, "Emergencia médica"),
        [101] = new(Family.Alarm, "Pulsador de emergencia personal"),
        [102] = new(Family.Alarm, "Falla de reporte de actividad"),
        // ---- Incendio (110)
        [110] = new(Family.Alarm, "Alarma de incendio"),
        [111] = new(Family.Alarm, "Alarma de humo"),
        [112] = new(Family.Alarm, "Combustión"),
        [113] = new(Family.Alarm, "Flujo de agua"),
        [114] = new(Family.Alarm, "Calor"),
        [115] = new(Family.Alarm, "Pulsador de incendio"),
        [116] = new(Family.Alarm, "Ducto"),
        [117] = new(Family.Alarm, "Llama"),
        [118] = new(Family.Alarm, "Prealarma de incendio"),
        // ---- Pánico (120)
        [120] = new(Family.Alarm, "Alarma de pánico"),
        [121] = new(Family.Alarm, "Coacción (clave de asalto)"),
        [122] = new(Family.Alarm, "Pánico silencioso"),
        [123] = new(Family.Alarm, "Pánico audible"),
        [124] = new(Family.Alarm, "Coacción: acceso concedido"),
        [125] = new(Family.Alarm, "Coacción: salida concedida"),
        // ---- Robo / intrusión (130)
        [130] = new(Family.Alarm, "Intrusión (robo)"),
        [131] = new(Family.Alarm, "Intrusión perimetral"),
        [132] = new(Family.Alarm, "Intrusión interior"),
        [133] = new(Family.Alarm, "Intrusión 24 horas"),
        [134] = new(Family.Alarm, "Intrusión entrada/salida"),
        [135] = new(Family.Alarm, "Intrusión día/noche"),
        [136] = new(Family.Alarm, "Intrusión exterior"),
        [137] = new(Family.Alarm, "Tamper (sabotaje)"),
        [138] = new(Family.Alarm, "Casi alarma (near alarm)"),
        [139] = new(Family.Alarm, "Intrusión verificada"),
        // ---- General (140)
        [140] = new(Family.Alarm, "Alarma general"),
        [141] = new(Family.Alarm, "Lazo abierto"),
        [142] = new(Family.Alarm, "Lazo en cortocircuito"),
        [143] = new(Family.Alarm, "Falla de módulo de expansión"),
        [144] = new(Family.Alarm, "Tamper de sensor"),
        [145] = new(Family.Alarm, "Tamper de módulo de expansión"),
        [146] = new(Family.Alarm, "Intrusión silenciosa"),
        [147] = new(Family.Alarm, "Falla de supervisión de sensor"),
        // ---- 24 h no robo (150)
        [150] = new(Family.Alarm, "Alarma 24 horas (no robo)"),
        [151] = new(Family.Alarm, "Detección de gas"),
        [152] = new(Family.Alarm, "Refrigeración"),
        [153] = new(Family.Alarm, "Pérdida de calefacción"),
        [154] = new(Family.Alarm, "Fuga de agua"),
        [155] = new(Family.Alarm, "Lámina/foil rota"),
        [156] = new(Family.Alarm, "Problema diurno"),
        [157] = new(Family.Alarm, "Gas bajo"),
        [158] = new(Family.Alarm, "Alta temperatura"),
        [159] = new(Family.Alarm, "Baja temperatura"),
        [161] = new(Family.Alarm, "Pérdida de flujo de aire"),
        [162] = new(Family.Alarm, "Monóxido de carbono"),
        [163] = new(Family.Alarm, "Nivel de tanque"),
        // ---- Supervisión incendio (200)
        [200] = new(Family.Supervisory, "Supervisión de incendio", Severity: AlarmSeverity.Warning),
        [201] = new(Family.Supervisory, "Presión de agua baja", Severity: AlarmSeverity.Warning),
        [202] = new(Family.Supervisory, "CO2 bajo", Severity: AlarmSeverity.Warning),
        [203] = new(Family.Supervisory, "Sensor de válvula", Severity: AlarmSeverity.Warning),
        [204] = new(Family.Supervisory, "Nivel de agua bajo", Severity: AlarmSeverity.Warning),
        [205] = new(Family.Supervisory, "Bomba activada", Severity: AlarmSeverity.Warning),
        [206] = new(Family.Supervisory, "Falla de bomba", Severity: AlarmSeverity.Warning),
        // ---- Fallas del sistema (300)
        [300] = new(Family.Trouble, "Falla del sistema", Severity: AlarmSeverity.Warning),
        [301] = new(Family.Trouble, "Pérdida de alimentación AC", "Alimentación AC restaurada", AlarmSeverity.Warning),
        [302] = new(Family.Trouble, "Batería baja del panel", "Batería del panel restaurada", AlarmSeverity.Warning),
        [303] = new(Family.Trouble, "Suma de verificación de RAM incorrecta", Severity: AlarmSeverity.Warning),
        [304] = new(Family.Trouble, "Suma de verificación de ROM incorrecta", Severity: AlarmSeverity.Warning),
        [305] = new(Family.Trouble, "Reinicio del sistema", Severity: AlarmSeverity.Warning),
        [306] = new(Family.Trouble, "Programación del panel modificada", Severity: AlarmSeverity.Warning),
        [307] = new(Family.Trouble, "Autoprueba fallida", Severity: AlarmSeverity.Warning),
        [308] = new(Family.Trouble, "Sistema apagado", Severity: AlarmSeverity.Warning),
        [309] = new(Family.Trouble, "Falla en prueba de batería", Severity: AlarmSeverity.Warning),
        [310] = new(Family.Trouble, "Falla de tierra", Severity: AlarmSeverity.Warning),
        [311] = new(Family.Trouble, "Batería ausente / descargada", "Batería restaurada", AlarmSeverity.Warning),
        [312] = new(Family.Trouble, "Sobrecorriente de alimentación", Severity: AlarmSeverity.Warning),
        [313] = new(Family.Trouble, "Reinicio por ingeniero", Severity: AlarmSeverity.Warning),
        [314] = new(Family.Trouble, "Falla de alimentación primaria", Severity: AlarmSeverity.Warning),
        [316] = new(Family.Trouble, "Sistema manipulado (tamper del sistema)", Severity: AlarmSeverity.Warning),
        [320] = new(Family.Trouble, "Falla de sirena/relé", Severity: AlarmSeverity.Warning),
        [321] = new(Family.Trouble, "Falla de sirena 1", "Sirena 1 restaurada", AlarmSeverity.Warning),
        [322] = new(Family.Trouble, "Falla de sirena 2", "Sirena 2 restaurada", AlarmSeverity.Warning),
        [323] = new(Family.Trouble, "Falla de relé de alarma", Severity: AlarmSeverity.Warning),
        [324] = new(Family.Trouble, "Falla de relé de problema", Severity: AlarmSeverity.Warning),
        [325] = new(Family.Trouble, "Falla de relé de reversión", Severity: AlarmSeverity.Warning),
        [326] = new(Family.Trouble, "Falla de campana/sirena 3", Severity: AlarmSeverity.Warning),
        [327] = new(Family.Trouble, "Falla de campana/sirena 4", Severity: AlarmSeverity.Warning),
        [330] = new(Family.Trouble, "Falla de periférico del sistema", Severity: AlarmSeverity.Warning),
        [331] = new(Family.Trouble, "Lazo de expansión abierto", Severity: AlarmSeverity.Warning),
        [332] = new(Family.Trouble, "Lazo de expansión en corto", Severity: AlarmSeverity.Warning),
        [333] = new(Family.Trouble, "Falla de módulo de expansión", Severity: AlarmSeverity.Warning),
        [334] = new(Family.Trouble, "Falla de repetidor", Severity: AlarmSeverity.Warning),
        [335] = new(Family.Trouble, "Impresora sin papel", Severity: AlarmSeverity.Warning),
        [336] = new(Family.Trouble, "Falla de impresora local", Severity: AlarmSeverity.Warning),
        [337] = new(Family.Trouble, "Pérdida de alimentación DC de expansor", Severity: AlarmSeverity.Warning),
        [338] = new(Family.Trouble, "Batería baja de expansor", Severity: AlarmSeverity.Warning),
        [339] = new(Family.Trouble, "Reinicio de expansor", Severity: AlarmSeverity.Warning),
        [341] = new(Family.Trouble, "Tamper de módulo de expansión", Severity: AlarmSeverity.Warning),
        [342] = new(Family.Trouble, "Pérdida de AC en módulo de expansión", Severity: AlarmSeverity.Warning),
        [343] = new(Family.Trouble, "Autoprueba de expansor fallida", Severity: AlarmSeverity.Warning),
        [344] = new(Family.Trouble, "Interferencia de radio (RF jamming)", "Interferencia de radio superada", AlarmSeverity.Warning),
        [350] = new(Family.Trouble, "Falla de comunicación", "Comunicación restaurada", AlarmSeverity.Warning),
        [351] = new(Family.Trouble, "Falla de línea telefónica 1", Severity: AlarmSeverity.Warning),
        [352] = new(Family.Trouble, "Falla de línea telefónica 2", Severity: AlarmSeverity.Warning),
        [353] = new(Family.Trouble, "Falla de transmisor de radio", Severity: AlarmSeverity.Warning),
        [354] = new(Family.Trouble, "No se pudo comunicar un evento", Severity: AlarmSeverity.Warning),
        [355] = new(Family.Trouble, "Pérdida de supervisión de radio", Severity: AlarmSeverity.Warning),
        [356] = new(Family.Trouble, "Pérdida de polling central", Severity: AlarmSeverity.Warning),
        [357] = new(Family.Trouble, "VSWR de transmisor de radio", Severity: AlarmSeverity.Warning),
        [358] = new(Family.Trouble, "Falla de red (Ethernet/IP)", "Red restaurada", AlarmSeverity.Warning),
        [370] = new(Family.Trouble, "Falla de protección", Severity: AlarmSeverity.Warning),
        [371] = new(Family.Trouble, "Circuito de protección abierto", Severity: AlarmSeverity.Warning),
        [372] = new(Family.Trouble, "Circuito de protección en corto", Severity: AlarmSeverity.Warning),
        [373] = new(Family.Trouble, "Falla de zona de incendio", Severity: AlarmSeverity.Warning),
        [374] = new(Family.Trouble, "Error de salida (armado con zona abierta)", Severity: AlarmSeverity.Warning),
        [375] = new(Family.Trouble, "Falla de zona de pánico", Severity: AlarmSeverity.Warning),
        [376] = new(Family.Trouble, "Falla de zona de atraco", Severity: AlarmSeverity.Warning),
        [377] = new(Family.Trouble, "Zona con fallas repetidas (swinger)", Severity: AlarmSeverity.Warning),
        [378] = new(Family.Trouble, "Falla de zona cruzada", Severity: AlarmSeverity.Warning),
        [380] = new(Family.Trouble, "Falla de sensor", Severity: AlarmSeverity.Warning),
        [381] = new(Family.Trouble, "Pérdida de supervisión de sensor RF", "Supervisión de sensor RF restaurada", AlarmSeverity.Warning),
        [382] = new(Family.Trouble, "Pérdida de supervisión RPM", Severity: AlarmSeverity.Warning),
        [383] = new(Family.Trouble, "Tamper de sensor", "Tamper de sensor restaurado", AlarmSeverity.Warning),
        [384] = new(Family.Trouble, "Batería baja de sensor RF", "Batería de sensor RF restaurada", AlarmSeverity.Warning),
        [385] = new(Family.Trouble, "Sensibilidad alta de detector de humo", Severity: AlarmSeverity.Warning),
        [386] = new(Family.Trouble, "Sensibilidad baja de detector de humo", Severity: AlarmSeverity.Warning),
        [387] = new(Family.Trouble, "Sensibilidad alta de sensor de intrusión", Severity: AlarmSeverity.Warning),
        [388] = new(Family.Trouble, "Sensibilidad baja de sensor de intrusión", Severity: AlarmSeverity.Warning),
        [389] = new(Family.Trouble, "Autoprueba de sensor fallida", Severity: AlarmSeverity.Warning),
        [391] = new(Family.Trouble, "Falla de vigilancia de sensor", Severity: AlarmSeverity.Warning),
        [392] = new(Family.Trouble, "Compensación de deriva", Severity: AlarmSeverity.Warning),
        [393] = new(Family.Trouble, "Alerta de mantención", Severity: AlarmSeverity.Warning),
        // ---- Apertura / cierre (400): 1 = apertura (desarmado), 3 = cierre (armado)
        [400] = new(Family.OpenClose, "Desarmado", "Armado", AlarmSeverity.Info),
        [401] = new(Family.OpenClose, "Desarmado por usuario", "Armado por usuario", AlarmSeverity.Info),
        [402] = new(Family.OpenClose, "Desarmado por grupo", "Armado por grupo", AlarmSeverity.Info),
        [403] = new(Family.OpenClose, "Desarmado automático", "Armado automático", AlarmSeverity.Info),
        [404] = new(Family.OpenClose, "Desarmado tardío", "Armado tardío", AlarmSeverity.Info),
        [405] = new(Family.OpenClose, "Desarmado diferido", "Armado diferido", AlarmSeverity.Info),
        [406] = new(Family.OpenClose, "Alarma cancelada", "Alarma cancelada", AlarmSeverity.Info),
        [407] = new(Family.OpenClose, "Desarmado remoto", "Armado remoto", AlarmSeverity.Info),
        [408] = new(Family.OpenClose, "Armado rápido", "Armado rápido", AlarmSeverity.Info),
        [409] = new(Family.OpenClose, "Desarmado por llave", "Armado por llave", AlarmSeverity.Info),
        [411] = new(Family.System, "Solicitud de descarga (callback)", Severity: AlarmSeverity.Info),
        [412] = new(Family.System, "Descarga/carga de programación correcta", Severity: AlarmSeverity.Info),
        [413] = new(Family.System, "Acceso de descarga fallido", Severity: AlarmSeverity.Warning),
        [414] = new(Family.System, "Apagado del sistema", Severity: AlarmSeverity.Warning),
        [415] = new(Family.System, "Apagado del marcador", Severity: AlarmSeverity.Warning),
        [416] = new(Family.System, "Carga de programación correcta", Severity: AlarmSeverity.Info),
        [421] = new(Family.System, "Acceso denegado", Severity: AlarmSeverity.Warning),
        [422] = new(Family.System, "Acceso concedido", Severity: AlarmSeverity.Info),
        [423] = new(Family.System, "Acceso forzado", Severity: AlarmSeverity.Warning),
        [424] = new(Family.System, "Salida denegada", Severity: AlarmSeverity.Warning),
        [425] = new(Family.System, "Salida concedida", Severity: AlarmSeverity.Info),
        [426] = new(Family.System, "Puerta abierta demasiado tiempo", Severity: AlarmSeverity.Warning),
        [429] = new(Family.System, "Ingresó al modo programación", Severity: AlarmSeverity.Info),
        [430] = new(Family.System, "Salió del modo programación", Severity: AlarmSeverity.Info),
        [441] = new(Family.OpenClose, "Desarmado desde modo en casa", "Armado en casa (stay)", AlarmSeverity.Info),
        [442] = new(Family.OpenClose, "Desarmado por llave (stay)", "Armado por llave (stay)", AlarmSeverity.Info),
        [450] = new(Family.OpenClose, "Apertura/cierre excepcional", "Apertura/cierre excepcional", AlarmSeverity.Info),
        [451] = new(Family.OpenClose, "Apertura anticipada", "Cierre anticipado", AlarmSeverity.Info),
        [452] = new(Family.OpenClose, "Apertura tardía", "Cierre tardío", AlarmSeverity.Info),
        [453] = new(Family.OpenClose, "Falla de apertura", "Falla de cierre", AlarmSeverity.Warning),
        [454] = new(Family.OpenClose, "Falla de cierre", "Falla de cierre", AlarmSeverity.Warning),
        [455] = new(Family.OpenClose, "Falla de armado automático", "Falla de armado automático", AlarmSeverity.Warning),
        [456] = new(Family.OpenClose, "Armado parcial", "Armado parcial", AlarmSeverity.Info),
        [457] = new(Family.OpenClose, "Error de salida (usuario)", "Error de salida (usuario)", AlarmSeverity.Warning),
        [458] = new(Family.OpenClose, "Usuario en el sitio", "Usuario en el sitio", AlarmSeverity.Info),
        [459] = new(Family.OpenClose, "Cierre reciente", "Cierre reciente", AlarmSeverity.Info),
        [461] = new(Family.System, "Código incorrecto ingresado", Severity: AlarmSeverity.Warning),
        [462] = new(Family.System, "Código válido ingresado", Severity: AlarmSeverity.Info),
        [463] = new(Family.OpenClose, "Rearmado tras alarma", "Rearmado tras alarma", AlarmSeverity.Info),
        [464] = new(Family.OpenClose, "Tiempo de armado automático extendido", "Tiempo de armado automático extendido", AlarmSeverity.Info),
        [465] = new(Family.OpenClose, "Reinicio de alarma de pánico", "Reinicio de alarma de pánico", AlarmSeverity.Info),
        [466] = new(Family.System, "Servicio en el sitio", "Servicio terminado", AlarmSeverity.Info),
        // ---- Deshabilitaciones / anulaciones (500)
        [501] = new(Family.Bypass, "Lector de acceso deshabilitado", "Lector de acceso habilitado", AlarmSeverity.Info),
        [520] = new(Family.Bypass, "Sirena deshabilitada", "Sirena habilitada", AlarmSeverity.Warning),
        [521] = new(Family.Bypass, "Sirena 1 deshabilitada", "Sirena 1 habilitada", AlarmSeverity.Warning),
        [522] = new(Family.Bypass, "Sirena 2 deshabilitada", "Sirena 2 habilitada", AlarmSeverity.Warning),
        [523] = new(Family.Bypass, "Relé de alarma deshabilitado", "Relé de alarma habilitado", AlarmSeverity.Warning),
        [531] = new(Family.Bypass, "Módulo agregado", "Módulo removido", AlarmSeverity.Info),
        [532] = new(Family.Bypass, "Módulo removido", "Módulo agregado", AlarmSeverity.Info),
        [551] = new(Family.Bypass, "Marcador deshabilitado", "Marcador habilitado", AlarmSeverity.Warning),
        [552] = new(Family.Bypass, "Transmisor de radio deshabilitado", "Transmisor de radio habilitado", AlarmSeverity.Warning),
        [553] = new(Family.Bypass, "Descarga remota deshabilitada", "Descarga remota habilitada", AlarmSeverity.Info),
        [570] = new(Family.Bypass, "Zona anulada (bypass)", "Zona restituida (fin de bypass)", AlarmSeverity.Warning),
        [571] = new(Family.Bypass, "Zona de incendio anulada", "Zona de incendio restituida", AlarmSeverity.Warning),
        [572] = new(Family.Bypass, "Zona 24 h anulada", "Zona 24 h restituida", AlarmSeverity.Warning),
        [573] = new(Family.Bypass, "Zona de intrusión anulada", "Zona de intrusión restituida", AlarmSeverity.Warning),
        [574] = new(Family.Bypass, "Grupo anulado", "Grupo restituido", AlarmSeverity.Warning),
        [575] = new(Family.Bypass, "Zona anulada por fallas repetidas", "Zona restituida (fallas repetidas)", AlarmSeverity.Warning),
        [576] = new(Family.Bypass, "Zona de acceso protegida anulada", "Zona de acceso protegida restituida", AlarmSeverity.Warning),
        [577] = new(Family.Bypass, "Punto de acceso anulado", "Punto de acceso restituido", AlarmSeverity.Warning),
        // ---- Pruebas y misceláneos (600)
        [601] = new(Family.Test, "Prueba manual disparada", Severity: AlarmSeverity.Info),
        [602] = new(Family.Test, "Reporte de prueba periódica", Severity: AlarmSeverity.Info),
        [603] = new(Family.Test, "Transmisión periódica por radio", Severity: AlarmSeverity.Info),
        [604] = new(Family.Test, "Prueba de incendio", Severity: AlarmSeverity.Info),
        [605] = new(Family.Test, "Reporte de estado a seguimiento", Severity: AlarmSeverity.Info),
        [606] = new(Family.Test, "Escucha a seguimiento", Severity: AlarmSeverity.Info),
        [607] = new(Family.Test, "Modo de prueba de caminata", "Fin de prueba de caminata", AlarmSeverity.Info),
        [608] = new(Family.Test, "Prueba periódica: problema del sistema presente", Severity: AlarmSeverity.Warning),
        [609] = new(Family.Test, "Video transmitido", Severity: AlarmSeverity.Info),
        [611] = new(Family.Test, "Punto probado correctamente", Severity: AlarmSeverity.Info),
        [612] = new(Family.Test, "Punto no probado", Severity: AlarmSeverity.Info),
        [613] = new(Family.Test, "Zona de intrusión activada en prueba", Severity: AlarmSeverity.Info),
        [614] = new(Family.Test, "Zona de incendio activada en prueba", Severity: AlarmSeverity.Info),
        [615] = new(Family.Test, "Zona de pánico activada en prueba", Severity: AlarmSeverity.Info),
        [616] = new(Family.Test, "Solicitud de servicio", Severity: AlarmSeverity.Info),
        [621] = new(Family.System, "Registro de eventos reiniciado", Severity: AlarmSeverity.Info),
        [622] = new(Family.System, "Registro de eventos al 50 %", Severity: AlarmSeverity.Info),
        [623] = new(Family.System, "Registro de eventos al 90 %", Severity: AlarmSeverity.Warning),
        [624] = new(Family.System, "Registro de eventos desbordado", Severity: AlarmSeverity.Warning),
        [625] = new(Family.System, "Fecha/hora reajustada", Severity: AlarmSeverity.Info),
        [626] = new(Family.System, "Fecha/hora incorrecta", Severity: AlarmSeverity.Warning),
        [627] = new(Family.System, "Ingreso a programación", Severity: AlarmSeverity.Info),
        [628] = new(Family.System, "Salida de programación", Severity: AlarmSeverity.Info),
        [629] = new(Family.System, "Evento de 32 horas", Severity: AlarmSeverity.Info),
        [630] = new(Family.System, "Cambio de horario", Severity: AlarmSeverity.Info),
        [631] = new(Family.System, "Excepción de horario", Severity: AlarmSeverity.Info),
        [632] = new(Family.System, "Punto de acceso desbloqueado", "Punto de acceso bloqueado", AlarmSeverity.Info),
        [641] = new(Family.System, "Falla de vigilancia (senior watch)", Severity: AlarmSeverity.Warning),
        [642] = new(Family.System, "Falla de reporte de actividad", Severity: AlarmSeverity.Warning),
        [651] = new(Family.System, "Reservado (uso del fabricante)", Severity: AlarmSeverity.Info),
        [652] = new(Family.System, "Reservado (uso del fabricante)", Severity: AlarmSeverity.Info),
        [653] = new(Family.System, "Reservado (uso del fabricante)", Severity: AlarmSeverity.Info),
        [654] = new(Family.System, "Sistema inactivo", Severity: AlarmSeverity.Info),
    };

    /// <summary>
    /// Traduce un código Contact-ID ("1130", "3401", 1570...). Devuelve null si
    /// el código no es Contact-ID (no tiene 4 dígitos con calificador 1/3/6).
    /// </summary>
    public static (AlarmEventKind Kind, AlarmSeverity Severity, string Description)? Translate(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim();
        if (code.Length != 4 || !int.TryParse(code, out int value)) return null;
        int qualifier = value / 1000;
        int eventCode = value % 1000;
        if (qualifier is not (1 or 3 or 6)) return null;

        if (!Codes.TryGetValue(eventCode, out var entry))
        {
            // Sin entrada exacta se deduce por la centena, que en Contact-ID
            // ya define la familia.
            var (kind, severity, family) = (eventCode / 100) switch
            {
                1 => ("Alarma", AlarmSeverity.Critical, Family.Alarm),
                2 => ("Supervisión", AlarmSeverity.Warning, Family.Supervisory),
                3 => ("Falla", AlarmSeverity.Warning, Family.Trouble),
                4 => ("Apertura/cierre", AlarmSeverity.Info, Family.OpenClose),
                5 => ("Anulación", AlarmSeverity.Warning, Family.Bypass),
                6 => ("Prueba", AlarmSeverity.Info, Family.Test),
                _ => ("Evento", AlarmSeverity.Info, Family.System),
            };
            entry = new Entry(family, $"{kind} (código {eventCode})", null, severity);
        }

        bool restored = qualifier == 3;
        string description = restored ? entry.Restored ?? $"{entry.New}: restaurado" : entry.New;
        if (qualifier == 6) description = $"{entry.New} (reporte de estado)";

        var resultKind = entry.Family switch
        {
            Family.Alarm or Family.Supervisory => restored ? AlarmEventKind.Restore : AlarmEventKind.Alarm,
            Family.Trouble => restored ? AlarmEventKind.Restore : AlarmEventKind.Trouble,
            // Apertura (1) = desarmado; cierre (3) = armado. 406 "cancelada" es informativo.
            Family.OpenClose when eventCode == 406 => AlarmEventKind.Info,
            Family.OpenClose => restored ? AlarmEventKind.Arm : AlarmEventKind.Disarm,
            Family.Bypass => AlarmEventKind.Bypass,
            Family.Test => AlarmEventKind.System,
            _ => AlarmEventKind.System,
        };
        if (qualifier == 6) resultKind = AlarmEventKind.Info;

        var resultSeverity = resultKind switch
        {
            AlarmEventKind.Alarm => AlarmSeverity.Critical,
            AlarmEventKind.Restore => AlarmSeverity.Info,
            AlarmEventKind.Info => AlarmSeverity.Info,
            _ => entry.Severity == AlarmSeverity.Critical ? AlarmSeverity.Warning : entry.Severity,
        };
        return (resultKind, resultSeverity, description);
    }

    /// <summary>
    /// Explica en una frase corta el código tal como lo envió el panel, para
    /// mostrarlo junto al evento sin que el operador tenga que conocer la norma:
    /// "Contact-ID 1406 · evento nuevo · apertura/cierre (406)". El primer dígito
    /// es el calificador (1 nuevo/apertura, 3 restauración/cierre, 6 estado) y
    /// los tres restantes el evento, cuya centena define el grupo. Acepta
    /// también los códigos SIA cortos (E350 / R350). Devuelve null si no
    /// reconoce el formato.
    /// </summary>
    public static string? Explain(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim();

        if (code.Length == 4 && int.TryParse(code, out int value))
        {
            int qualifier = value / 1000;
            int eventCode = value % 1000;
            int hundreds = eventCode / 100;
            string? qualifierText = (qualifier, hundreds) switch
            {
                (1, 4) => eventCode == 406 ? "cancelación" : "apertura (desarmado)",
                (3, 4) => eventCode == 406 ? "cancelación" : "cierre (armado)",
                (1, _) => "evento nuevo",
                (3, _) => "restauración",
                (6, _) => "reporte de estado",
                _ => null,
            };
            if (qualifierText is null) return null;
            string group = hundreds switch
            {
                1 => "alarma",
                2 => "supervisión",
                3 => "falla del sistema",
                4 => "apertura/cierre",
                5 => "anulación / deshabilitación",
                6 => "prueba / misceláneo",
                _ => "propio del fabricante",
            };
            return $"Contact-ID {code} · {qualifierText} · {group} ({eventCode})";
        }

        if (code.Length is >= 2 and <= 5 && char.IsLetter(code[0]) && code[1..].All(char.IsDigit))
        {
            string? kind = char.ToUpperInvariant(code[0]) switch
            {
                'E' => "evento",
                'R' => "restauración",
                'P' => "estado",
                _ => null,
            };
            return kind is null ? null : $"SIA {code.ToUpperInvariant()} · {kind}";
        }
        return null;
    }
}
