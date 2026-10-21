using System.Diagnostics;
using Npgsql;

namespace TrueCentralVms.Server.Data;

/// <summary>
/// PostgreSQL embebido: el servidor arranca su propia instancia local con los
/// binarios distribuidos junto al sistema (tools\postgres\pgsql) y la detiene
/// al salir. No requiere instalación ni servicio de Windows: el clúster de
/// datos (pgdata) y sus credenciales viven junto al servidor.
///
/// Primer arranque: initdb crea el clúster con una contraseña generada (se
/// guarda en pgdata\tcvms-pg.secret) y escucha solo en 127.0.0.1. Arranques
/// siguientes: si quedó una instancia corriendo (p. ej. el server se cerró de
/// golpe) se reutiliza en vez de fallar.
///
/// El SERVIDOR no usa el superusuario: entra con <see cref="AppRole"/>, dueño de la base del producto y de sus
/// tablas, sin privilegios de superusuario (un superusuario puede ejecutar comandos de Windows desde SQL con
/// COPY ... TO PROGRAM y leer cualquier archivo; si algún día hubiera una inyección SQL, no pasaría de la base).
/// El superusuario queda solo para crear la base y mantener el rol, en cada arranque (ver EnsureAppRoleAsync).
/// </summary>
public sealed class EmbeddedPostgres
{
    public const string DatabaseName = "truecentral_vms";
    private const string SuperUser = "postgres";
    /// <summary>Rol con que trabaja el servidor: LOGIN y dueño de truecentral_vms, nada más.</summary>
    public const string AppRole = "tcvms_app";

    /// <summary>Variables de entorno de libpq que no deben interferir con la instancia embebida.</summary>
    private static readonly string[] LibpqEnvVars =
        ["PGDATA", "PGHOST", "PGPORT", "PGUSER", "PGPASSWORD", "PGDATABASE", "PGOPTIONS", "PGSERVICE", "PGSSLMODE", "PGLOCALEDIR", "PGSYSCONFDIR"];

    /// <summary>
    /// Puerto por defecto del clúster embebido. Deliberadamente lejos del 5432
    /// de PostgreSQL (y del 25480 del producto videowall): esta base es privada
    /// del sistema (solo 127.0.0.1) y no debe chocar ni confundirse con otra
    /// instalación del equipo.
    /// </summary>
    public const int DefaultPort = 25490;

    private readonly string _binDir;
    private readonly string _dataDir;
    private int _port;
    private string? _password;
    private string? _appPassword;

    public EmbeddedPostgres(IConfiguration config, string contentRoot)
    {
        // En equipos que YA tienen PostgreSQL instalado suele haber variables
        // PG* definidas para esa instalación. Npgsql las usa para todo lo que la
        // cadena de conexión no fije explícitamente: con PGOPTIONS="-c
        // search_path=..." el esquema queda apuntando a otro lado y el servidor
        // no encuentra sus propias tablas. Se limpian para TODO el proceso (no
        // solo para los procesos hijos): esta instancia es dueña de su clúster.
        foreach (string v in LibpqEnvVars)
            Environment.SetEnvironmentVariable(v, null);

        _port = config.GetValue("Database:PgPort", DefaultPort);
        // La ruta admite variables de entorno (%ProgramData%\CLRTrueCentralVMS\pgdata):
        // instalado como servicio, los datos viven fuera de Program Files.
        _dataDir = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(config["Database:PgDataDir"] ?? "pgdata"), contentRoot);
        _binDir = FindBinDir(config) ?? throw new InvalidOperationException(
            "No se encontraron los binarios de PostgreSQL embebido (pg_ctl.exe). " +
            @"Copie la carpeta pgsql (bin\lib\share) en tools\postgres — junto al ejecutable o en la raíz del repositorio — " +
            "o indique la ruta de bin en Database:PgBinDir.");
    }

    /// <summary>Directorio de datos del clúster (ahí también viven los secretos del servidor).</summary>
    public string DataDirectory => _dataDir;

    /// <summary>Puerto efectivo del clúster (el del postgresql.conf si ya existía).</summary>
    public int Port => _port;

    private string SecretPath => Path.Combine(_dataDir, "tcvms-pg.secret");

    private string AppSecretPath => Path.Combine(_dataDir, "tcvms-app.secret");

    /// <summary>
    /// Conexión del servidor: con el rol propio <see cref="AppRole"/>. Solo si no se pudo preparar el rol (se avisa en
    /// el log y se reintenta en el próximo arranque) cae al superusuario, para no dejar el sistema sin base.
    /// </summary>
    public string ConnectionString => _appPassword is { } appPassword
        ? BuildConnectionString(DatabaseName, AppRole, appPassword)
        : BuildConnectionString(DatabaseName);

    /// <summary>¿El servidor trabaja con el rol propio (true) o cayó al superusuario (false)?</summary>
    public bool UsesAppRole => _appPassword is not null;

    /// <summary>Carpeta raíz de los datos del sistema (la que contiene pgdata, anpr, workflows, license, backups…).</summary>
    public string DataRoot => Path.GetDirectoryName(_dataDir) ?? _dataDir;

    /// <summary>Ruta de una herramienta de PostgreSQL (pg_dump.exe, pg_restore.exe…) de los binarios embebidos.</summary>
    public string ToolPath(string exe) => Path.Combine(_binDir, exe);

    /// <summary>Con qué rol se respalda: el propio (dueño de todo lo de la base) o, si cayó, el superusuario.</summary>
    public (string User, string Password) DumpCredentials => _appPassword is { } app
        ? (AppRole, app)
        : (SuperUser, _password ?? throw new InvalidOperationException("PostgreSQL embebido aún no fue iniciado."));

    /// <summary>Superusuario: SOLO para restaurar y probar restauraciones (crear, renombrar y borrar bases).</summary>
    public (string User, string Password) AdminCredentials =>
        (SuperUser, _password ?? throw new InvalidOperationException("PostgreSQL embebido aún no fue iniciado."));

    /// <summary>Conexión de superusuario, sin pool, a <paramref name="database"/> (restauración y su prueba).</summary>
    public string AdminConnectionString(string database) => BuildConnectionString(database, pooling: false);

    private string BuildConnectionString(string database, string? user = null, string? password = null, bool pooling = true) => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1",
        Port = _port,
        Username = user ?? SuperUser,
        Password = password ?? _password ?? throw new InvalidOperationException("PostgreSQL embebido aún no fue iniciado."),
        Database = database,
        Timeout = 15,
        // El clúster acepta 100 conexiones: el pool del servidor no las agota (quedan para el superusuario,
        // el supervisor y una consola de diagnóstico).
        MaxPoolSize = 60,
        // Las conexiones de arranque y de mantenimiento del rol (superusuario) no quedan abiertas en un pool.
        Pooling = pooling,
    }.ConnectionString;

    /// <summary>Busca los binarios como el resto de las herramientas: tools\ junto al exe, subiendo por el árbol (repo).</summary>
    private static string? FindBinDir(IConfiguration config)
    {
        string? configured = config["Database:PgBinDir"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));

        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "tools", "postgres", "pgsql", "bin");
            if (File.Exists(Path.Combine(candidate, "pg_ctl.exe")))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public async Task StartAsync(ILogger logger, CancellationToken ct = default)
    {
        AdoptClusterPortIfExists(logger);
        InitDbIfNeeded(logger);
        WriteTuning(logger);

        _password ??= File.Exists(SecretPath)
            ? (await File.ReadAllTextAsync(SecretPath, ct)).Trim()
            : throw new InvalidOperationException(
                $"El clúster '{_dataDir}' existe pero falta su archivo de credenciales '{SecretPath}'. " +
                "Restaure el archivo o elimine el directorio de datos para reinicializar (se perdería la configuración).");

        if (IsRunning())
        {
            // Lo recargable del ajuste se aplica ya; la memoria compartida, al próximo reinicio del clúster.
            RunTool("pg_ctl.exe", ["reload", "-D", _dataDir], timeoutSeconds: 30);
            logger.LogInformation("PostgreSQL embebido ya estaba corriendo en el puerto {Port}; se reutiliza " +
                "(shared_buffers cambia recién cuando el clúster se reinicie).", _port);
        }
        else
            StartServer();

        await WaitUntilReadyAsync(ct);
        await EnsureDatabaseExistsAsync(ct);
        await EnsureAppRoleAsync(logger, ct);
        logger.LogInformation("PostgreSQL embebido listo en 127.0.0.1:{Port} (datos en {DataDir}).", _port, _dataDir);
    }

    /// <summary>Detención ordenada al salir el servidor (modo fast: corta conexiones y hace checkpoint).</summary>
    public void Stop(ILogger logger)
    {
        if (_password is null)
            return; // nunca llegó a iniciarse
        try
        {
            NpgsqlConnection.ClearAllPools();
            var (exitCode, output) = RunTool("pg_ctl.exe", ["stop", "-D", _dataDir, "-m", "fast", "-w", "-t", "30"], timeoutSeconds: 45);
            if (exitCode == 0)
                logger.LogInformation("PostgreSQL embebido detenido.");
            else
                logger.LogWarning("pg_ctl stop terminó con código {Code}: {Output}", exitCode, output.Trim());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo detener PostgreSQL embebido.");
        }
    }

    /// <summary>
    /// Con un clúster ya creado manda el puerto de SU postgresql.conf: es donde
    /// escucha de verdad el postmaster que levanta pg_ctl. Así, cambiar el
    /// valor por defecto (o Database:PgPort) no deja al servidor buscando la
    /// base en un puerto donde no está.
    /// </summary>
    private void AdoptClusterPortIfExists(ILogger logger)
    {
        if (!File.Exists(Path.Combine(_dataDir, "PG_VERSION")))
            return;
        if (ReadClusterPort() is not int clusterPort || clusterPort == _port)
            return;

        logger.LogWarning(
            "El clúster de {DataDir} ya existe y escucha en el puerto {ClusterPort}: se usa ese y se ignora " +
            "Database:PgPort={ConfiguredPort} (para cambiarlo hay que editar también 'port' en su postgresql.conf).",
            _dataDir, clusterPort, _port);
        _port = clusterPort;
    }

    /// <summary>Último "port = N" efectivo de postgresql.conf (las líneas comentadas no cuentan).</summary>
    private int? ReadClusterPort()
    {
        try
        {
            int? found = null;
            foreach (string raw in File.ReadLines(Path.Combine(_dataDir, "postgresql.conf")))
            {
                string line = raw.Trim();
                if (!line.StartsWith("port", StringComparison.OrdinalIgnoreCase))
                    continue;
                string rest = line[4..].TrimStart();
                if (!rest.StartsWith('='))
                    continue;
                string value = rest[1..].Trim();
                int comment = value.IndexOf('#');
                if (comment >= 0)
                    value = value[..comment].Trim();
                if (int.TryParse(value, out int parsed) && parsed > 0)
                    found = parsed; // gana la última, como hace PostgreSQL
            }
            return found;
        }
        catch
        {
            return null; // conf ilegible: se mantiene el puerto configurado
        }
    }

    private void InitDbIfNeeded(ILogger logger)
    {
        if (File.Exists(Path.Combine(_dataDir, "PG_VERSION")))
            return;
        if (Directory.Exists(_dataDir) && Directory.EnumerateFileSystemEntries(_dataDir).Any())
            throw new InvalidOperationException(
                $"El directorio de datos '{_dataDir}' existe pero está incompleto (sin PG_VERSION; quizás un initdb interrumpido). " +
                "Elimínelo para que se reinicialice.");

        logger.LogInformation("Inicializando el clúster de PostgreSQL embebido en {DataDir}...", _dataDir);

        // La contraseña del superusuario se genera una sola vez y queda junto a
        // los datos: si se borra pgdata se borra todo el clúster, credencial incluida.
        string password = Guid.NewGuid().ToString("N");
        string pwFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(pwFile, password);
        try
        {
            var (exitCode, output) = RunTool("initdb.exe",
                ["-D", _dataDir, "-U", SuperUser, "-E", "UTF8", "--locale=C",
                 "-A", "scram-sha-256", $"--pwfile={pwFile}", "--data-checksums"],
                timeoutSeconds: 180);
            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"initdb falló (código {exitCode}): {output.Trim()}{DescribeStartupFailure(exitCode)}");
        }
        finally
        {
            try { File.Delete(pwFile); } catch { /* mejor esfuerzo */ }
        }

        File.WriteAllText(SecretPath, password);
        _password = password;

        // Solo loopback, puerto propio y logs rotativos dentro del clúster.
        File.AppendAllText(Path.Combine(_dataDir, "postgresql.conf"), $"""

            # --- CLR TrueCentral VMS (generado automáticamente; no editar a mano) ---
            listen_addresses = '127.0.0.1'
            port = {_port}
            unix_socket_directories = ''
            logging_collector = on
            log_directory = 'log'
            log_filename = 'postgresql-%a.log'
            log_rotation_age = 1d
            log_rotation_size = 0
            log_truncate_on_rotation = on
            """);
    }

    private const string TuningFile = "tcvms-tuning.conf";

    /// <summary>
    /// Ajuste de rendimiento y de registros, regenerado en CADA arranque en su propio archivo (incluido al final de
    /// postgresql.conf, así manda sobre lo de initdb): llega también a los clústeres ya instalados y sigue a la máquina
    /// si le agregan memoria. La memoria se reparte según la RAM del equipo, como HikCentral (perfiles ≤4, ≤8 y más de
    /// 8 GB), pero con tope: en este equipo también corren MediaMTX, FFmpeg, los SDK y el propio servidor.
    /// </summary>
    private void WriteTuning(ILogger logger)
    {
        long ramMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        var (sharedMb, workMb, maintenanceMb) = ramMb switch
        {
            <= 4096 => (256, 4, 64),
            <= 8192 => (512, 8, 128),
            _ => ((int)Math.Min(2048, ramMb / 4), 8, 256),
        };
        // Lo que el sistema operativo usará de caché de disco: orienta al planificador, no reserva nada.
        long cacheMb = Math.Max(512, ramMb / 2);

        string conf = $"""
            # CLR TrueCentral VMS — ajuste generado en cada arranque del servidor. NO editar: se sobrescribe.
            # Equipo con {ramMb} MB de RAM.

            # --- Memoria ---
            shared_buffers = {sharedMb}MB
            effective_cache_size = {cacheMb}MB
            work_mem = {workMb}MB
            maintenance_work_mem = {maintenanceMb}MB

            # --- Bloqueos y sesiones colgadas ---
            # Una sentencia que espera un bloqueo más de 60 s se cancela (antes esperaba para siempre).
            lock_timeout = 60s
            # Una transacción abierta y olvidada no retiene bloqueos ni impide la limpieza.
            idle_in_transaction_session_timeout = 10min

            # --- Registros (diagnóstico y auditoría) ---
            log_line_prefix = '%m [%p] %q%u@%d '
            log_connections = on
            log_disconnections = on
            log_checkpoints = on
            log_lock_waits = on
            # Consultas lentas, SIN sus valores: en los parámetros puede haber datos personales o patentes.
            log_min_duration_statement = 5s
            log_parameter_max_length = 0
            log_parameter_max_length_on_error = 0
            log_temp_files = 10MB
            log_autovacuum_min_duration = 30s

            # --- Seguridad ---
            password_encryption = scram-sha-256
            """;
        string path = Path.Combine(_dataDir, TuningFile);
        File.WriteAllText(path, conf.Replace("\n", Environment.NewLine));

        // La inclusión se agrega una sola vez y al final: lo que venga después en postgresql.conf ganaría.
        string mainConf = Path.Combine(_dataDir, "postgresql.conf");
        if (!File.ReadLines(mainConf).Any(l => l.Trim().StartsWith($"include_if_exists = '{TuningFile}'", StringComparison.Ordinal)))
            File.AppendAllText(mainConf, $"{Environment.NewLine}# Ajuste de CLR TrueCentral VMS (memoria y registros), regenerado en cada arranque.{Environment.NewLine}" +
                                         $"include_if_exists = '{TuningFile}'{Environment.NewLine}");
        logger.LogInformation("PostgreSQL embebido: ajuste para {Ram} MB de RAM (shared_buffers {Shared} MB, work_mem {Work} MB).",
            ramMb, sharedMb, workMb);
    }

    /// <summary>
    /// Crea la base del producto si no existe. Migrate() no crea bases en
    /// PostgreSQL (solo tablas), así que se hace aquí una única vez.
    /// </summary>
    private async Task EnsureDatabaseExistsAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(BuildConnectionString("postgres", pooling: false));
        await conn.OpenAsync(ct);
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", conn)
        {
            Parameters = { new NpgsqlParameter { Value = DatabaseName } },
        };
        if (await exists.ExecuteScalarAsync(ct) is null)
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{DatabaseName}\"", conn);
            await create.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Deja el rol propio listo, en CADA arranque y de forma idempotente (es también la migración de las instalaciones
    /// que hasta ahora trabajaban con el superusuario): crea el rol si falta, le fija su contraseña (la de
    /// tcvms-app.secret, que se genera la primera vez), le pasa la propiedad de la base, del esquema public y de todo lo
    /// que contiene (tablas, secuencias, vistas, funciones y tipos de las migraciones), y cierra la base a cualquier otro
    /// rol. Todo en una transacción: si algo falla no queda a medias, se avisa y este arranque sigue con el superusuario.
    /// </summary>
    private async Task EnsureAppRoleAsync(ILogger logger, CancellationToken ct)
    {
        try
        {
            string password = File.Exists(AppSecretPath) ? (await File.ReadAllTextAsync(AppSecretPath, ct)).Trim() : "";
            if (password.Length != 32 || !password.All(char.IsAsciiHexDigitLower))
            {
                password = Guid.NewGuid().ToString("N");
                await File.WriteAllTextAsync(AppSecretPath, password, ct);
            }

            await using var conn = new NpgsqlConnection(BuildConnectionString(DatabaseName, pooling: false));
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            // La contraseña es hexadecimal (validada arriba): se puede escribir literal en el DDL, que no admite parámetros.
            const string roleOptions = "LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS INHERIT CONNECTION LIMIT 80";
            string sql = $$"""
                DO $tcvms$
                DECLARE r record;
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{AppRole}}') THEN
                    CREATE ROLE {{AppRole}} {{roleOptions}} PASSWORD '{{password}}';
                  ELSE
                    ALTER ROLE {{AppRole}} {{roleOptions}} PASSWORD '{{password}}';
                  END IF;

                  EXECUTE format('ALTER DATABASE %I OWNER TO {{AppRole}}', current_database());
                  ALTER SCHEMA public OWNER TO {{AppRole}};

                  -- Tablas, vistas y tablas foráneas (sus secuencias propias e identidades se mueven con ellas).
                  FOR r IN SELECT c.oid::regclass AS obj, c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                           WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
                             AND c.relowner <> '{{AppRole}}'::regrole LOOP
                    EXECUTE format('ALTER %s %s OWNER TO {{AppRole}}',
                      CASE r.relkind WHEN 'v' THEN 'VIEW' WHEN 'm' THEN 'MATERIALIZED VIEW' WHEN 'f' THEN 'FOREIGN TABLE' ELSE 'TABLE' END, r.obj);
                  END LOOP;
                  -- Secuencias sueltas (las de las tablas ya cambiaron de dueño arriba).
                  FOR r IN SELECT c.oid::regclass AS obj FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                           WHERE n.nspname = 'public' AND c.relkind = 'S' AND c.relowner <> '{{AppRole}}'::regrole LOOP
                    EXECUTE format('ALTER SEQUENCE %s OWNER TO {{AppRole}}', r.obj);
                  END LOOP;
                  FOR r IN SELECT p.oid::regprocedure AS obj, p.prokind FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                           WHERE n.nspname = 'public' AND p.proowner <> '{{AppRole}}'::regrole LOOP
                    EXECUTE format('ALTER %s %s OWNER TO {{AppRole}}', CASE r.prokind WHEN 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END, r.obj);
                  END LOOP;
                  -- Tipos propios (enumerados, dominios, compuestos sueltos); los de las tablas y los arreglos van solos.
                  FOR r IN SELECT t.oid::regtype AS obj FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                           WHERE n.nspname = 'public' AND t.typowner <> '{{AppRole}}'::regrole AND t.typelem = 0
                             AND (t.typtype IN ('e', 'd') OR (t.typtype = 'c' AND (SELECT relkind FROM pg_class WHERE oid = t.typrelid) = 'c')) LOOP
                    EXECUTE format('ALTER TYPE %s OWNER TO {{AppRole}}', r.obj);
                  END LOOP;

                  -- Nadie más entra a la base ni crea en su esquema (el superusuario igual puede, para mantenimiento).
                  EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
                  REVOKE ALL ON SCHEMA public FROM PUBLIC;
                  -- Tampoco a la base de mantenimiento: el rol solo conoce la suya.
                  REVOKE CONNECT ON DATABASE postgres FROM PUBLIC;
                END
                $tcvms$;
                """;
            await using (var cmd = new NpgsqlCommand(sql, conn, tx))
                await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);

            // Probar el rol antes de entregárselo al servidor.
            await using (var probe = new NpgsqlConnection(BuildConnectionString(DatabaseName, AppRole, password, pooling: false)))
                await probe.OpenAsync(ct);
            _appPassword = password;
            logger.LogInformation("PostgreSQL embebido: el servidor trabaja con el rol propio '{Role}' (sin superusuario).", AppRole);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _appPassword = null;
            logger.LogError(ex, "PostgreSQL embebido: no se pudo preparar el rol '{Role}'. Este arranque sigue con el superusuario " +
                "(menos seguro); se reintenta en el próximo arranque.", AppRole);
        }
    }

    /// <summary>pg_ctl status: 0 = corriendo, 3 = detenido, 4 = directorio inválido.</summary>
    private bool IsRunning() => RunTool("pg_ctl.exe", ["status", "-D", _dataDir], timeoutSeconds: 30).ExitCode == 0;

    private void StartServer()
    {
        // -l es obligatorio: sin él, postgres hereda las tuberías de salida de este
        // proceso y las mantiene abiertas para siempre. -w espera hasta aceptar conexiones.
        var (exitCode, output) = RunTool("pg_ctl.exe",
            ["start", "-D", _dataDir, "-w", "-t", "60", "-l", Path.Combine(_dataDir, "startup.log")],
            timeoutSeconds: 90);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"No se pudo iniciar PostgreSQL embebido (código {exitCode}): {output.Trim()} — revise {Path.Combine(_dataDir, "startup.log")}.{DescribeStartupFailure(exitCode)}");
    }

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        // Con contraseña incorrecta no tiene sentido reintentar: el secreto no
        // corresponde a este clúster (pgdata restaurado de otra instalación, etc.).
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(BuildConnectionString("postgres", pooling: false));
                await conn.OpenAsync(ct);
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidPassword)
            {
                throw new InvalidOperationException(
                    $"La credencial guardada en '{SecretPath}' no coincide con el clúster de datos.", ex);
            }
            catch (Exception) when (attempt < 20)
            {
                await Task.Delay(500, ct);
            }
        }
    }

    /// <summary>
    /// Traduce los códigos con que Windows mata un proceso que ni siquiera llegó
    /// a ejecutarse. El más habitual en un servidor recién instalado es
    /// 0xC0000135 (falta una DLL): los binarios de PostgreSQL son builds MSVC y
    /// necesitan el runtime de Visual C++, que se distribuye junto a ellos.
    /// Sin esta pista, el log solo muestra un número negativo sin salida alguna.
    /// </summary>
    private string DescribeStartupFailure(int exitCode) => unchecked((uint)exitCode) switch
    {
        0xC0000135 => $" — Windows no encontró una DLL necesaria: faltan VCRUNTIME140.dll/MSVCP140.dll " +
                      $"junto a los binarios de PostgreSQL ('{_binDir}'). Reinstale el sistema con un " +
                      "instalador que los incluya, o instale el redistribuible de Visual C++ x64.",
        0xC0000142 => $" — una DLL de '{_binDir}' no pudo inicializarse.",
        0xC000007B => $" — los binarios de '{_binDir}' no son de 64 bits (imagen inválida).",
        _ => "",
    };

    private (int ExitCode, string Output) RunTool(string exe, IEnumerable<string> args, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_binDir, exe),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        foreach (string v in LibpqEnvVars)
            psi.Environment.Remove(v);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"No se pudo ejecutar {psi.FileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        // Esperar la SALIDA DEL PROCESO, no de las tuberías: pg_ctl start deja el
        // postmaster corriendo con los extremos de escritura heredados, así que
        // las tuberías pueden no cerrarse nunca. Leerlas con tope y seguir.
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* mejor esfuerzo */ }
            throw new TimeoutException($"{exe} {string.Join(' ', args)} no terminó en {timeoutSeconds} s.");
        }
        Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(2));
        string output = (stdout.IsCompletedSuccessfully ? stdout.Result : "")
                      + (stderr.IsCompletedSuccessfully ? stderr.Result : "");
        return (process.ExitCode, output);
    }
}
