using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// </summary>
public sealed class LocalIpReceiverService(
    IConfiguration config,
    EmbeddedPostgres postgres,
    CredentialProtector protector,
    ILogger<LocalIpReceiverService> logger)
{
    /// <summary>Nombre de la cuenta que crea la activación (la receptora no admite otro).</summary>
    public const string AdminUser = "admin";

    private readonly SemaphoreSlim _gate = new(1, 1);

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
            return new LocalReceiverState(false, false, "La receptora local está desactivada por configuración.");

        await _gate.WaitAsync(ct);
        try
        {
            if (HasCredentials)
                return new LocalReceiverState(true, true, null);

            using var http = CreateClient();
            if (!await IsReachableAsync(http, ct))
                return new LocalReceiverState(false, false,
                    $"No hay una receptora de paneles escuchando en {Host}:{Port}.");

            string password = GeneratePassword();
            string encrypted = await EncryptForReceiverAsync(http, password, ct);

            using var request = new HttpRequestMessage(HttpMethod.Put, "/ISAPI/System/activate?format=json")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { ActivateInfo = new { password = encrypted } }),
                    Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                SavePassword(password);
                logger.LogInformation(
                    "Receptora de paneles activada automáticamente en {Host}:{Port}; su credencial queda cifrada en {Path} " +
                    "y la administra el servidor (nadie necesita conocerla).", Host, Port, SecretPath);
                return new LocalReceiverState(true, true, null);
            }

            if (body.Contains("hasActivated", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "La receptora de {Host}:{Port} ya estaba activada antes de instalar el sistema y su contraseña no la " +
                    "conoce el servidor. Escríbala una vez al agregar el panel, o reinstale la receptora para que el " +
                    "sistema la active solo.", Host, Port);
                return new LocalReceiverState(true, false,
                    "La receptora ya estaba activada con una contraseña que el sistema no conoce.");
            }

            logger.LogWarning("La receptora de {Host}:{Port} rechazó la activación automática: {Body}", Host, Port, body.Trim());
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
        const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string Lower = "abcdefghijkmnopqrstuvwxyz";
        const string Digits = "23456789";
        const string Symbols = "@#%+=?";
        string all = Upper + Lower + Digits + Symbols;

        var chars = new List<char>
        {
            Upper[RandomNumberGenerator.GetInt32(Upper.Length)],
            Lower[RandomNumberGenerator.GetInt32(Lower.Length)],
            Digits[RandomNumberGenerator.GetInt32(Digits.Length)],
            Symbols[RandomNumberGenerator.GetInt32(Symbols.Length)],
        };
        while (chars.Count < 24)
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
        using var content = new StringContent(challengeBody, Encoding.UTF8, "application/json");
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

/// <summary>Estado de la receptora local: si responde y si el servidor tiene su credencial.</summary>
public sealed record LocalReceiverState(bool Present, bool Ready, string? Message);
