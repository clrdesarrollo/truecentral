using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrueCentralVms.Server.Services.Backup;

/// <summary>
/// Cabecera de un respaldo .tcbak: va EN CLARO (sin datos sensibles) para poder listar los respaldos y saber con qué
/// llave se abren sin descifrarlos. Todo lo demás del archivo va cifrado.
/// </summary>
public sealed record BackupHeader
{
    public int Format { get; init; } = 1;
    public DateTime CreatedUtc { get; init; }
    /// <summary><see cref="BackupKinds"/>.</summary>
    public string Kind { get; init; } = BackupKinds.Daily;
    /// <summary>"password" = contraseña de respaldo (se restaura en cualquier equipo); "machine" = llave de datos de ESTE equipo.</summary>
    public string KeyMode { get; init; } = "password";
    public int Iterations { get; init; }
    public string Salt { get; init; } = "";
    /// <summary>Comprobante de la llave: distingue "contraseña incorrecta" de "archivo dañado".</summary>
    public string KeyCheck { get; init; } = "";
    public string NoncePrefix { get; init; } = "";
    public int ChunkSize { get; init; }
    public string ServerVersion { get; init; } = "";
    public string? LastMigration { get; init; }
    public string Machine { get; init; } = "";
    public bool IncludesAnpr { get; init; }
}

public static class BackupKinds
{
    public const string Daily = "diario";
    public const string Manual = "manual";
    public const string PreUpdate = "antes-de-actualizar";

    public static readonly string[] All = [Daily, Manual, PreUpdate];
}

/// <summary>
/// Formato .tcbak: "TCBAK" + versión, largo y JSON de la cabecera, y después el contenido (un ZIP) cifrado con
/// AES-256-GCM en bloques de 1 MiB. Cada bloque lleva su propia etiqueta y, como dato asociado, el hash de la cabecera,
/// su número y si es el último: no se puede cambiar la cabecera, reordenar, quitar ni truncar bloques sin que se note.
/// La llave sale de la contraseña de respaldo (PBKDF2-SHA256, 600.000 iteraciones) o, sin contraseña configurada, de la
/// llave de datos del equipo (HKDF): ese respaldo solo se abre en este mismo equipo.
/// </summary>
public static class BackupFormat
{
    private static readonly byte[] Magic = "TCBAK"u8.ToArray();
    private const byte Version = 1;
    public const int DefaultChunkSize = 1024 * 1024;
    public const int PasswordIterations = 600_000;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] DeriveFromPassword(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    public static byte[] DeriveFromMachineKey(byte[] dataKey, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt, "tcvms-backup-v1"u8.ToArray());

    private static string KeyCheckOf(byte[] key) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, "tcvms-backup-key-check"u8)[..16]);

    /// <summary>Cabecera nueva con sal y prefijo de nonce al azar; devuelve también la llave de cifrado.</summary>
    public static (BackupHeader Header, byte[] Key) NewHeader(BackupHeader template, string? password, byte[]? machineKey)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] key = password is not null
            ? DeriveFromPassword(password, salt, PasswordIterations)
            : DeriveFromMachineKey(machineKey ?? throw new ArgumentNullException(nameof(machineKey)), salt);
        var header = template with
        {
            Format = 1,
            KeyMode = password is not null ? "password" : "machine",
            Iterations = password is not null ? PasswordIterations : 0,
            Salt = Convert.ToBase64String(salt),
            KeyCheck = KeyCheckOf(key),
            NoncePrefix = Convert.ToBase64String(RandomNumberGenerator.GetBytes(4)),
            ChunkSize = DefaultChunkSize,
        };
        return (header, key);
    }

    /// <summary>Llave de un respaldo existente; null si la contraseña (o la llave del equipo) no es la que lo cifró.</summary>
    public static byte[]? KeyFor(BackupHeader header, string? password, byte[]? machineKey)
    {
        byte[] salt = Convert.FromBase64String(header.Salt);
        byte[]? key = header.KeyMode == "password"
            ? password is null ? null : DeriveFromPassword(password, salt, header.Iterations)
            : machineKey is null ? null : DeriveFromMachineKey(machineKey, salt);
        return key is not null && CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(KeyCheckOf(key)), Convert.FromBase64String(header.KeyCheck)) ? key : null;
    }

    /// <summary>Escribe la cabecera y devuelve sus bytes (van, hasheados, en el dato asociado de cada bloque).</summary>
    public static byte[] WriteHeader(Stream output, BackupHeader header)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        output.Write(Magic);
        output.WriteByte(Version);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, json.Length);
        output.Write(len);
        output.Write(json);
        return json;
    }

    /// <summary>Lee la cabecera (sin descifrar nada). Lanza si el archivo no es un respaldo de TrueCentral.</summary>
    public static (BackupHeader Header, byte[] Raw) ReadHeader(Stream input)
    {
        Span<byte> start = stackalloc byte[10];
        input.ReadExactly(start);
        if (!start[..5].SequenceEqual(Magic) || start[5] != Version)
            throw new InvalidDataException("El archivo no es un respaldo de CLR TrueCentral VMS (o es de una versión más nueva).");
        int length = BinaryPrimitives.ReadInt32LittleEndian(start[6..]);
        if (length is <= 0 or > 64 * 1024) throw new InvalidDataException("La cabecera del respaldo está dañada.");
        byte[] raw = new byte[length];
        input.ReadExactly(raw);
        var header = JsonSerializer.Deserialize<BackupHeader>(raw, Json)
            ?? throw new InvalidDataException("La cabecera del respaldo está dañada.");
        return (header, raw);
    }

    public static BackupHeader ReadHeader(string path)
    {
        using var file = File.OpenRead(path);
        return ReadHeader(file).Header;
    }

    internal static byte[] Nonce(byte[] prefix, long index)
    {
        byte[] nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4), index);
        return nonce;
    }

    internal static byte[] Aad(byte[] headerHash, long index, bool final)
    {
        byte[] aad = new byte[headerHash.Length + 9];
        headerHash.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(headerHash.Length), index);
        aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }

    /// <summary>Flujo de escritura que cifra por bloques. El último bloque (aunque quede vacío) se escribe al cerrarlo.</summary>
    public sealed class EncryptingStream(Stream output, byte[] key, BackupHeader header, byte[] rawHeader) : Stream
    {
        private readonly AesGcm _aes = new(key, TagSize);
        private readonly byte[] _prefix = Convert.FromBase64String(header.NoncePrefix);
        private readonly byte[] _headerHash = SHA256.HashData(rawHeader);
        private readonly byte[] _buffer = new byte[header.ChunkSize];
        private int _filled;
        private long _index;
        private long _written;
        private bool _closed;

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                int take = Math.Min(data.Length, _buffer.Length - _filled);
                data[..take].CopyTo(_buffer.AsSpan(_filled));
                _filled += take;
                _written += take;
                data = data[take..];
                if (_filled == _buffer.Length) EmitChunk(final: false);
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        private void EmitChunk(bool final)
        {
            byte[] cipher = new byte[_filled];
            byte[] tag = new byte[TagSize];
            _aes.Encrypt(Nonce(_prefix, _index), _buffer.AsSpan(0, _filled), cipher, tag, Aad(_headerHash, _index, final));
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, _filled);
            output.Write(len);
            output.Write(cipher);
            output.Write(tag);
            _index++;
            _filled = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                EmitChunk(final: true);
                output.Flush();
                _aes.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Descifra todo el contenido hacia <paramref name="output"/>, comprobando cada bloque y que no falte el final.</summary>
    public static void Decrypt(Stream input, byte[] key, BackupHeader header, byte[] rawHeader, Stream output)
    {
        using var aes = new AesGcm(key, TagSize);
        byte[] prefix = Convert.FromBase64String(header.NoncePrefix);
        byte[] headerHash = SHA256.HashData(rawHeader);
        Span<byte> lenBytes = stackalloc byte[4];
        byte[] tag = new byte[TagSize];
        for (long index = 0; ; index++)
        {
            if (input.ReadAtLeast(lenBytes, 4, throwOnEndOfStream: false) < 4)
                throw new InvalidDataException("El respaldo está incompleto (el archivo se cortó).");
            int length = BinaryPrimitives.ReadInt32LittleEndian(lenBytes);
            if (length < 0 || length > header.ChunkSize) throw new InvalidDataException("El respaldo está dañado.");
            byte[] cipher = new byte[length];
            input.ReadExactly(cipher);
            input.ReadExactly(tag);
            byte[] plain = new byte[length];
            // Los bloques llenos nunca son el último: al cerrar siempre se escribe uno más corto (aunque sea vacío).
            bool final = length < header.ChunkSize;
            try { aes.Decrypt(Nonce(prefix, index), cipher, tag, plain, Aad(headerHash, index, final)); }
            catch (AuthenticationTagMismatchException)
            {
                throw new InvalidDataException("El respaldo está dañado o fue modificado.");
            }
            output.Write(plain);
            if (final) return;
        }
    }

    /// <summary>Texto de la cabecera para la bitácora y el panel.</summary>
    public static string Describe(BackupHeader h) =>
        $"{h.Kind}, {h.CreatedUtc.ToLocalTime():dd-MM-yyyy HH:mm}, versión {h.ServerVersion}, " +
        (h.KeyMode == "password" ? "cifrado con la contraseña de respaldo" : "cifrado con la llave de este equipo");

    internal static string HeaderJson(BackupHeader h) => Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(h, Json));
}
