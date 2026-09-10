using System.IO;
using System.Management;
using System.Text.RegularExpressions;

namespace TrueCentralVms.WebControl.Fingerprint;

/// <summary>Lector ofrecido al operador en el diálogo "origen de captura".</summary>
/// <param name="Id">Identificador estable: la letra de unidad, el puerto COM, o "auto".</param>
/// <param name="Name">Rótulo para la lista.</param>
/// <param name="Device">Ruta Win32 del equipo (<c>\\.\D:</c> o <c>\\.\COM5</c>); null en la detección automática.</param>
/// <param name="HardwareId">Ruta PnP del equipo (trae VID/PID o el modelo).</param>
/// <param name="Likely">Candidato reconocido como lector de huellas.</param>
public sealed record FingerprintReaderInfo(
    string Id, string Name, string? Device, string? HardwareId, bool Likely);

/// <summary>
/// Enumera los lectores de huellas conectados al equipo.
///
/// CÓMO SE PRESENTA EL LECTOR (verificado con un DS-K1F820-F real): NO es un
/// puerto serie, sino una <b>unidad de CD-ROM USB</b>
/// (<c>USBSTOR\CDROM&amp;VEN_&amp;PROD_DS-K1F820-F</c>, sin medio insertado). El SDK
/// abre <c>\\.\&lt;letra&gt;:</c> y le habla por comandos SCSI — de ahí la cadena
/// <c>\\.\%c:</c> dentro de FPModule_SDK.dll. Por eso los candidatos se buscan
/// entre las unidades de CD-ROM; los modelos antiguos con puerto serie se
/// contemplan igual, pero solo si el nombre del equipo los delata (así no se
/// llena la lista con COM de Bluetooth o de placa base).
///
/// El SDK no admite indicarle cuál abrir: siempre se ofrece además "Detección
/// automática", que es lo que hace él por su cuenta.
/// </summary>
public static class FingerprintReaders
{
    public const string AutoId = "auto";

    private static readonly Regex PortInCaption = new(@"\((COM\d+)\)", RegexOptions.Compiled);

    /// <summary>Modelos y palabras que identifican a un lector de enrolamiento.</summary>
    private static readonly Regex ReaderName = new(
        @"(?i)DS-K1F|hikvision|fingerprint|finger\s*print|huella", RegexOptions.Compiled);

    public static List<FingerprintReaderInfo> Scan()
    {
        var readers = new List<FingerprintReaderInfo>();
        readers.AddRange(ScanCdromReaders());
        readers.AddRange(ScanSerialReaders());

        // La detección automática siempre está disponible; queda de primera
        // opción cuando no se reconoció ningún lector, para que el operador no
        // tenga que elegir a ciegas.
        var auto = new FingerprintReaderInfo(
            AutoId, "Detección automática (primer lector Hikvision conectado)", null, null, true);
        if (readers.Count > 0) readers.Add(auto);
        else readers.Insert(0, auto);
        return readers;
    }

    /// <summary>Lectores que Windows ve como unidad de CD-ROM (DS-K1F820-F y familia).</summary>
    private static IEnumerable<FingerprintReaderInfo> ScanCdromReaders()
    {
        foreach (var (caption, drive, pnpId) in Query(
            "SELECT Caption, Drive, PNPDeviceID FROM Win32_CDROMDrive",
            item => (
                item["Caption"]?.ToString() ?? "",
                item["Drive"]?.ToString() ?? "",
                item["PNPDeviceID"]?.ToString() ?? "")))
        {
            if (!ReaderName.IsMatch($"{caption} {pnpId}")) continue;
            string letter = drive.Length >= 2 ? drive[..2] : drive; // "D:"
            if (letter.Length == 0) continue;

            yield return new FingerprintReaderInfo(
                Id: letter,
                Name: $"Lector de huellas {ExtractModel(caption)} ({letter})",
                Device: $@"\\.\{letter}",
                HardwareId: pnpId,
                Likely: true);
        }
    }

    /// <summary>Modelos antiguos que exponen puerto serie; solo si el nombre los delata.</summary>
    private static IEnumerable<FingerprintReaderInfo> ScanSerialReaders()
    {
        foreach (var (caption, deviceId, manufacturer) in Query(
            "SELECT Caption, DeviceID, Manufacturer FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'",
            item => (
                item["Caption"]?.ToString() ?? "",
                item["DeviceID"]?.ToString() ?? "",
                item["Manufacturer"]?.ToString() ?? "")))
        {
            if (!ReaderName.IsMatch($"{caption} {manufacturer} {deviceId}")) continue;
            var match = PortInCaption.Match(caption);
            if (!match.Success) continue;
            string port = match.Groups[1].Value;

            yield return new FingerprintReaderInfo(
                Id: port,
                Name: $"{caption.Trim()}",
                Device: $@"\\.\{port}",
                HardwareId: deviceId,
                Likely: true);
        }
    }

    /// <summary>Saca el modelo del nombre que reporta Windows ("DS-K1F820-F USB Device").</summary>
    private static string ExtractModel(string caption)
    {
        var match = Regex.Match(caption, @"(?i)DS-K1F\S*");
        return match.Success ? match.Value : caption.Trim();
    }

    /// <summary>Consulta WMI tolerante a fallos (si WMI no responde, no hay candidatos).</summary>
    private static List<T> Query<T>(string wql, Func<ManagementBaseObject, T> map)
    {
        var results = new List<T>();
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            foreach (var item in searcher.Get())
                results.Add(map(item));
        }
        catch (Exception)
        {
            // WMI deshabilitado o sin permisos: queda la detección automática.
        }
        return results;
    }

    /// <summary>
    /// Toma los lectores que el operador NO eligió para que la autodetección del
    /// SDK se salte esos equipos y abra el elegido. Es "el mejor esfuerzo
    /// posible": el SDK no admite indicarle cuál abrir, y si alguna reserva
    /// falla (permisos, equipo ocupado) simplemente se sigue adelante.
    /// </summary>
    public static IDisposable ReserveOtherReaders(string selectedId, IEnumerable<FingerprintReaderInfo> readers)
    {
        var held = new List<FileStream>();
        if (!string.Equals(selectedId, AutoId, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var reader in readers)
            {
                if (reader.Device is null || string.Equals(reader.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    held.Add(new FileStream(reader.Device, FileMode.Open, FileAccess.Read, FileShare.None));
                }
                catch (Exception)
                {
                    // Equipo ocupado o inaccesible: no hay nada que reservar.
                }
            }
        }
        return new ReaderReservation(held);
    }

    private sealed class ReaderReservation(List<FileStream> held) : IDisposable
    {
        public void Dispose()
        {
            foreach (var stream in held)
            {
                try { stream.Dispose(); } catch { }
            }
            held.Clear();
        }
    }
}
