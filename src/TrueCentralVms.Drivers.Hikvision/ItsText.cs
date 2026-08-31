namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Traducción a español de los códigos ITS/ANPR de HCNetSDK (colores, tipos de
/// vehículo, sentido de circulación…). Cuando el equipo informa un valor que
/// el SDK no documenta se devuelve el código crudo en vez de inventar una
/// etiqueta: es información real de la cámara y el operador puede reportarla.
/// Un valor 0 significa "no informado" en casi todos estos campos y se
/// devuelve como null para que la pantalla lo muestre como "—".
/// </summary>
internal static class ItsText
{
    private static string? Lookup(byte code, string[] labels) => code switch
    {
        0 => null,
        _ when code < labels.Length => labels[code],
        _ => $"Código {code}",
    };

    /// <summary>
    /// VCA_PLATE_COLOR. Aquí el 0 SÍ es un valor (azul), no "sin dato"; el
    /// 0xFF es la marca universal del SDK para "no informado" (aparece cuando
    /// el equipo vio el vehículo pero no logró leer la placa).
    /// </summary>
    public static string? PlateColor(byte code) => code switch
    {
        0xFF => null,
        0 => "Azul",
        1 => "Amarillo",
        2 => "Blanco",
        3 => "Negro",
        4 => "Verde",
        _ => $"Código {code}",
    };

    /// <summary>
    /// VCA_PLATE_TYPE. Los tipos del SDK son las categorías del registro chino;
    /// las dos estándar se muestran simplemente como "Estándar" (que es lo que
    /// reportan las cámaras fuera de China para una placa común).
    /// </summary>
    public static string? PlateType(byte code) => code switch
    {
        0xFF => null,
        0 or 1 => "Estándar",
        2 => "Policial militar",
        3 => "Policial",
        4 => "Trasera de dos líneas",
        5 => "Diplomática",
        6 => "Agrícola",
        7 => "Motocicleta",
        _ => $"Código {code}",
    };

    /// <summary>byVehicleType de NET_ITS_PLATE_RESULT / NET_DVR_VEHICLE_INFO.</summary>
    public static string? VehicleType(byte code) => Lookup(code,
        ["", "Bus", "Camión", "Automóvil", "Furgón", "Camioneta"]);

    /// <summary>byColor de NET_DVR_VEHICLE_INFO (VCR_CLR_CLASS).</summary>
    public static string? VehicleColor(byte code) => Lookup(code,
        ["", "Blanco", "Plata", "Gris", "Negro", "Rojo", "Azul oscuro", "Azul",
         "Amarillo", "Verde", "Café", "Rosado", "Morado", "Gris oscuro", "Cian"]);

    /// <summary>VLR_VEHICLE_CLASS: marca reconocida por el logo del vehículo.</summary>
    public static string? VehicleBrand(byte code) => Lookup(code,
        ["", "Volkswagen", "Buick", "BMW", "Honda", "Peugeot", "Toyota", "Ford",
         "Nissan", "Audi", "Mazda", "Chevrolet", "Citroën", "Hyundai", "Chery"]);

    /// <summary>byDir: sentido de circulación detectado.</summary>
    public static string? Direction(byte code) => Lookup(code,
        ["", "Entrada", "Salida", "Bidireccional", "Este → Oeste", "Oeste → Este",
         "Sur → Norte", "Norte → Sur", "Otra"]);

    /// <summary>byDetectType: qué disparó la captura.</summary>
    public static string? DetectType(byte code) => Lookup(code,
        ["", "Lazo magnético", "Video", "Multi-fotograma", "Radar"]);

    /// <summary>
    /// Atributos que la cámara observa dentro del vehículo. En todos estos
    /// campos el SDK usa 0 = no informado, 1 = no, 2 = sí, así que solo se
    /// listan los confirmados: mostrar "sin cinturón" porque el equipo no
    /// sabe sería peor que no decir nada.
    /// </summary>
    public static string? Attributes(byte pilotSafebelt, byte copilotSafebelt, byte pilotCall,
        byte dangerousVehicle, byte yellowLabel, byte pendant)
    {
        var parts = new List<string>();
        if (pilotSafebelt == 2) parts.Add("Conductor sin cinturón");
        if (copilotSafebelt == 2) parts.Add("Acompañante sin cinturón");
        if (pilotCall == 2) parts.Add("Conductor usando el teléfono");
        if (dangerousVehicle == 2) parts.Add("Transporte de carga peligrosa");
        if (yellowLabel == 2) parts.Add("Vehículo con etiqueta amarilla");
        if (pendant == 2) parts.Add("Colgante en el parabrisas");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// wIllegalType: código de infracción del equipo. El catálogo depende del
    /// firmware y del país, así que se informa el código tal cual.
    /// </summary>
    public static string? Violation(ushort code) => code == 0 ? null : $"Infracción {code}";
}
