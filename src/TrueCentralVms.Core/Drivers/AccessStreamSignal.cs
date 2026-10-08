namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Lo que avisa la escucha en vivo además de los eventos, para que el servidor
/// sepa cuánto confiar en ella: con una escucha sana, el historial se lee solo
/// de respaldo; ante un corte o un hueco, enseguida.
/// </summary>
public enum AccessStreamSignal
{
    /// <summary>La conexión quedó abierta: el equipo aceptó la suscripción.</summary>
    Connected,
    /// <summary>Llegó algo del equipo (un evento o su latido): la conexión está viva.</summary>
    Alive,
    /// <summary>Falta un número de evento entre dos que llegaron: algo no se recibió.</summary>
    Gap,
}
