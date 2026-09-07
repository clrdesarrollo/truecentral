using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace TrueCentralVms.Server.Services.Licensing;

// ---------------------------------------------------------------------------
// Archivo de licencia (.lic) tal como lo emite license_service_server (v2):
//   { schema_version, payload, signed_payload, signature, public_key, algorithm }
// El VMS verifica la firma Ed25519 sobre los BYTES EXACTOS de signed_payload
// (base64 del JSON canónico) con la clave pública compilada, y recién después
// parsea esos mismos bytes: así no depende de reproducir la canonicalización
// de Python ni confía en el `payload` legible (que solo es informativo).
// ---------------------------------------------------------------------------

public sealed class LicenseEnvelope
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
    [JsonPropertyName("signed_payload")] public string? SignedPayload { get; set; }
    [JsonPropertyName("signature")] public string? Signature { get; set; }
    [JsonPropertyName("public_key")] public string? PublicKey { get; set; }
    [JsonPropertyName("algorithm")] public string? Algorithm { get; set; }
}

public sealed class LicensePayload
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
    [JsonPropertyName("license_uuid")] public string? LicenseUuid { get; set; }
    [JsonPropertyName("license_key")] public string LicenseKey { get; set; } = "";
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("product")] public LicenseProduct? Product { get; set; }
    [JsonPropertyName("customer")] public LicenseCustomer? Customer { get; set; }
    [JsonPropertyName("package")] public string? Package { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, JsonElement> Features { get; set; } = new();
    [JsonPropertyName("addons")] public List<LicenseAddon> Addons { get; set; } = [];
    [JsonPropertyName("validation")] public LicenseValidation? Validation { get; set; }
    [JsonPropertyName("issued_at")] public string? IssuedAt { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
    [JsonPropertyName("hardware_id")] public string? HardwareId { get; set; }
    [JsonPropertyName("max_activations")] public int MaxActivations { get; set; }
    [JsonPropertyName("generated_at")] public string? GeneratedAt { get; set; }

    public DateTime? IssuedAtUtc => ParseUtc(IssuedAt);
    public DateTime? ExpiresAtUtc => ParseUtc(ExpiresAt);
    public DateTime? GeneratedAtUtc => ParseUtc(GeneratedAt);

    public bool IsOffline => string.Equals(Validation?.Mode, "OFFLINE", StringComparison.OrdinalIgnoreCase);
    public int HeartbeatDays => Math.Max(1, Validation?.HeartbeatIntervalDays ?? 7);
    public int GraceDays => Math.Max(0, Validation?.GracePeriodDays ?? 30);

    public static DateTime? ParseUtc(string? iso) =>
        DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var value)
            ? value.UtcDateTime
            : null;
}

public sealed class LicenseProduct
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class LicenseCustomer
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("tax_id")] public string? TaxId { get; set; }
}

public sealed class LicenseAddon
{
    [JsonPropertyName("license_key")] public string LicenseKey { get; set; } = "";
    [JsonPropertyName("package")] public string? Package { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, JsonElement> Features { get; set; } = new();
}

public sealed class LicenseValidation
{
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("heartbeat_interval_days")] public int HeartbeatIntervalDays { get; set; }
    [JsonPropertyName("grace_period_days")] public int GracePeriodDays { get; set; }
}

/// <summary>Valores efectivos de las características (licencia o prueba).</summary>
public sealed class LicenseFeatureSet
{
    private readonly Dictionary<string, object> _values;

    public LicenseFeatureSet(IReadOnlyDictionary<string, object> values)
    {
        _values = new Dictionary<string, object>(values, StringComparer.Ordinal);
    }

    public static LicenseFeatureSet Empty { get; } = new(new Dictionary<string, object>());

    public static LicenseFeatureSet FromPayload(LicensePayload payload)
    {
        var values = new Dictionary<string, object>();
        foreach (var (key, element) in payload.Features)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.True: values[key] = true; break;
                case JsonValueKind.False: values[key] = false; break;
                case JsonValueKind.Number when element.TryGetInt64(out long n): values[key] = (int)Math.Clamp(n, int.MinValue, int.MaxValue); break;
                case JsonValueKind.String: values[key] = element.GetString() ?? ""; break;
            }
        }
        return new LicenseFeatureSet(values);
    }

    public bool IsEnabled(string key) => _values.TryGetValue(key, out var v) && v is true;

    /// <summary>Cupo de una característica entera; 0 si no viene en la licencia.</summary>
    public int Quota(string key) => _values.TryGetValue(key, out var v) && v is int n ? n : 0;

    public bool Has(string key) => _values.ContainsKey(key);
}

/// <summary>Resultado de verificar un archivo .lic: el payload firmado o el motivo del rechazo.</summary>
public sealed record LicenseVerification(LicensePayload? Payload, string? Error)
{
    public bool Ok => Payload is not null;
    public static LicenseVerification Fail(string error) => new(null, error);
}

public static class LicenseFileVerifier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Verifica la firma del archivo con <paramref name="publicKeyBase64"/> y
    /// devuelve el payload parseado desde los bytes firmados. No valida
    /// vigencia ni hardware: eso lo decide <see cref="LicenseService"/>.
    /// </summary>
    public static LicenseVerification Verify(string licenseFileJson, string publicKeyBase64)
    {
        LicenseEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize<LicenseEnvelope>(licenseFileJson, Json); }
        catch (JsonException) { return LicenseVerification.Fail("El archivo de licencia no es un JSON válido."); }
        if (envelope is null)
            return LicenseVerification.Fail("El archivo de licencia está vacío.");
        if (envelope.SchemaVersion != LicensingConstants.SupportedLicenseSchema || string.IsNullOrEmpty(envelope.SignedPayload))
            return LicenseVerification.Fail(
                $"Versión de archivo de licencia no soportada ({envelope.SchemaVersion}); este VMS requiere la versión {LicensingConstants.SupportedLicenseSchema}.");
        if (!string.Equals(envelope.Algorithm, "Ed25519", StringComparison.OrdinalIgnoreCase))
            return LicenseVerification.Fail($"Algoritmo de firma no soportado: {envelope.Algorithm}.");
        if (!string.IsNullOrEmpty(envelope.PublicKey)
            && !string.Equals(envelope.PublicKey.Trim(), publicKeyBase64.Trim(), StringComparison.Ordinal))
            return LicenseVerification.Fail(
                "El archivo fue firmado por otro servidor de licencias (la clave pública no coincide con la de este producto).");

        byte[] signed, signature, publicKey;
        try
        {
            signed = Convert.FromBase64String(envelope.SignedPayload);
            signature = Convert.FromBase64String(envelope.Signature ?? "");
            publicKey = Convert.FromBase64String(publicKeyBase64.Trim());
        }
        catch (FormatException)
        {
            return LicenseVerification.Fail("El archivo de licencia está corrupto (base64 inválido).");
        }
        if (publicKey.Length != 32)
            return LicenseVerification.Fail("La clave pública compilada en el VMS no es una clave Ed25519 válida.");

        try
        {
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            signer.BlockUpdate(signed, 0, signed.Length);
            if (!signer.VerifySignature(signature))
                return LicenseVerification.Fail("La firma del archivo de licencia no es válida (archivo alterado o de otro producto).");
        }
        catch (Exception ex)
        {
            return LicenseVerification.Fail($"No se pudo verificar la firma: {ex.Message}");
        }

        LicensePayload? payload;
        try { payload = JsonSerializer.Deserialize<LicensePayload>(signed, Json); }
        catch (JsonException) { return LicenseVerification.Fail("El contenido firmado de la licencia no se pudo interpretar."); }
        if (payload is null || string.IsNullOrEmpty(payload.LicenseKey))
            return LicenseVerification.Fail("El contenido firmado de la licencia está incompleto.");
        if (!string.Equals(payload.Product?.Code, Core.Contracts.LicenseFeatures.ProductCode, StringComparison.OrdinalIgnoreCase))
            return LicenseVerification.Fail($"La licencia es del producto '{payload.Product?.Code}', no de TrueCentral VMS.");
        return new LicenseVerification(payload, null);
    }
}
