using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Receptora de paneles instalada junto al servidor (Hik IP Receiver Pro).
///
/// El instalador la deja escuchando solo en 127.0.0.1 y este servicio la
/// <b>activa solo</b> en el primer arranque: genera una contraseña aleatoria,
/// se la fija a la cuenta admin de la receptora y la guarda cifrada junto a los
/// datos del sistema. Nadie —ni el operador ni el administrador del VMS— tiene
/// que conocerla ni escribirla: para el usuario la receptora es un servicio
/// interno que no se ve, igual que PostgreSQL o MediaMTX.
///
/// Cómo se activa (reproducido del propio panel web de la receptora, que hace
/// esto mismo en JavaScript):
///  1. Se genera un par RSA de 1024 bits y se manda el módulo, en hexadecimal
///     y Base64, a <c>POST /ISAPI/Security/challenge</c>.
///  2. La receptora responde con una clave de sesión cifrada con esa clave
///     pública (RSA PKCS#1 v1.5); al descifrarla quedan 32 caracteres
///     hexadecimales.
///  3. Esos 32 caracteres son la llave AES-128. La contraseña viaja como
///     Base64 de: AES(primeros 16 caracteres del desafío) + AES(contraseña),
///     ambos en ECB, relleno de ceros y salida hexadecimal.
///  4. <c>PUT /ISAPI/System/activate</c> con <c>{"ActivateInfo":{"password":…}}</c>.
///
/// Si la receptora ya estaba activada por fuera (responde
/// <c>hasActivated</c>) no se puede adivinar su contraseña: queda anotado en el
/// registro para que un administrador la escriba a mano una sola vez.
///
/// Con la credencial en la mano queda un segundo paso, igual de invisible:
/// dejarle habilitada la salida «Automation Output → Protocol» con tipo
/// <c>Private</c>. Sin ella su API rechaza la suscripción de eventos ("Invalid
/// operation") y los paneles funcionan solo por sondeo —hasta 30 s de atraso y
/// sin el usuario que armó o desarmó—. No lo puede hacer el instalador:
/// recién acá existe la contraseña que la receptora exige para configurarse.
/// </summary>
public sealed class LocalIpReceiverService(
    IConfiguration config,
    EmbeddedPostgres postgres,
    CredentialProtector protector,
    AlarmDriverRegistry drivers,
    ILogger<LocalIpReceiverService> logger)
{
    /// <summary>Nombre de la cuenta que crea la activación (la receptora no admite otro).</summary>
    public const string AdminUser = "admin";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>La salida de eventos ya quedó habilitada en esta ejecución (no hay que volver a preguntar).</summary>
    private bool _eventsEnabled;

    public string Host => config["Alarms:LocalReceiver:Host"] is { Length: > 0 } h ? h : "127.0.0.1";
    public int Port => int.TryParse(config["Alarms:LocalReceiver:Port"], out int p) && p is > 0 and < 65536 ? p : 8091;
    public bool Enabled => !string.Equals(config["Alarms:LocalReceiver:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Junto a los datos (mismo criterio y misma ACL que tcvms-pg.secret).</summary>
    private string SecretPath => Path.Combine(postgres.DataDirectory, "tcvms-iprp.secret");

    /// <summary>¿Hay una contraseña guardada para la receptora local?</summary>
    public bool HasCredentials => File.Exists(SecretPath);

    /// <summary>
    /// Contraseña de la receptora local, o null si todavía no se activó. Se usa
    /// solo dentro del servidor: nunca se expone por la API.
    /// </summary>
    public string? GetPassword()
    {
        try
        {
            return File.Exists(SecretPath)
                ? protector.Unprotect(Convert.FromBase64String(File.ReadAllText(SecretPath).Trim()))
                : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo leer la credencial de la receptora local ({Path}).", SecretPath);
            return null;
        }
    }

    private void SavePassword(string password) =>
        File.WriteAllText(SecretPath, Convert.ToBase64String(protector.Protect(password)));

    /// <summary>
    /// Deja la receptora local lista para usarse: si no está activada, la activa
    /// con una contraseña nueva. No lanza: un fallo aquí no debe impedir que el
    /// servidor arranque (la receptora puede no estar instalada).
    /// </summary>
    public async Task<LocalReceiverState> EnsureActivatedAsync(CancellationToken ct = default)
    {
        if (!Enabled)
            return new LocalReceiverState(false, false, "La receptora local está desactivada por configuración.", Final: true);

        await _gate.WaitAsync(ct);
        try
        {
            if (HasCredentials)
                return await ReadyAsync(ct);

            using var http = CreateClient();
            if (!await IsReachableAsync(http, ct))
                return new LocalReceiverState(false, false,
                    $"No hay una receptora de paneles escuchando en {Host}:{Port}.");

            // Si la receptora dice que ya está activada, no hay nada que intentar.
            if (await IsActivatedAsync(http, ct) is true)
            {
                logger.LogWarning(
                    "La receptora de {Host}:{Port} ya estaba activada antes de instalar el sistema y su contraseña no la " +
                    "conoce el servidor. Escríbala una vez al agregar el panel, o reinstale la receptora para que el " +
                    "sistema la active solo.", Host, Port);
                return new LocalReceiverState(true, false,
                    "La receptora ya estaba activada con una contraseña que el sistema no conoce.", Final: true);
            }

            string password = GeneratePassword();
            string body = "";

            // Dos formas de mandar el mismo JSON: la del propio panel de la
            // receptora (declarado como formulario) y la estándar. Distintas
            // versiones del equipo aceptan una u otra, así que se prueban las
            // dos antes de darse por vencido. Cada intento renueva el desafío:
            // la llave de sesión se usa una sola vez.
            foreach (string contentType in new[] { "application/x-www-form-urlencoded", "application/json" })
            {
                string encrypted = await EncryptForReceiverAsync(http, password, ct);
                string json = JsonSerializer.Serialize(new { ActivateInfo = new { password = encrypted } });

                using var request = new HttpRequestMessage(HttpMethod.Put, "/ISAPI/System/activate?format=json")
                {
                    Content = contentType == "application/json"
                        ? new StringContent(json, Encoding.UTF8, "application/json")
                        : VendorBody(json),
                };
                using var response = await http.SendAsync(request, ct);
                body = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    SavePassword(password);
                    logger.LogInformation(
                        "Receptora de paneles activada automáticamente en {Host}:{Port} (formato {ContentType}); su " +
                        "credencial queda cifrada en {Path} y la administra el servidor (nadie necesita conocerla).",
                        Host, Port, contentType, SecretPath);
                    return await ReadyAsync(ct);
                }

                if (body.Contains("hasActivated", StringComparison.OrdinalIgnoreCase))
                    break;

                logger.LogWarning("La receptora de {Host}:{Port} rechazó la activación enviada como {ContentType}: {Body}",
                    Host, Port, contentType, body.Trim());
            }

            if (body.Contains("hasActivated", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "La receptora de {Host}:{Port} ya estaba activada antes de instalar el sistema y su contraseña no la " +
                    "conoce el servidor. Escríbala una vez al agregar el panel, o reinstale la receptora para que el " +
                    "sistema la active solo.", Host, Port);
                return new LocalReceiverState(true, false,
                    "La receptora ya estaba activada con una contraseña que el sistema no conoce.", Final: true);
            }

            return new LocalReceiverState(true, false, "La receptora rechazó la activación automática.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo activar automáticamente la receptora de paneles de {Host}:{Port}.", Host, Port);
            return new LocalReceiverState(false, false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// La receptora ya tiene credencial; queda dejarle habilitada la salida de
    /// eventos. Si eso todavía no se logra (recién arrancando, por ejemplo) el
    /// estado no es final y el arranque vuelve a intentarlo: un panel sin
    /// canal de eventos funciona, pero a medias.
    /// </summary>
    private async Task<LocalReceiverState> ReadyAsync(CancellationToken ct)
    {
        if (_eventsEnabled) return new LocalReceiverState(true, true, null, Final: true);
        if (await EnableEventsAsync(ct) is { } problem)
            return new LocalReceiverState(true, true, problem);
        _eventsEnabled = true;
        return new LocalReceiverState(true, true, null, Final: true);
    }

    /// <summary>
    /// Habilita en la receptora la salida de automatización «Private», sin la
    /// cual su API rechaza la suscripción de eventos de los paneles. Es
    /// idempotente (el driver lee antes de escribir). Devuelve null si quedó
    /// lista o si no hay nada que hacer, o el motivo por el que hay que
    /// reintentar.
    /// </summary>
    private async Task<string?> EnableEventsAsync(CancellationToken ct)
    {
        // Activada por fuera (sin contraseña conocida) o sin driver de
        // pasarela: no hay con qué configurarla, y el estado ya lo explica.
        if (GetPassword() is not { Length: > 0 } password) return null;
        if (drivers.All.Select(f => f.Create()).OfType<IAlarmGatewayDriver>().FirstOrDefault() is not { } gateway) return null;

        try
        {
            await gateway.EnsureEventsEnabledAsync(new AlarmConnectionInfo(Host, Port, false, AdminUser, password), ct);
            logger.LogInformation("Receptora de {Host}:{Port}: salida de eventos «Private» habilitada; los paneles reciben " +
                                  "sus eventos en el momento y no solo por sondeo.", Host, Port);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo habilitar la salida de eventos «Private» en la receptora de {Host}:{Port}: " +
                                  "los paneles quedan solo con el sondeo hasta lograrlo.", Host, Port);
            return "La receptora todavía no acepta habilitar su salida de eventos «Private»: los paneles funcionan por sondeo.";
        }
    }

    /// <summary>
    /// Cuerpo tal como lo manda el propio panel de la receptora: JSON, pero
    /// declarado como formulario. Se capturó de su interfaz web, y la
    /// activación falla ("notActivated") si se envía como application/json.
    /// </summary>
    private static StringContent VendorBody(string json)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded") { CharSet = "UTF-8" };
        return content;
    }

    /// <summary>
    /// Estado de activación según la propia receptora. Null si no se pudo
    /// determinar (el equipo ya activado responde 403 a esta consulta).
    /// </summary>
    private static async Task<bool?> IsActivatedAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("/ISAPI/System/activateStatus?format=json", ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return body.Contains("hasActivated", StringComparison.OrdinalIgnoreCase) ? true : null;
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("ActivateStatus", out var status) &&
                status.TryGetProperty("Activated", out var activated))
                return activated.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => activated.GetInt32() != 0,
                    _ => null,
                };
            return null;
        }
        catch
        {
            return null;
        }
    }

    private HttpClient CreateClient()
    {
        // Por IP y nunca por "localhost": el nginx de la receptora compara el
        // encabezado Host con la dirección en la que escucha y responde 403 si
        // no coinciden.
        var http = new HttpClient { BaseAddress = new Uri($"http://{Host}:{Port}"), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private static async Task<bool> IsReachableAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("/", ct);
            return true; // cualquier respuesta sirve: lo que importa es que haya alguien escuchando
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Contraseña
    // ------------------------------------------------------------------

    /// <summary>
    /// Contraseña larga y aleatoria que cumple la política de la receptora
    /// (mínimo 8, y no puede ser toda de la misma clase de caracteres). No se
    /// usan símbolos que compliquen el transporte por JSON ni el copiado
    /// manual si alguna vez hubiera que leerla del archivo.
    /// </summary>
    private static string GeneratePassword()
    {
        // 16 caracteres: los equipos Hikvision suelen limitar la contrasena a 16,
        // asi que pasarse de ahi hace fallar la activacion. Con cuatro clases y
        // ese largo sobra fuerza (nadie la escribe: la guarda el servidor).
        const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string Lower = "abcdefghijkmnopqrstuvwxyz";
        const string Digits = "23456789";
        const string Symbols = "@#%+=";
        string all = Upper + Lower + Digits + Symbols;

        var chars = new List<char>
        {
            Upper[RandomNumberGenerator.GetInt32(Upper.Length)],
            Lower[RandomNumberGenerator.GetInt32(Lower.Length)],
            Digits[RandomNumberGenerator.GetInt32(Digits.Length)],
            Symbols[RandomNumberGenerator.GetInt32(Symbols.Length)],
        };
        while (chars.Count < 16)
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);

        // Barajado Fisher-Yates para que las cuatro clases obligatorias no
        // queden siempre al principio.
        for (int i = chars.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string([.. chars]);
    }

    // ------------------------------------------------------------------
    // Cifrado que espera la receptora
    // ------------------------------------------------------------------

    private static async Task<string> EncryptForReceiverAsync(HttpClient http, string password, CancellationToken ct)
    {
        using var rsa = RSA.Create(1024);
        var parameters = rsa.ExportParameters(false);
        string modulusHex = Convert.ToHexString(parameters.Modulus!).ToLowerInvariant().TrimStart('0');

        string challengeBody = JsonSerializer.Serialize(new { PublicKey = new { key = Base64Ascii(modulusHex) } });
        using var content = VendorBody(challengeBody);
        using var response = await http.PostAsync("/ISAPI/Security/challenge?format=json", content, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"La receptora no entregó el desafío de activación: {json.Trim()}");

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Challenge", out var challenge) ||
            !challenge.TryGetProperty("key", out var keyElement) ||
            keyElement.GetString() is not { Length: > 0 } encodedChallenge)
            throw new InvalidOperationException("La receptora respondió un desafío de activación inesperado.");

        // Base64 → cadena hexadecimal (la receptora agrega a veces un '?' al final).
        string cipherHex = Encoding.ASCII.GetString(Convert.FromBase64String(encodedChallenge)).Split('?')[0].Trim();
        byte[] sessionKeyBytes = rsa.Decrypt(Convert.FromHexString(cipherHex), RSAEncryptionPadding.Pkcs1);
        string session = Encoding.ASCII.GetString(sessionKeyBytes);
        if (session.Length < 32)
            throw new InvalidOperationException("La clave de sesión de la receptora es más corta de lo esperado.");

        byte[] aesKey = Convert.FromHexString(session[..32]);
        string prefix = EcbHex(session[..16], aesKey);
        string body = EcbHex(password, aesKey);
        return Base64Ascii(prefix + body);
    }

    /// <summary>AES-128 ECB con relleno de ceros; entrada ASCII y salida hexadecimal.</summary>
    private static string EcbHex(string text, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.Zeros;
        aes.Key = key;
        using var encryptor = aes.CreateEncryptor();
        byte[] plain = Encoding.ASCII.GetBytes(text);
        return Convert.ToHexString(encryptor.TransformFinalBlock(plain, 0, plain.Length)).ToLowerInvariant();
    }

    private static string Base64Ascii(string value) => Convert.ToBase64String(Encoding.ASCII.GetBytes(value));
}

/// <summary>
/// Estado de la receptora local: si responde, si el servidor tiene su
/// credencial y si insistir tiene sentido. <paramref name="Final"/> es true
/// cuando ya no hay nada que reintentar: o quedó lista, o la activó alguien
/// más con una contraseña que este servidor no puede averiguar.
/// </summary>
public sealed record LocalReceiverState(bool Present, bool Ready, string? Message, bool Final = false);
