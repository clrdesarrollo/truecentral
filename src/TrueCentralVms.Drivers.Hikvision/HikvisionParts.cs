using System.Xml.Linq;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Lo que un equipo Hikvision trae además de su función principal y que el VMS
/// ofrece como recursos propios: su altavoz y sus entradas y salidas de alarma.
/// Lo usan el driver de control de acceso (terminales) y el de video (cámaras
/// y grabadores): las rutas ISAPI son las mismas.
/// </summary>
internal static class HikvisionParts
{
    /// <summary>
    /// Altavoz: el canal de audio bidireccional (el mismo con que los parlantes IP
    /// Hikvision reciben la voz en vivo y los sonidos).
    /// </summary>
    internal static async Task<bool> HasSpeakerAsync(HikvisionIsapiClient client, CancellationToken ct)
    {
        // La lista de canales (la que se usa para abrir la voz) primero: hay firmwares
        // de terminales que no contestan la ruta de capacidades.
        foreach (string path in new[] { "/ISAPI/System/TwoWayAudio/channels", "/ISAPI/System/TwoWayAudio/channels/capabilities" })
        {
            try
            {
                if (await client.RequestAsync(HttpMethod.Get, path, ct: ct) is { } xml &&
                    xml.Contains("TwoWayAudioChannel", StringComparison.Ordinal))
                    return true;
            }
            catch (DriverException) { /* se prueba la otra ruta */ }
        }
        return false;
    }

    /// <summary>
    /// Entradas y salidas de alarma: <c>/ISAPI/System/IO</c> las lista (las de
    /// puerta —contacto, botón, cerradura— no van ahí). Null si no tiene.
    /// </summary>
    internal static async Task<DeviceIoInfo?> ReadIoAsync(HikvisionIsapiClient client, CancellationToken ct)
    {
        // La lista completa; si el equipo no la contesta, las de entradas y de salidas por separado.
        async Task<int> CountAsync(string path, string element)
        {
            try
            {
                return await client.RequestAsync(HttpMethod.Get, path, ct: ct) is { } xml
                    ? XDocument.Parse(xml).Descendants().Count(e => e.Name.LocalName == element) : -1;
            }
            catch (Exception ex) when (ex is DriverException or System.Xml.XmlException) { return -1; }
        }
        int inputs, outputs;
        try
        {
            string? list = await client.RequestAsync(HttpMethod.Get, "/ISAPI/System/IO", ct: ct);
            var all = list is null ? null : XDocument.Parse(list).Descendants().ToList();
            inputs = all?.Count(e => e.Name.LocalName == "IOInputPort") ?? -1;
            outputs = all?.Count(e => e.Name.LocalName == "IOOutputPort") ?? -1;
        }
        catch (Exception ex) when (ex is DriverException or System.Xml.XmlException) { inputs = outputs = -1; }
        if (inputs < 0 && outputs < 0)
        {
            inputs = await CountAsync("/ISAPI/System/IO/inputs", "IOInputPort");
            outputs = await CountAsync("/ISAPI/System/IO/outputs", "IOOutputPort");
        }
        inputs = Math.Max(0, inputs);
        outputs = Math.Max(0, outputs);
        return inputs + outputs == 0 ? null : new DeviceIoInfo(inputs, outputs);
    }
}
