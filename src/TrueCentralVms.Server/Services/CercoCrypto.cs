using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Primitivas de la autenticación mutua con los paneles de cerco. DEBEN coincidir
/// byte a byte con el firmware (cerco-firmware/src/main.cpp y crypto.h): mismas
/// construcciones HMAC-SHA256 y la misma serialización canónica de args.
/// </summary>
public static class CercoCrypto
{
    public static byte[] Hmac(byte[] key, ReadOnlySpan<byte> msg) => HMACSHA256.HashData(key, msg.ToArray());

    public static string HmacB64(byte[] key, string msg) =>
        Convert.ToBase64String(Hmac(key, Encoding.UTF8.GetBytes(msg)));

    public static bool VerifyB64(byte[] key, string msg, string? macB64)
    {
        if (string.IsNullOrEmpty(macB64)) return false;
        byte[] expected = Hmac(key, Encoding.UTF8.GetBytes(msg));
        byte[] got;
        try { got = Convert.FromBase64String(macB64); } catch { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, got);
    }

    /// <summary>proof = HMAC(psk, prefix | id | a | b). prefix 'S' (servidor) o 'C' (panel).</summary>
    public static byte[] Proof(byte[] psk, char prefix, string deviceId, byte[] a, byte[] b)
    {
        var buf = new byte[1 + Encoding.UTF8.GetByteCount(deviceId) + a.Length + b.Length];
        int p = 0;
        buf[p++] = (byte)prefix;
        p += Encoding.UTF8.GetBytes(deviceId, 0, deviceId.Length, buf, p);
        Buffer.BlockCopy(a, 0, buf, p, a.Length); p += a.Length;
        Buffer.BlockCopy(b, 0, buf, p, b.Length);
        return Hmac(psk, buf);
    }

    /// <summary>sk = HMAC(psk, nonce_c | nonce_s).</summary>
    public static byte[] SessionKey(byte[] psk, byte[] nonceC, byte[] nonceS)
    {
        var buf = new byte[nonceC.Length + nonceS.Length];
        Buffer.BlockCopy(nonceC, 0, buf, 0, nonceC.Length);
        Buffer.BlockCopy(nonceS, 0, buf, nonceC.Length, nonceS.Length);
        return Hmac(psk, buf);
    }

    public static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    /// <summary>
    /// Serialización canónica de args idéntica al firmware: claves ordenadas asc,
    /// "k=v" unidos por ",", bool→1/0, entero→decimal, string→tal cual.
    /// Sin args → cadena vacía.
    /// </summary>
    public static string CanonArgs(JsonObject? args)
    {
        if (args is null || args.Count == 0) return "";
        var parts = new List<string>(args.Count);
        foreach (var key in args.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
        {
            JsonNode? v = args[key];
            string sv;
            if (v is JsonValue jv && jv.TryGetValue(out bool b)) sv = b ? "1" : "0";
            else if (v is JsonValue jn && jn.TryGetValue(out long l)) sv = l.ToString(System.Globalization.CultureInfo.InvariantCulture);
            else sv = v?.ToString() ?? "";
            parts.Add($"{key}={sv}");
        }
        return string.Join(",", parts);
    }
}
