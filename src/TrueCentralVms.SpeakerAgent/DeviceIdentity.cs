using System.Reflection;
using System.Runtime.InteropServices;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.SpeakerAgent;

/// <summary>Lo que el agente informa de sí mismo: versión, plataforma, hardware y número de serie.</summary>
public static class DeviceIdentity
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static string Platform => RuntimeInformation.RuntimeIdentifier;

    public static string Hardware { get; } = ReadHardware();

    public static string? SerialNumber { get; } = ReadSerial();

    public static AgentInfo Info => new(Version, SpeakerAgentProtocol.Version, Platform, Hardware, Environment.MachineName, SerialNumber);

    private static string ReadHardware()
    {
        try
        {
            // Raspberry Pi y otras placas ARM: el modelo exacto está en el árbol de dispositivos.
            const string model = "/proc/device-tree/model";
            if (File.Exists(model)) return File.ReadAllText(model).TrimEnd('\0', '\n', ' ');
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return RuntimeInformation.OSDescription.Trim();
    }

    private static string? ReadSerial()
    {
        try
        {
            const string cpuinfo = "/proc/cpuinfo";
            if (File.Exists(cpuinfo))
            {
                var line = File.ReadLines(cpuinfo).FirstOrDefault(l => l.StartsWith("Serial", StringComparison.OrdinalIgnoreCase));
                if (line?.Split(':', 2) is [_, var value] && value.Trim().TrimStart('0') is { Length: > 0 } serial) return serial;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
