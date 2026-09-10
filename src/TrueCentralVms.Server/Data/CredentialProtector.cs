using System.Security.Cryptography;

namespace TrueCentralVms.Server.Data;

/// <summary>
/// Cifrado en reposo de las credenciales de los dispositivos: AES-256-GCM con
/// una llave local generada en el primer uso y guardada junto a los datos
/// (pgdata\tcvms-data.key, mismo criterio que tcvms-pg.secret). La llave viaja
/// con el directorio de datos en respaldos/migraciones de servidor.
/// Formato del blob: [12 bytes nonce][16 bytes tag][cifrado].
/// </summary>
public sealed class CredentialProtector(EmbeddedPostgres postgres)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Lock _sync = new();
    private byte[]? _key;

    private byte[] Key
    {
        get
        {
            lock (_sync)
            {
                if (_key is not null) return _key;
                string path = Path.Combine(postgres.DataDirectory, "tcvms-data.key");
                if (File.Exists(path))
                {
                    _key = Convert.FromBase64String(File.ReadAllText(path).Trim());
                    if (_key.Length != 32)
                        throw new InvalidOperationException($"La llave de datos '{path}' está corrupta (largo inválido).");
                }
                else
                {
                    _key = RandomNumberGenerator.GetBytes(32);
                    File.WriteAllText(path, Convert.ToBase64String(_key));
                }
                return _key;
            }
        }
    }

    public byte[] Protect(string plaintext) =>
        ProtectBytes(System.Text.Encoding.UTF8.GetBytes(plaintext));

    /// <summary>
    /// Igual que <see cref="Protect(string)"/> pero para datos que NO son
    /// texto: la foto del rostro es un JPEG, y pasarla por UTF-8 la rompería.
    /// </summary>
    public byte[] ProtectBytes(byte[] plain)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];

        using var aes = new AesGcm(Key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        byte[] blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return blob;
    }

    public string Unprotect(byte[] blob) =>
        System.Text.Encoding.UTF8.GetString(UnprotectBytes(blob));

    /// <summary>Descifra datos que no son texto (ver <see cref="ProtectBytes"/>).</summary>
    public byte[] UnprotectBytes(byte[] blob)
    {
        if (blob.Length < NonceSize + TagSize)
            throw new InvalidOperationException("Credencial cifrada corrupta.");

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        byte[] plain = new byte[cipher.Length];

        using var aes = new AesGcm(Key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
