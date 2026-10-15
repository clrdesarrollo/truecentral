using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Datos de conexión de un equipo de video para su driver: dirección y credenciales, o su ID ISUP si es él quien se conecta al
/// servidor (entonces no tiene contraseña guardada).
/// </summary>
public static class DeviceConnections
{
    public static DeviceConnectionInfo Of(Device device, CredentialProtector protector) => new(
        device.Host, device.SdkPort, device.Username,
        device.PasswordCiphertext.Length == 0 ? "" : protector.Unprotect(device.PasswordCiphertext),
        device.IsupDeviceId);
}
