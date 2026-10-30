using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Data;

/// <summary>
/// Contexto EF Core del VMS. El esquema evoluciona SOLO con migraciones
/// (dotnet ef migrations add ...): nada de EnsureCreated ni ALTER a mano.
/// </summary>
public class VmsDbContext(DbContextOptions<VmsDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<PasswordHistory> PasswordHistories => Set<PasswordHistory>();
    /// <summary>Ubicaciones del alcance de cada usuario restringido (ver User.RestrictToLocations).</summary>
    public DbSet<UserLocation> UserLocations => Set<UserLocation>();
    /// <summary>Roles (conjuntos de permisos) y su asignación a usuarios.</summary>
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    /// <summary>Alcance de cada rol: ubicaciones y recursos sueltos.</summary>
    public DbSet<RoleLocation> RoleLocations => Set<RoleLocation>();
    public DbSet<RoleResource> RoleResources => Set<RoleResource>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<StreamSession> StreamSessions => Set<StreamSession>();
    /// <summary>Vehículos de la flota (módulo Flota); su grabador es un Device con IsMobile.</summary>
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<FleetEvent> FleetEvents => Set<FleetEvent>();
    public DbSet<Geofence> Geofences => Set<Geofence>();

    /// <summary>Reconocimientos de patentes (módulo Aplicaciones → ANPR).</summary>
    public DbSet<PlateEvent> PlateEvents => Set<PlateEvent>();
    /// <summary>Padrón de vehículos de las personas y registro de ingresos y salidas (acceso vehicular).</summary>
    public DbSet<AccessVehicle> AccessVehicles => Set<AccessVehicle>();
    public DbSet<VehicleAccessRecord> VehicleAccessRecords => Set<VehicleAccessRecord>();

    /// <summary>Bitácora de auditoría (solo-agregar; ver Services\AuditService).</summary>
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    // Paneles de alarma (centrales de intrusión)
    public DbSet<AlarmPanel> AlarmPanels => Set<AlarmPanel>();
    public DbSet<AlarmArea> AlarmAreas => Set<AlarmArea>();
    public DbSet<AlarmZone> AlarmZones => Set<AlarmZone>();
    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();

    // Paneles de cerco eléctrico (ESP8266 que reportan por WebSocket)
    public DbSet<CercoPanel> CercoPanels => Set<CercoPanel>();
    public DbSet<CercoZone> CercoZones => Set<CercoZone>();
    public DbSet<CercoEvent> CercoEvents => Set<CercoEvent>();
    public DbSet<CercoRemote> CercoRemotes => Set<CercoRemote>();

    // Parlantes IP
    public DbSet<Speaker> Speakers => Set<Speaker>();
    public DbSet<SpeakerGroup> SpeakerGroups => Set<SpeakerGroup>();
    public DbSet<SpeakerGroupMember> SpeakerGroupMembers => Set<SpeakerGroupMember>();

    // Módulos de entradas y salidas (relés de barreras, portones, balizas)
    public DbSet<IoModule> IoModules => Set<IoModule>();
    public DbSet<IoPoint> IoPoints => Set<IoPoint>();
    public DbSet<PatrolAgent> PatrolAgents => Set<PatrolAgent>();
    public DbSet<PatrolReader> PatrolReaders => Set<PatrolReader>();
    public DbSet<PatrolAgentCommandRow> PatrolAgentCommands => Set<PatrolAgentCommandRow>();
    public DbSet<PatrolCheckpoint> PatrolCheckpoints => Set<PatrolCheckpoint>();
    public DbSet<PatrolGuard> PatrolGuards => Set<PatrolGuard>();
    public DbSet<PatrolTag> PatrolTags => Set<PatrolTag>();
    public DbSet<PatrolDownload> PatrolDownloads => Set<PatrolDownload>();
    public DbSet<PatrolRawRead> PatrolRawReads => Set<PatrolRawRead>();
    public DbSet<PatrolPass> PatrolPasses => Set<PatrolPass>();
    public DbSet<PatrolRoute> PatrolRoutes => Set<PatrolRoute>();
    public DbSet<PatrolRouteStop> PatrolRouteStops => Set<PatrolRouteStop>();
    public DbSet<PatrolRouteAgent> PatrolRouteAgents => Set<PatrolRouteAgent>();
    public DbSet<PatrolSchedule> PatrolSchedules => Set<PatrolSchedule>();
    public DbSet<PatrolRun> PatrolRuns => Set<PatrolRun>();
    public DbSet<PatrolRunStop> PatrolRunStops => Set<PatrolRunStop>();

    // Citofonía (frentes de videoportero y su historial de llamadas)
    public DbSet<Intercom> Intercoms => Set<Intercom>();
    public DbSet<IntercomCall> IntercomCalls => Set<IntercomCall>();

    // Control de acceso (terminales y controladoras de puertas)
    public DbSet<AccessDevice> AccessDevices => Set<AccessDevice>();
    public DbSet<AccessDoor> AccessDoors => Set<AccessDoor>();
    public DbSet<AccessSchedule> AccessSchedules => Set<AccessSchedule>();
    public DbSet<AccessPlanSlot> AccessPlanSlots => Set<AccessPlanSlot>();
    public DbSet<AccessHoliday> AccessHolidays => Set<AccessHoliday>();
    public DbSet<AccessScheduleSegment> AccessScheduleSegments => Set<AccessScheduleSegment>();
    public DbSet<AccessLevel> AccessLevels => Set<AccessLevel>();
    public DbSet<AccessLevelDoor> AccessLevelDoors => Set<AccessLevelDoor>();
    public DbSet<AccessLevelPerson> AccessLevelPersons => Set<AccessLevelPerson>();
    public DbSet<AccessPerson> AccessPersons => Set<AccessPerson>();
    public DbSet<AccessCard> AccessCards => Set<AccessCard>();
    public DbSet<AccessFingerprint> AccessFingerprints => Set<AccessFingerprint>();
    public DbSet<AccessFace> AccessFaces => Set<AccessFace>();
    public DbSet<AccessPersonDevice> AccessPersonDevices => Set<AccessPersonDevice>();
    public DbSet<AccessPersonField> AccessPersonFields => Set<AccessPersonField>();
    public DbSet<AccessPersonFieldValue> AccessPersonFieldValues => Set<AccessPersonFieldValue>();
    public DbSet<AccessEvent> AccessEvents => Set<AccessEvent>();

    // Automatizaciones (workflows): disparador + acciones, y su historial
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowAction> WorkflowActions => Set<WorkflowAction>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    /// <summary>Alertas mostradas a los operadores y su acuse de recibo.</summary>
    public DbSet<WorkflowAlert> WorkflowAlerts => Set<WorkflowAlert>();
    // Pantallas de alarma de las casetas: los equipos, la constancia de qué
    // aviso le llegó a cada una y los ajustes comunes (sonidos, recordatorio).
    public DbSet<AlarmScreen> AlarmScreens => Set<AlarmScreen>();
    public DbSet<AlarmScreenDelivery> AlarmScreenDeliveries => Set<AlarmScreenDelivery>();
    public DbSet<AlarmScreenSettings> AlarmScreenSettings => Set<AlarmScreenSettings>();
    /// <summary>Servidor de correo saliente: fila única (Id = 1).</summary>
    public DbSet<SmtpSettings> SmtpSettings => Set<SmtpSettings>();
    /// <summary>Cómo debe estar el reloj de los equipos: fila única (Id = 1).</summary>
    public DbSet<DeviceClockPolicy> DeviceClockPolicies => Set<DeviceClockPolicy>();
    /// <summary>Datos internos opacos por nombre (copia del estado de licencia).</summary>
    public DbSet<SystemBlob> SystemBlobs => Set<SystemBlob>();

    // Muro de video
    public DbSet<Decoder> Decoders => Set<Decoder>();
    public DbSet<VideoWall> Walls => Set<VideoWall>();
    public DbSet<WallScreen> WallScreens => Set<WallScreen>();
    public DbSet<ScreenWindow> ScreenWindows => Set<ScreenWindow>();
    public DbSet<WallFloatingWindow> WallFloatingWindows => Set<WallFloatingWindow>();
    public DbSet<WallLayoutPreset> WallLayouts => Set<WallLayoutPreset>();

    // Vistas guardadas del monitoreo en vivo (Custom View de iVMS-4200)
    public DbSet<LiveView> LiveViews => Set<LiveView>();
    public DbSet<LiveViewItem> LiveViewItems => Set<LiveViewItem>();
    public DbSet<LiveViewRole> LiveViewRoles => Set<LiveViewRole>();
    public DbSet<WebLink> WebLinks => Set<WebLink>();

    // Ubicaciones: el árbol lógico de Recursos (cada recurso apunta a una)
    public DbSet<Location> Locations => Set<Location>();
    /// <summary>Fichas de recursos (descripción, consignas y cámaras asociadas).</summary>
    public DbSet<ResourceProfile> ResourceProfiles => Set<ResourceProfile>();
    public DbSet<ResourceProfileCamera> ResourceProfileCameras => Set<ResourceProfileCamera>();

    // -------------------------------------------------------------------
    // Marca "apagado por la licencia": un cambio manual del habilitado la
    // limpia (si el administrador lo enciende o lo apaga, ya es decisión suya
    // y el servidor no debe volver a encenderlo solo). El reconciliador de
    // cupos cambia las dos cosas a la vez, así que su marca se respeta.
    // -------------------------------------------------------------------
    private static readonly (Type Type, string Flag, string Mark)[] LicenseMarks =
    [
        (typeof(AlarmPanel), nameof(AlarmPanel.Enabled), nameof(AlarmPanel.DisabledByLicense)),
        (typeof(CercoPanel), nameof(CercoPanel.Enabled), nameof(CercoPanel.DisabledByLicense)),
        (typeof(AccessDevice), nameof(AccessDevice.Enabled), nameof(AccessDevice.DisabledByLicense)),
        (typeof(Decoder), nameof(Decoder.Enabled), nameof(Decoder.DisabledByLicense)),
        (typeof(Speaker), nameof(Speaker.Enabled), nameof(Speaker.DisabledByLicense)),
        (typeof(Intercom), nameof(Intercom.Enabled), nameof(Intercom.DisabledByLicense)),
        (typeof(IoModule), nameof(IoModule.Enabled), nameof(IoModule.DisabledByLicense)),
        (typeof(PatrolReader), nameof(PatrolReader.Enabled), nameof(PatrolReader.DisabledByLicense)),
        (typeof(PatrolCheckpoint), nameof(PatrolCheckpoint.Enabled), nameof(PatrolCheckpoint.DisabledByLicense)),
        (typeof(Vehicle), nameof(Vehicle.Enabled), nameof(Vehicle.DisabledByLicense)),
        (typeof(Workflow), nameof(Workflow.Enabled), nameof(Workflow.DisabledByLicense)),
        (typeof(User), nameof(User.Enabled), nameof(User.DisabledByLicense)),
        (typeof(Device), nameof(Device.AnprEnabled), nameof(Device.AnprDisabledByLicense)),
    ];

    private void ClearLicenseMarksOnManualChange()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Modified) continue;
            foreach (var (type, flag, mark) in LicenseMarks)
            {
                if (entry.Metadata.ClrType != type) continue;
                var markProp = entry.Property(mark);
                if (entry.Property(flag).IsModified && !markProp.IsModified && markProp.CurrentValue is true)
                    markProp.CurrentValue = false;
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ClearLicenseMarksOnManualChange();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ClearLicenseMarksOnManualChange();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Username).HasMaxLength(64);
            e.Property(u => u.Role).HasMaxLength(16);
        });

        modelBuilder.Entity<PasswordHistory>(e =>
        {
            e.HasOne(h => h.User)
                .WithMany(u => u.PasswordHistories)
                .HasForeignKey(h => h.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(h => h.UserId);
        });

        modelBuilder.Entity<UserLocation>(e =>
        {
            e.HasKey(x => new { x.UserId, x.LocationId });
            e.HasOne<User>().WithMany(u => u.Locations).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // Una ubicación en el alcance de alguien no se borra en silencio: la
            // API exige sacarla antes de los alcances (y la base lo respalda).
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.LocationId);
        });

        modelBuilder.Entity<Role>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(64);
            e.HasIndex(r => r.Name).IsUnique();
            e.Property(r => r.Description).HasMaxLength(256);
            e.Property(r => r.SystemKey).HasMaxLength(16);
            e.HasIndex(r => r.SystemKey).IsUnique().HasFilter("\"SystemKey\" IS NOT NULL");
            e.Ignore(r => r.IsAdmin);
        });

        modelBuilder.Entity<RolePermission>(e =>
        {
            e.HasKey(x => new { x.RoleId, x.Permission });
            e.Property(x => x.Permission).HasMaxLength(48);
            e.HasOne<Role>().WithMany(r => r.Permissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoleLocation>(e =>
        {
            e.HasKey(x => new { x.RoleId, x.LocationId });
            e.HasOne<Role>().WithMany(r => r.Locations).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            // Como en el alcance de un usuario: la API exige sacarla de los roles
            // antes de borrar la ubicación (y la base lo respalda).
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.LocationId);
        });

        modelBuilder.Entity<RoleResource>(e =>
        {
            e.HasKey(x => new { x.RoleId, x.Kind, x.ResourceId });
            e.Property(x => x.Kind).HasMaxLength(24);
            e.HasOne<Role>().WithMany(r => r.Resources).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserRole>(e =>
        {
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne<User>().WithMany(u => u.Roles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // Un rol asignado no se borra en silencio: la API exige quitárselo
            // antes a sus usuarios (y la base lo respalda).
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.RoleId);
        });

        modelBuilder.Entity<Device>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(128);
            e.Property(d => d.DriverKey).HasMaxLength(32);
            e.Property(d => d.Host).HasMaxLength(255);
            e.Property(d => d.Username).HasMaxLength(64);
            e.Property(d => d.Model).HasMaxLength(64);
            e.Property(d => d.SerialNumber).HasMaxLength(64);
            e.Property(d => d.FirmwareVersion).HasMaxLength(64);
            // Los enums se guardan como texto: la base queda legible con psql.
            e.Property(d => d.DeviceType).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(d => new { d.Host, d.SdkPort }).IsUnique().HasFilter("\"IsupDeviceId\" IS NULL");
            e.Property(d => d.IsupDeviceId).HasMaxLength(32);
            // Equipos por ISUP: no tienen IP; los identifica su ID ISUP, único por tipo de equipo.
            e.HasIndex(d => d.IsupDeviceId).IsUnique().HasFilter("\"IsupDeviceId\" IS NOT NULL");
            e.Property(d => d.PartsNote).HasMaxLength(256);
            // Cámara de un terminal de acceso: una por terminal, y se va con él (sus canales también).
            e.HasOne(d => d.AccessDevice).WithOne()
                .HasForeignKey<Device>(d => d.AccessDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Vehículos de la flota: un grabador por vehículo; borrar el vehículo borra su grabador (y este, sus canales).
        modelBuilder.Entity<Vehicle>(e =>
        {
            e.Property(v => v.Code).HasMaxLength(64);
            e.Property(v => v.Plate).HasMaxLength(16);
            e.Property(v => v.Kind).HasMaxLength(64);
            e.Property(v => v.Notes).HasMaxLength(1024);
            e.HasIndex(v => v.Code).IsUnique();
            e.HasIndex(v => v.DeviceId).IsUnique();
            e.HasOne(v => v.Device).WithOne().HasForeignKey<Vehicle>(v => v.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        // Eventos de vehículos: se listan por vehículo y hora, y los pendientes de acusar.
        modelBuilder.Entity<FleetEvent>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Description).HasMaxLength(256);
            e.Property(x => x.AcknowledgedBy).HasMaxLength(128);
            e.Property(x => x.AckNote).HasMaxLength(512);
            e.HasIndex(x => new { x.VehicleId, x.At });
            e.HasIndex(x => x.At);
            e.HasIndex(x => x.AcknowledgedAt).HasFilter("\"AcknowledgedAt\" IS NULL");
            e.HasOne(x => x.Vehicle).WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Geofence>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Color).HasMaxLength(16);
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<Channel>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(128);
            e.Property(c => c.RtspMainUrl).HasMaxLength(512);
            e.Property(c => c.RtspSubUrl).HasMaxLength(512);
            e.HasOne(c => c.Device)
                .WithMany(d => d.Channels)
                .HasForeignKey(c => c.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => new { c.DeviceId, c.ChannelNumber }).IsUnique();
        });

        modelBuilder.Entity<StreamSession>(e =>
        {
            e.Property(s => s.Username).HasMaxLength(64);
            e.Property(s => s.DeviceName).HasMaxLength(128);
            e.Property(s => s.Profile).HasMaxLength(8);
            e.Property(s => s.ClientIp).HasMaxLength(64);
            e.Property(s => s.Path).HasMaxLength(128);
            e.Property(s => s.MtxSessionId).HasMaxLength(64);
            e.HasIndex(s => s.EndedAt);   // las activas se consultan seguido
            e.HasIndex(s => s.MtxSessionId);
        });

        modelBuilder.Entity<PlateEvent>(e =>
        {
            e.Property(p => p.PlateNumber).HasMaxLength(24);
            // CapturedAt es la hora de PARED del equipo (su propio reloj), no
            // un instante UTC: va sin zona, como la trae la cámara. ReceivedAt
            // sí es UTC del servidor y queda con zona (el resto del esquema).
            e.Property(p => p.CapturedAt).HasColumnType("timestamp without time zone");
            e.Property(p => p.CharConfidences).HasMaxLength(96);
            e.Property(p => p.PlateColor).HasMaxLength(32);
            e.Property(p => p.PlateType).HasMaxLength(32);
            e.Property(p => p.VehicleType).HasMaxLength(32);
            e.Property(p => p.VehicleColor).HasMaxLength(32);
            e.Property(p => p.VehicleBrand).HasMaxLength(32);
            e.Property(p => p.VehicleAttributes).HasMaxLength(160);
            e.Property(p => p.Direction).HasMaxLength(32);
            e.Property(p => p.DetectionMethod).HasMaxLength(32);
            e.Property(p => p.Violation).HasMaxLength(64);
            e.Property(p => p.SceneImagePath).HasMaxLength(160);
            e.Property(p => p.PlateImagePath).HasMaxLength(160);
            e.HasOne(p => p.Device)
                .WithMany()
                .HasForeignKey(p => p.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            // La lista en vivo lee siempre por recepción descendente, y el
            // buscador por patente y por equipo.
            e.HasIndex(p => p.ReceivedAt);
            e.HasIndex(p => p.PlateNumber);
            e.HasIndex(p => new { p.DeviceId, p.ReceivedAt });
        });

        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.Property(a => a.Username).HasMaxLength(64);
            e.Property(a => a.Role).HasMaxLength(16);
            e.Property(a => a.Origin).HasMaxLength(16);
            e.Property(a => a.ClientIp).HasMaxLength(64);
            e.Property(a => a.Category).HasMaxLength(32);
            e.Property(a => a.Action).HasMaxLength(48);
            e.Property(a => a.TargetType).HasMaxLength(32);
            e.Property(a => a.TargetId).HasMaxLength(64);
            e.Property(a => a.TargetName).HasMaxLength(128);
            e.Property(a => a.Detail).HasMaxLength(512);
            // El panel consulta siempre por fecha descendente, y filtra por
            // categoría/acción y por usuario.
            e.HasIndex(a => a.Timestamp);
            e.HasIndex(a => new { a.Category, a.Action, a.Timestamp });
            e.HasIndex(a => new { a.Username, a.Timestamp });
        });

        // -------------------------------------------------------------------
        // Paneles de alarma
        // -------------------------------------------------------------------
        modelBuilder.Entity<Speaker>(e =>
        {
            // Altavoz de un terminal de acceso o de un equipo de video: uno por equipo, y se va con él.
            e.HasOne(s => s.AccessDevice).WithOne()
                .HasForeignKey<Speaker>(s => s.AccessDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Device).WithOne()
                .HasForeignKey<Speaker>(s => s.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Ignore(s => s.IsPart);
            e.Property(s => s.Name).HasMaxLength(128);
            e.Property(s => s.DriverKey).HasMaxLength(32);
            e.Property(s => s.Host).HasMaxLength(255);
            e.Property(s => s.Username).HasMaxLength(64);
            e.Property(s => s.Model).HasMaxLength(64);
            e.Property(s => s.SerialNumber).HasMaxLength(64);
            e.Property(s => s.FirmwareVersion).HasMaxLength(64);
            e.Property(s => s.LastError).HasMaxLength(512);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
            // Los agentes TrueCentral Speaker no se alcanzan por IP (Host es solo informativo: varios pueden salir por el mismo NAT).
            e.HasIndex(s => new { s.Host, s.Port }).IsUnique()
                .HasFilter("\"IsupDeviceId\" IS NULL AND \"DriverKey\" <> 'truecentral-agent'");
            e.Property(s => s.IsupDeviceId).HasMaxLength(32);
            // Equipos por ISUP: no tienen IP; los identifica su ID ISUP, único por tipo de equipo.
            e.HasIndex(s => s.IsupDeviceId).IsUnique().HasFilter("\"IsupDeviceId\" IS NOT NULL");
            e.Property(s => s.AgentId).HasMaxLength(32);
            e.Property(s => s.AgentTokenHash).HasMaxLength(64);
            e.Property(s => s.EnrollmentCodeHash).HasMaxLength(64);
            e.HasIndex(s => s.AgentId).IsUnique().HasFilter("\"AgentId\" IS NOT NULL");
            e.HasIndex(s => s.AgentTokenHash).HasFilter("\"AgentTokenHash\" IS NOT NULL");
            e.HasIndex(s => s.EnrollmentCodeHash).HasFilter("\"EnrollmentCodeHash\" IS NOT NULL");
        });

        modelBuilder.Entity<SpeakerGroup>(e =>
        {
            e.Property(g => g.Name).HasMaxLength(64);
            e.Property(g => g.Description).HasMaxLength(255);
            e.HasIndex(g => g.Name).IsUnique();
        });

        modelBuilder.Entity<SpeakerGroupMember>(e =>
        {
            e.HasKey(m => new { m.SpeakerGroupId, m.SpeakerId });
            e.HasOne(m => m.Group).WithMany(g => g.Members).HasForeignKey(m => m.SpeakerGroupId).OnDelete(DeleteBehavior.Cascade);
            // Borrar un parlante lo saca de sus grupos.
            e.HasOne(m => m.Speaker).WithMany(s => s.GroupMemberships).HasForeignKey(m => m.SpeakerId).OnDelete(DeleteBehavior.Cascade);
        });

        // Módulos de entradas y salidas: una fila por módulo y una por cada
        // entrada o salida con su nombre (se van con el módulo).
        modelBuilder.Entity<IoModule>(e =>
        {
            e.Property(m => m.Name).HasMaxLength(128);
            e.Property(m => m.DriverKey).HasMaxLength(32);
            e.Property(m => m.Host).HasMaxLength(255);
            e.Property(m => m.ModelKey).HasMaxLength(32);
            e.Property(m => m.Model).HasMaxLength(32);
            e.Property(m => m.ReportedModel).HasMaxLength(32);
            e.Property(m => m.LastError).HasMaxLength(512);
            e.Property(m => m.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(m => new { m.Host, m.Port }).IsUnique();
            e.Property(m => m.Username).HasMaxLength(64);
            // Entradas y salidas de un terminal de acceso o de un equipo de video: un módulo por equipo, y se va con él.
            e.HasOne(m => m.AccessDevice).WithOne()
                .HasForeignKey<IoModule>(m => m.AccessDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Device).WithOne()
                .HasForeignKey<IoModule>(m => m.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Ignore(m => m.IsPart);
            e.HasMany(m => m.Points).WithOne(p => p.IoModule)
                .HasForeignKey(p => p.IoModuleId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<IoPoint>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(64);
            e.Property(p => p.Kind).HasConversion<string>().HasMaxLength(8);
            e.HasIndex(p => new { p.IoModuleId, p.Kind, p.Number }).IsUnique();
        });

        // -------------------------------------------------------------------
        // Control de rondas
        modelBuilder.Entity<PatrolAgent>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(128);
            e.Property(a => a.TokenHash).HasMaxLength(64);
            e.Property(a => a.EnrollmentCodeHash).HasMaxLength(64);
            e.Property(a => a.MachineName).HasMaxLength(128);
            e.Property(a => a.Version).HasMaxLength(32);
            e.Property(a => a.RemoteAddress).HasMaxLength(64);
            e.Property(a => a.LastError).HasMaxLength(512);
            e.HasIndex(a => a.TokenHash).IsUnique().HasFilter("\"TokenHash\" IS NOT NULL");
            e.HasIndex(a => a.EnrollmentCodeHash).HasFilter("\"EnrollmentCodeHash\" IS NOT NULL");
            e.HasOne<Location>().WithMany().HasForeignKey(a => a.LocationId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<PatrolReader>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(128);
            e.Property(r => r.DriverKey).HasMaxLength(32);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.LastError).HasMaxLength(512);
            e.HasIndex(r => r.Serial).IsUnique();
            e.HasOne(r => r.Agent).WithMany().HasForeignKey(r => r.AgentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Location>().WithMany().HasForeignKey(r => r.LocationId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<PatrolAgentCommandRow>(e =>
        {
            e.ToTable("PatrolAgentCommands");
            e.Property(c => c.Kind).HasMaxLength(16);
            e.Property(c => c.RequestedBy).HasMaxLength(128);
            e.Property(c => c.Message).HasMaxLength(512);
            e.HasIndex(c => new { c.AgentId, c.CompletedAt });
            e.HasOne<PatrolAgent>().WithMany().HasForeignKey(c => c.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PatrolReader>().WithMany().HasForeignKey(c => c.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PatrolCheckpoint>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(128);
            e.Property(c => c.Kind).HasConversion<string>().HasMaxLength(8);
            e.Property(c => c.TagId).HasMaxLength(16);
            e.Property(c => c.Notes).HasMaxLength(1024);
            e.HasIndex(c => c.TagId).IsUnique().HasFilter("\"TagId\" IS NOT NULL");
            e.HasOne<Location>().WithMany().HasForeignKey(c => c.LocationId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<AccessDoor>().WithMany().HasForeignKey(c => c.AccessDoorId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<PatrolGuard>(e =>
        {
            e.Property(g => g.Notes).HasMaxLength(1024);
            e.Property(g => g.DeactivatedBy).HasMaxLength(128);
            e.Property(g => g.DeactivationReason).HasMaxLength(512);
            e.HasIndex(g => g.AccessPersonId).IsUnique();
            // Borrar la persona (borrado definitivo, excepcional) se lleva el rol;
            // los pasos conservan la copia del nombre (PatrolPass.GuardName).
            e.HasOne(g => g.AccessPerson).WithMany().HasForeignKey(g => g.AccessPersonId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(g => g.Tags).WithOne(t => t.PatrolGuard)
                .HasForeignKey(t => t.PatrolGuardId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<PatrolTag>(e =>
        {
            e.Property(t => t.TagId).HasMaxLength(16);
            e.Property(t => t.Label).HasMaxLength(128);
            e.HasIndex(t => t.TagId).IsUnique();
        });
        modelBuilder.Entity<PatrolDownload>(e =>
        {
            e.Property(d => d.Origin).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.Actor).HasMaxLength(128);
            e.Property(d => d.Error).HasMaxLength(512);
            e.HasIndex(d => new { d.ReaderId, d.StartedAt });
            e.HasOne<PatrolReader>().WithMany().HasForeignKey(d => d.ReaderId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PatrolRawRead>(e =>
        {
            e.Property(r => r.TagId).HasMaxLength(16);
            e.HasIndex(r => new { r.ReaderId, r.TagId, r.ReadAt }).IsUnique();
            e.HasIndex(r => new { r.ReaderId, r.ReadAt });
            e.HasIndex(r => r.TagId);
            e.HasOne<PatrolReader>().WithMany().HasForeignKey(r => r.ReaderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PatrolDownload>().WithMany().HasForeignKey(r => r.DownloadId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PatrolPass>(e =>
        {
            e.Property(p => p.Source).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.GuardName).HasMaxLength(256);
            e.Property(p => p.GuardEmployeeNo).HasMaxLength(64);
            e.Property(p => p.VoidReason).HasMaxLength(512);
            e.Property(p => p.EditedBy).HasMaxLength(128);
            e.Property(p => p.EditReason).HasMaxLength(512);
            e.HasIndex(p => p.ReadAt);
            e.HasIndex(p => new { p.CheckpointId, p.ReadAt });
            e.HasIndex(p => new { p.PatrolGuardId, p.ReadAt });
            e.HasIndex(p => p.CheckpointReadId).IsUnique().HasFilter("\"CheckpointReadId\" IS NOT NULL");
            e.HasOne<PatrolCheckpoint>().WithMany().HasForeignKey(p => p.CheckpointId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PatrolGuard>().WithMany().HasForeignKey(p => p.PatrolGuardId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<PatrolTag>().WithMany().HasForeignKey(p => p.PatrolTagId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<PatrolReader>().WithMany().HasForeignKey(p => p.ReaderId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<PatrolRawRead>().WithMany().HasForeignKey(p => p.CheckpointReadId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<PatrolRawRead>().WithMany().HasForeignKey(p => p.GuardReadId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PatrolRoute>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(128);
            e.Property(r => r.Notes).HasMaxLength(1024);
            e.Property(r => r.OrderMode).HasConversion<string>().HasMaxLength(8);
            e.HasOne<Location>().WithMany().HasForeignKey(r => r.LocationId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(r => r.Stops).WithOne().HasForeignKey(s => s.RouteId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Agents).WithOne().HasForeignKey(a => a.RouteId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Schedules).WithOne().HasForeignKey(s => s.RouteId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PatrolRouteStop>(e =>
        {
            e.HasIndex(s => new { s.RouteId, s.Sequence }).IsUnique();
            e.HasOne(s => s.Checkpoint).WithMany().HasForeignKey(s => s.CheckpointId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PatrolRouteAgent>(e =>
        {
            e.HasKey(a => new { a.RouteId, a.AgentId });
            e.HasOne<PatrolAgent>().WithMany().HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PatrolRun>(e =>
        {
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Cause).HasMaxLength(512);
            e.Property(r => r.CancelledBy).HasMaxLength(128);
            // Una ronda por horario y hora de inicio: materializar dos veces no duplica.
            e.HasIndex(r => new { r.ScheduleId, r.ExpectedStart }).IsUnique().HasFilter("\"ScheduleId\" IS NOT NULL");
            e.HasIndex(r => new { r.RouteId, r.ExpectedStart });
            e.HasIndex(r => new { r.Status, r.ExpectedStart });
            e.HasOne<PatrolRoute>().WithMany().HasForeignKey(r => r.RouteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PatrolSchedule>().WithMany().HasForeignKey(r => r.ScheduleId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<PatrolReader>().WithMany().HasForeignKey(r => r.ReaderId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(r => r.Stops).WithOne().HasForeignKey(s => s.RunId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PatrolRunStop>(e =>
        {
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(s => new { s.RunId, s.Sequence }).IsUnique();
            e.HasIndex(s => s.PassId);
            e.HasOne<PatrolCheckpoint>().WithMany().HasForeignKey(s => s.CheckpointId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PatrolPass>().WithMany().HasForeignKey(s => s.PassId).OnDelete(DeleteBehavior.SetNull);
        });

        // -------------------------------------------------------------------
        // Citofonía
        // -------------------------------------------------------------------
        modelBuilder.Entity<Intercom>(e =>
        {
            e.Property(i => i.Name).HasMaxLength(128);
            e.Property(i => i.DriverKey).HasMaxLength(32);
            e.Property(i => i.Host).HasMaxLength(255);
            e.Property(i => i.Username).HasMaxLength(64);
            e.Property(i => i.GroupName).HasMaxLength(64);
            e.Property(i => i.Model).HasMaxLength(64);
            e.Property(i => i.SerialNumber).HasMaxLength(64);
            e.Property(i => i.FirmwareVersion).HasMaxLength(64);
            e.Property(i => i.LastError).HasMaxLength(512);
            e.Property(i => i.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(i => new { i.Host, i.Port }).IsUnique().HasFilter("\"IsupDeviceId\" IS NULL");
            e.Property(i => i.IsupDeviceId).HasMaxLength(32);
            // Equipos por ISUP: no tienen IP; los identifica su ID ISUP, único por tipo de equipo.
            e.HasIndex(i => i.IsupDeviceId).IsUnique().HasFilter("\"IsupDeviceId\" IS NOT NULL");
            // Borrar el dispositivo de video deja el frente sin cámara, no lo borra.
            e.HasOne(i => i.Channel).WithMany().HasForeignKey(i => i.ChannelId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<IntercomCall>(e =>
        {
            e.Property(c => c.IntercomName).HasMaxLength(128);
            e.Property(c => c.AnsweredBy).HasMaxLength(64);
            e.Property(c => c.DoorOpenedBy).HasMaxLength(64);
            e.Property(c => c.Origin).HasMaxLength(128);
            e.Property(c => c.EndReason).HasMaxLength(128);
            e.Property(c => c.State).HasConversion<string>().HasMaxLength(16);
            // Sin FK al frente: el historial sobrevive a su borrado (lo pide la auditoría).
            e.HasIndex(c => c.StartedAt);
            e.HasIndex(c => new { c.IntercomId, c.StartedAt });
        });

        // -------------------------------------------------------------------
        // Control de acceso
        // -------------------------------------------------------------------
        modelBuilder.Entity<AccessDevice>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(128);
            e.Property(a => a.DriverKey).HasMaxLength(32);
            e.Property(a => a.Host).HasMaxLength(255);
            e.Property(a => a.Username).HasMaxLength(64);
            e.Property(a => a.Model).HasMaxLength(64);
            e.Property(a => a.SerialNumber).HasMaxLength(64);
            e.Property(a => a.FirmwareVersion).HasMaxLength(64);
            e.Property(a => a.MacAddress).HasMaxLength(32);
            e.Property(a => a.LastError).HasMaxLength(512);
            e.Property(a => a.PartsNote).HasMaxLength(256);
            e.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(a => new { a.Host, a.Port }).IsUnique().HasFilter("\"IsupDeviceId\" IS NULL");
            e.Property(a => a.IsupDeviceId).HasMaxLength(32);
            // Equipos por ISUP: no tienen IP; los identifica su ID ISUP, único por tipo de equipo.
            e.HasIndex(a => a.IsupDeviceId).IsUnique().HasFilter("\"IsupDeviceId\" IS NOT NULL");
        });

        modelBuilder.Entity<AccessDoor>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(128);
            e.Property(d => d.Mode).HasConversion<string>().HasMaxLength(16);
            e.HasOne(d => d.AccessDevice)
                .WithMany(a => a.Doors)
                .HasForeignKey(d => d.AccessDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => new { d.AccessDeviceId, d.Number }).IsUnique();
        });

        modelBuilder.Entity<AccessSchedule>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(128);
            e.Property(s => s.Description).HasMaxLength(512);
            e.Property(s => s.HolidayMode).HasConversion<string>().HasMaxLength(16)
                .HasDefaultValue(TrueCentralVms.Core.Contracts.AccessHolidayMode.Normal);
            e.HasIndex(s => s.Name).IsUnique();
        });

        modelBuilder.Entity<AccessHoliday>(e =>
        {
            e.Property(h => h.Name).HasMaxLength(128);
            e.HasIndex(h => h.StartDate);
        });

        modelBuilder.Entity<AccessPlanSlot>(e =>
        {
            e.Property(s => s.Hash).HasMaxLength(64);
            e.Property(s => s.Label).HasMaxLength(64);
            e.HasIndex(s => s.Number).IsUnique();
            e.HasIndex(s => s.Hash).IsUnique();
        });

        modelBuilder.Entity<AccessScheduleSegment>(e =>
        {
            e.HasOne(s => s.AccessSchedule)
                .WithMany(s => s.Segments)
                .HasForeignKey(s => s.AccessScheduleId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.AccessScheduleId, s.Day });
        });

        modelBuilder.Entity<AccessLevel>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(128);
            e.Property(l => l.Description).HasMaxLength(512);
            e.HasIndex(l => l.Name).IsUnique();
            // Un horario en uso no se puede borrar: primero hay que sacarlo de
            // los niveles que lo usan (si no, quedarían sin decir "cuándo").
            e.HasOne(l => l.AccessSchedule)
                .WithMany(s => s.Levels)
                .HasForeignKey(l => l.AccessScheduleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccessLevelDoor>(e =>
        {
            e.HasKey(x => new { x.AccessLevelId, x.AccessDoorId });
            e.HasOne(x => x.AccessLevel)
                .WithMany(l => l.Doors)
                .HasForeignKey(x => x.AccessLevelId)
                .OnDelete(DeleteBehavior.Cascade);
            // Borrar un equipo (y con él sus puertas) saca esas puertas de los
            // niveles; el nivel sobrevive con las que le queden.
            e.HasOne(x => x.AccessDoor)
                .WithMany(d => d.Levels)
                .HasForeignKey(x => x.AccessDoorId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccessLevelPerson>(e =>
        {
            e.HasKey(x => new { x.AccessLevelId, x.AccessPersonId });
            e.HasOne(x => x.AccessLevel)
                .WithMany(l => l.Persons)
                .HasForeignKey(x => x.AccessLevelId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AccessPerson)
                .WithMany(p => p.Levels)
                .HasForeignKey(x => x.AccessPersonId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccessPerson>(e =>
        {
            e.Property(p => p.EmployeeNo).HasMaxLength(32);
            e.Property(p => p.FirstName).HasMaxLength(64);
            e.Property(p => p.LastName).HasMaxLength(64);
            e.Property(p => p.Department).HasMaxLength(128);
            e.Property(p => p.Position).HasMaxLength(128);
            e.Property(p => p.Email).HasMaxLength(255);
            e.Property(p => p.Phone).HasMaxLength(32);
            e.Property(p => p.Notes).HasMaxLength(1024);
            e.Property(p => p.SyncError).HasMaxLength(512);
            e.Property(p => p.SyncState).HasConversion<string>().HasMaxLength(16);
            e.Ignore(p => p.FullName);
            e.HasIndex(p => p.EmployeeNo).IsUnique();
            e.HasIndex(p => new { p.LastName, p.FirstName });
        });

        modelBuilder.Entity<AccessPersonField>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(64);
            e.Property(f => f.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(f => f.Options).HasMaxLength(4000);
            e.Property(f => f.Description).HasMaxLength(256);
            e.Ignore(f => f.OptionList);
            e.HasIndex(f => f.Name).IsUnique();
        });

        modelBuilder.Entity<AccessPersonFieldValue>(e =>
        {
            e.HasKey(v => new { v.AccessPersonId, v.AccessPersonFieldId });
            e.Property(v => v.Value).HasMaxLength(512);
            e.HasOne(v => v.AccessPerson)
                .WithMany(p => p.FieldValues)
                .HasForeignKey(v => v.AccessPersonId)
                .OnDelete(DeleteBehavior.Cascade);
            // Borrar un campo borra sus valores: el campo es la pregunta, sin
            // ella las respuestas no significan nada.
            e.HasOne(v => v.Field)
                .WithMany(f => f.Values)
                .HasForeignKey(v => v.AccessPersonFieldId)
                .OnDelete(DeleteBehavior.Cascade);
            // Filtrar la lista por un campo ("Colacion = 2") busca por campo y valor.
            e.HasIndex(v => new { v.AccessPersonFieldId, v.Value });
        });

        modelBuilder.Entity<AccessVehicle>(e =>
        {
            e.Property(v => v.Plate).HasMaxLength(16);
            e.Property(v => v.Label).HasMaxLength(64);
            e.Property(v => v.Brand).HasMaxLength(64);
            e.Property(v => v.Model).HasMaxLength(64);
            e.Property(v => v.Color).HasMaxLength(32);
            e.Property(v => v.Notes).HasMaxLength(512);
            e.HasOne(v => v.AccessPerson)
                .WithMany()
                .HasForeignKey(v => v.AccessPersonId)
                .OnDelete(DeleteBehavior.Cascade);
            // Una patente es de un solo vehículo: si no, la lectura no sabría de quién es.
            e.HasIndex(v => v.Plate).IsUnique();
            e.HasIndex(v => v.AccessPersonId);
        });

        modelBuilder.Entity<VehicleAccessRecord>(e =>
        {
            e.Property(r => r.AccessPoint).HasMaxLength(96);
            e.Property(r => r.Direction).HasConversion<string>().HasMaxLength(8);
            e.Property(r => r.Result).HasConversion<string>().HasMaxLength(24);
            e.Property(r => r.Plate).HasMaxLength(24);
            e.Property(r => r.PlateStatus).HasMaxLength(16);
            e.Property(r => r.CameraName).HasMaxLength(256);
            e.Property(r => r.VehicleLabel).HasMaxLength(64);
            e.Property(r => r.PersonName).HasMaxLength(256);
            e.Property(r => r.EmployeeNo).HasMaxLength(64);
            e.Property(r => r.Department).HasMaxLength(128);
            e.Property(r => r.WorkflowName).HasMaxLength(128);
            e.Property(r => r.PlateImagePath).HasMaxLength(160);
            e.Property(r => r.Detail).HasMaxLength(512);
            // Sin claves foráneas: el registro sobrevive a que se borren la
            // persona, el vehículo, la cámara o la automatización.
            e.HasIndex(r => r.At);
            e.HasIndex(r => new { r.Plate, r.At });
            e.HasIndex(r => new { r.AccessPersonId, r.At });
        });

        modelBuilder.Entity<AccessCard>(e =>
        {
            e.Property(c => c.Number).HasMaxLength(32);
            e.HasOne(c => c.AccessPerson)
                .WithMany(p => p.Cards)
                .HasForeignKey(c => c.AccessPersonId)
                .OnDelete(DeleteBehavior.Cascade);
            // Dos personas con la misma tarjeta serían dos identidades para el
            // mismo trozo de plástico: el equipo no sabría a quién anotar.
            e.HasIndex(c => c.Number).IsUnique();
        });

        modelBuilder.Entity<AccessFingerprint>(e =>
        {
            e.Property(f => f.Source).HasMaxLength(128);
            e.Property(f => f.Format).HasMaxLength(16)
                .HasDefaultValue(TrueCentralVms.Core.Drivers.AccessFingerprintFormats.Hikvision);
            e.HasOne(f => f.AccessPerson)
                .WithMany(p => p.Fingerprints)
                .HasForeignKey(f => f.AccessPersonId)
                .OnDelete(DeleteBehavior.Cascade);
            // Un dedo por persona Y formato: volver a capturar el mismo reemplaza
            // la anterior, y el mismo dedo puede estar para Hikvision y ZKTeco.
            e.HasIndex(f => new { f.AccessPersonId, f.Number, f.Format }).IsUnique();
        });

        modelBuilder.Entity<AccessFace>(e =>
        {
            e.Property(f => f.ContentType).HasMaxLength(32);
            e.Property(f => f.Source).HasMaxLength(128);
            e.HasOne(f => f.AccessPerson)
                .WithOne(p => p.Face)
                .HasForeignKey<AccessFace>(f => f.AccessPersonId)
                .OnDelete(DeleteBehavior.Cascade);
            // Un rostro por persona: los terminales guardan un modelo por
            // legajo, así que una foto nueva reemplaza a la anterior.
            e.HasIndex(f => f.AccessPersonId).IsUnique();
        });

        modelBuilder.Entity<AccessPersonDevice>(e =>
        {
            e.Property(x => x.Error).HasMaxLength(512);
            e.Property(x => x.AppliedHash).HasMaxLength(64);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.AccessPerson)
                .WithMany(p => p.Devices)
                .HasForeignKey(x => x.AccessPersonId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AccessDevice)
                .WithMany()
                .HasForeignKey(x => x.AccessDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AccessPersonId, x.AccessDeviceId }).IsUnique();
        });

        modelBuilder.Entity<AccessEvent>(e =>
        {
            e.Property(x => x.DeviceName).HasMaxLength(128);
            e.Property(x => x.DoorName).HasMaxLength(128);
            e.Property(x => x.Description).HasMaxLength(512);
            e.Property(x => x.EmployeeNo).HasMaxLength(32);
            e.Property(x => x.PersonName).HasMaxLength(128);
            e.Property(x => x.CardNumber).HasMaxLength(32);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Credential).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.AccessDevice)
                .WithMany()
                .HasForeignKey(x => x.AccessDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.AccessPersonId);
            // El sondeo pregunta "¿ya tenía este evento?" por equipo y hora.
            e.HasIndex(x => new { x.AccessDeviceId, x.Timestamp });
        });

        modelBuilder.Entity<AlarmPanel>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(128);
            e.Property(p => p.DriverKey).HasMaxLength(32);
            e.Property(p => p.Host).HasMaxLength(255);
            e.Property(p => p.Username).HasMaxLength(64);
            e.Property(p => p.Model).HasMaxLength(64);
            e.Property(p => p.SerialNumber).HasMaxLength(64);
            e.Property(p => p.FirmwareVersion).HasMaxLength(64);
            e.Property(p => p.LastError).HasMaxLength(512);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(p => new { p.Host, p.Port }).IsUnique().HasFilter("\"IsupDeviceId\" IS NULL");
            e.Property(p => p.IsupDeviceId).HasMaxLength(32);
            // Equipos por ISUP: no tienen IP; los identifica su ID ISUP, único por tipo de equipo.
            e.HasIndex(p => p.IsupDeviceId).IsUnique().HasFilter("\"IsupDeviceId\" IS NOT NULL");
        });

        modelBuilder.Entity<AlarmArea>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(128);
            e.Property(a => a.ArmState).HasConversion<string>().HasMaxLength(16);
            e.HasOne(a => a.AlarmPanel)
                .WithMany(p => p.Areas)
                .HasForeignKey(a => a.AlarmPanelId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.AlarmPanelId, a.Number }).IsUnique();
        });

        modelBuilder.Entity<AlarmZone>(e =>
        {
            e.Property(z => z.Name).HasMaxLength(128);
            e.Property(z => z.ZoneType).HasMaxLength(48);
            e.Property(z => z.DetectorType).HasMaxLength(48);
            e.Property(z => z.Model).HasMaxLength(64);
            e.Property(z => z.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(z => z.AlarmPanel)
                .WithMany(p => p.Zones)
                .HasForeignKey(z => z.AlarmPanelId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(z => new { z.AlarmPanelId, z.Number }).IsUnique();
        });

        modelBuilder.Entity<AlarmEvent>(e =>
        {
            e.Property(a => a.PanelName).HasMaxLength(128);
            e.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.Severity).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.Code).HasMaxLength(16);
            e.Property(a => a.Description).HasMaxLength(256);
            e.Property(a => a.AreaName).HasMaxLength(128);
            e.Property(a => a.ZoneName).HasMaxLength(128);
            e.Property(a => a.Operator).HasMaxLength(64);
            e.Property(a => a.Source).HasMaxLength(8);
            e.Property(a => a.RawJson).HasMaxLength(4096);
            // La pantalla lee por recepción descendente y filtra por panel,
            // por naturaleza y por fecha del evento.
            e.HasIndex(a => a.ReceivedAt);
            e.HasIndex(a => new { a.AlarmPanelId, a.ReceivedAt });
            e.HasIndex(a => new { a.Kind, a.ReceivedAt });
        });

        // ---- Paneles de cerco eléctrico ----
        modelBuilder.Entity<CercoPanel>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(128);
            e.Property(p => p.DeviceId).HasMaxLength(48);
            e.Property(p => p.Model).HasMaxLength(64);
            e.Property(p => p.Firmware).HasMaxLength(64);
            e.Property(p => p.Mac).HasMaxLength(32);
            e.Property(p => p.LastError).HasMaxLength(512);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.KeyMode).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.Zone0Mode).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.PowerSource).HasConversion<string>().HasMaxLength(16);
            // El DeviceId identifica al panel en el handshake: único.
            e.HasIndex(p => p.DeviceId).IsUnique();
        });

        modelBuilder.Entity<CercoRemote>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(64);
            e.Property(r => r.Code).HasMaxLength(16);
            e.Property(r => r.Action).HasConversion<string>().HasMaxLength(16);
            e.HasOne(r => r.CercoPanel)
                .WithMany(p => p.Remotes)
                .HasForeignKey(r => r.CercoPanelId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.CercoPanelId, r.Slot }).IsUnique();
        });

        modelBuilder.Entity<CercoZone>(e =>
        {
            e.Property(z => z.Name).HasMaxLength(128);
            e.HasOne(z => z.CercoPanel)
                .WithMany(p => p.Zones)
                .HasForeignKey(z => z.CercoPanelId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(z => new { z.CercoPanelId, z.Number }).IsUnique();
        });

        modelBuilder.Entity<CercoEvent>(e =>
        {
            e.Property(a => a.PanelName).HasMaxLength(128);
            e.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.Severity).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.Description).HasMaxLength(256);
            e.Property(a => a.ZoneName).HasMaxLength(128);
            e.Property(a => a.RawJson).HasMaxLength(4096);
            e.HasIndex(a => a.ReceivedAt);
            e.HasIndex(a => new { a.CercoPanelId, a.ReceivedAt });
            e.HasIndex(a => new { a.Kind, a.ReceivedAt });
        });

        // -------------------------------------------------------------------
        // Automatizaciones (workflows)
        // -------------------------------------------------------------------
        modelBuilder.Entity<Workflow>(e =>
        {
            e.Property(w => w.Name).HasMaxLength(128);
            e.Property(w => w.Description).HasMaxLength(512);
            e.Property(w => w.GroupName).HasMaxLength(64);
            e.Property(w => w.TriggerType).HasMaxLength(32);
            e.Property(w => w.CreatedBy).HasMaxLength(64);
            e.HasIndex(w => w.Name).IsUnique();
            // El motor consulta las habilitadas de un disparador en cada evento.
            e.HasIndex(w => new { w.TriggerType, w.Enabled });
        });

        modelBuilder.Entity<WorkflowAction>(e =>
        {
            e.Property(a => a.Type).HasMaxLength(32);
            e.Property(a => a.NodeId).HasMaxLength(32);
            e.HasOne(a => a.Workflow)
                .WithMany(w => w.Actions)
                .HasForeignKey(a => a.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.WorkflowId, a.Order });
        });

        modelBuilder.Entity<WorkflowRun>(e =>
        {
            e.Property(r => r.WorkflowName).HasMaxLength(128);
            e.Property(r => r.TriggerSummary).HasMaxLength(256);
            e.Property(r => r.Error).HasMaxLength(512);
            e.Property(r => r.StartedBy).HasMaxLength(64);
            // El historial se lee por fecha descendente y se filtra por workflow.
            e.HasIndex(r => r.StartedAt);
            e.HasIndex(r => new { r.WorkflowId, r.StartedAt });
        });

        modelBuilder.Entity<WorkflowAlert>(e =>
        {
            e.Property(a => a.WorkflowName).HasMaxLength(128);
            e.Property(a => a.Title).HasMaxLength(160);
            e.Property(a => a.Message).HasMaxLength(512);
            e.Property(a => a.TriggerSummary).HasMaxLength(256);
            e.Property(a => a.ImagePath).HasMaxLength(256);
            e.Property(a => a.ImagePathsJson).HasMaxLength(2048);
            e.Property(a => a.ChannelIdsJson).HasMaxLength(256);
            e.Property(a => a.Sound).HasMaxLength(64);
            e.Property(a => a.Severity).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.AcknowledgedBy).HasMaxLength(64);
            e.Property(a => a.AcknowledgedFrom).HasMaxLength(16);
            e.Property(a => a.AcknowledgedIp).HasMaxLength(64);
            e.Property(a => a.RecipientUserIds).HasMaxLength(512);
            e.Property(a => a.Recipients).HasMaxLength(512);
            e.Property(a => a.ScreenIds).HasMaxLength(512);
            e.Property(a => a.Screens).HasMaxLength(512);
            e.Property(a => a.ResourceKey).HasMaxLength(32);
            e.Property(a => a.LocationPath).HasMaxLength(1100);
            e.Property(a => a.Instructions).HasMaxLength(4000);
            // Las pendientes se consultan en cada arranque de cliente; el
            // registro se lee por fecha descendente.
            e.HasIndex(a => a.RaisedAt);
            e.HasIndex(a => new { a.AcknowledgedAt, a.RaisedAt });
            // Historial de la ficha de un recurso: sus avisos, los más recientes primero.
            e.HasIndex(a => new { a.ResourceKey, a.RaisedAt }).HasFilter("\"ResourceKey\" IS NOT NULL");
        });

        // Pantallas de alarma: una fila por equipo emparejado (o que pidió
        // emparejarse). El MachineId es la identidad del equipo y la
        // credencial se busca por su hash en cada conexión.
        modelBuilder.Entity<AlarmScreen>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.MachineId).HasMaxLength(64);
            e.Property(x => x.Hostname).HasMaxLength(128);
            e.Property(x => x.AppVersion).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.PairingCode).HasMaxLength(8);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Layout).HasMaxLength(16);
            e.Property(x => x.AudioOutput).HasMaxLength(256);
            e.Property(x => x.AudioOutputsJson).HasMaxLength(4000);
            e.Property(x => x.ApprovedBy).HasMaxLength(64);
            e.Property(x => x.LastIp).HasMaxLength(64);
            e.HasIndex(x => x.MachineId).IsUnique();
            e.HasIndex(x => x.TokenHash).IsUnique().HasFilter("\"TokenHash\" IS NOT NULL");
        });
        modelBuilder.Entity<AlarmScreenDelivery>(e =>
        {
            // Una constancia por aviso y pantalla: la primera entrega es la que vale.
            e.HasIndex(x => new { x.AlertId, x.ScreenId }).IsUnique();
            e.HasIndex(x => x.ScreenId);
            e.HasOne<WorkflowAlert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AlarmScreen>().WithMany().HasForeignKey(x => x.ScreenId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AlarmScreenSettings>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SiteName).HasMaxLength(128);
            e.Property(x => x.AlarmSound).HasMaxLength(64);
            e.Property(x => x.HighPrioritySound).HasMaxLength(64);
            e.Property(x => x.ReminderSound).HasMaxLength(64);
        });

        modelBuilder.Entity<SmtpSettings>(e =>
        {
            e.Property(s => s.Host).HasMaxLength(255);
            e.Property(s => s.Username).HasMaxLength(128);
            e.Property(s => s.FromAddress).HasMaxLength(255);
            e.Property(s => s.FromName).HasMaxLength(128);
            e.Property(s => s.Security).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<DeviceClockPolicy>(e =>
        {
            e.Property(p => p.TimeZoneId).HasMaxLength(128);
            e.Property(p => p.Mode).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.NtpServer).HasMaxLength(255);
        });

        modelBuilder.Entity<SystemBlob>(e =>
        {
            e.Property(b => b.Name).HasMaxLength(64);
            e.HasIndex(b => b.Name).IsUnique();
        });

        // -------------------------------------------------------------------
        // Muro de video
        // -------------------------------------------------------------------
        modelBuilder.Entity<Decoder>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(128);
            e.Property(d => d.DriverKey).HasMaxLength(32);
            e.Property(d => d.Host).HasMaxLength(255);
            e.Property(d => d.Username).HasMaxLength(64);
            e.Property(d => d.Model).HasMaxLength(64);
            e.HasIndex(d => new { d.Host, d.Port }).IsUnique();
        });

        modelBuilder.Entity<VideoWall>(e =>
        {
            e.Property(w => w.Name).HasMaxLength(128);
            // Un decodificador con muros no se puede borrar: la API lo exige
            // explícitamente para poder liberar antes sus ventanas en el equipo.
            e.HasOne(w => w.Decoder)
                .WithMany(d => d.Walls)
                .HasForeignKey(w => w.DecoderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WallScreen>(e =>
        {
            e.Property(s => s.Label).HasMaxLength(64);
            e.HasOne(s => s.VideoWall)
                .WithMany(w => w.Screens)
                .HasForeignKey(s => s.VideoWallId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.VideoWallId, s.Row, s.Col }).IsUnique();
        });

        modelBuilder.Entity<ScreenWindow>(e =>
        {
            e.Property(x => x.ExternalUrl).HasMaxLength(512);
            e.Property(x => x.ExternalLabel).HasMaxLength(128);
            e.HasOne(x => x.WallScreen)
                .WithMany(s => s.Windows)
                .HasForeignKey(x => x.WallScreenId)
                .OnDelete(DeleteBehavior.Cascade);
            // Si el dispositivo (y con él su canal) se elimina, la ventana
            // queda vacía en vez de impedir el borrado.
            e.HasOne(x => x.AssignedChannel)
                .WithMany()
                .HasForeignKey(x => x.AssignedChannelId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.WallScreenId, x.WindowIndex }).IsUnique();
        });

        modelBuilder.Entity<WallFloatingWindow>(e =>
        {
            e.Property(f => f.ExternalUrl).HasMaxLength(512);
            e.Property(f => f.ExternalLabel).HasMaxLength(128);
            e.HasOne(f => f.VideoWall)
                .WithMany(w => w.Floating)
                .HasForeignKey(f => f.VideoWallId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(f => f.AssignedChannel)
                .WithMany()
                .HasForeignKey(f => f.AssignedChannelId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WallLayoutPreset>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(128);
            e.HasOne(l => l.VideoWall)
                .WithMany(w => w.Layouts)
                .HasForeignKey(l => l.VideoWallId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => new { l.VideoWallId, l.Name }).IsUnique();
        });

        modelBuilder.Entity<WallLayoutScreen>(e =>
        {
            e.ToTable("WallLayoutScreens");
            e.HasOne(s => s.Preset)
                .WithMany(l => l.Screens)
                .HasForeignKey(s => s.WallLayoutPresetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WallLayoutItem>(e =>
        {
            e.ToTable("WallLayoutItems");
            e.HasOne(i => i.Preset)
                .WithMany(l => l.Items)
                .HasForeignKey(i => i.WallLayoutPresetId)
                .OnDelete(DeleteBehavior.Cascade);
            // Una entrada de layout sin cámara no tiene sentido: si el canal
            // desaparece, la entrada se va con él.
            e.HasOne(i => i.Channel)
                .WithMany()
                .HasForeignKey(i => i.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LiveView>(e =>
        {
            e.Property(v => v.Name).HasMaxLength(128);
            e.Property(v => v.OwnerName).HasMaxLength(64);
            // 64: las divisiones con un card subdividido llevan hasta 30 cuadros de distinto tamaño en la clave.
            e.Property(v => v.LayoutName).HasMaxLength(64);
            // La vista sobrevive a la cuenta que la creó (queda el nombre
            // congelado en OwnerName): borrar un usuario no puede llevarse las
            // vistas compartidas del turno.
            e.HasOne(v => v.OwnerUser)
                .WithMany()
                .HasForeignKey(v => v.OwnerUserId)
                .OnDelete(DeleteBehavior.SetNull);
            // Nombre único por dueño: dos operadores pueden tener cada uno su
            // "Turno noche" sin pisarse.
            e.HasIndex(v => new { v.OwnerUserId, v.Name }).IsUnique();
            e.HasIndex(v => v.Shared);
        });

        modelBuilder.Entity<LiveViewItem>(e =>
        {
            e.ToTable("LiveViewItems");
            e.HasOne(i => i.View)
                .WithMany(v => v.Items)
                .HasForeignKey(i => i.LiveViewId)
                .OnDelete(DeleteBehavior.Cascade);
            // Si el canal desaparece del inventario, su cuadro se va con él y el
            // tablero abre con ese hueco (los cuadros que no son cámara no tienen canal).
            e.HasOne(i => i.Channel)
                .WithMany()
                .HasForeignKey(i => i.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(i => i.Kind).HasMaxLength(16).HasDefaultValue("camera");
            e.Property(i => i.Ref).HasMaxLength(2048);
            e.Property(i => i.Title).HasMaxLength(128);
            e.HasIndex(i => new { i.LiveViewId, i.CellIndex }).IsUnique();
        });

        modelBuilder.Entity<LiveViewRole>(e =>
        {
            e.ToTable("LiveViewRoles");
            e.HasKey(r => new { r.LiveViewId, r.RoleId });
            e.HasOne<LiveView>().WithMany(v => v.Roles).HasForeignKey(r => r.LiveViewId).OnDelete(DeleteBehavior.Cascade);
            // Borrar un rol lo saca de los tableros; si era el único, el tablero
            // queda visible solo para quien administra los tableros (RestrictToRoles).
            e.HasOne<Role>().WithMany().HasForeignKey(r => r.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebLink>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(128);
            e.Property(l => l.Url).HasMaxLength(2048);
            e.Property(l => l.Description).HasMaxLength(500);
            // Nombre único en la lista compartida y, aparte, dentro de las páginas de cada usuario.
            e.HasIndex(l => l.Name).IsUnique().HasFilter("\"OwnerUserId\" IS NULL");
            e.HasIndex(l => new { l.OwnerUserId, l.Name }).IsUnique().HasFilter("\"OwnerUserId\" IS NOT NULL");
            e.HasOne<User>().WithMany().HasForeignKey(l => l.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });

        // -------------------------------------------------------------------
        // Ubicaciones (árbol de Recursos). Cada recurso apunta a UNA ubicación
        // con su propio LocationId: borrar la ubicación lo deja "por ubicar"
        // (SET NULL) en vez de llevarse la cámara o la puerta, y borrar el
        // equipo se lleva sus recursos sin dejar referencias colgando.
        // -------------------------------------------------------------------
        modelBuilder.Entity<Location>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(128);
            e.Property(l => l.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(l => l.Description).HasMaxLength(500);
            e.Property(l => l.Address).HasMaxLength(255);
            // Una ubicación con sububicaciones no se borra: la API exige
            // vaciarla antes (mover o borrar sus hijas).
            e.HasOne(l => l.Parent)
                .WithMany(l => l.Children)
                .HasForeignKey(l => l.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Channel>().HasOne<Location>().WithMany()
            .HasForeignKey(c => c.LocationId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<AccessDoor>().HasOne<Location>().WithMany()
            .HasForeignKey(d => d.LocationId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<AlarmArea>().HasOne<Location>().WithMany()
            .HasForeignKey(a => a.LocationId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<AlarmZone>().HasOne<Location>().WithMany()
            .HasForeignKey(z => z.LocationId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<CercoPanel>().HasOne<Location>().WithMany()
            .HasForeignKey(p => p.LocationId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Speaker>().HasOne<Location>().WithMany()
            .HasForeignKey(s => s.LocationId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Intercom>().HasOne<Location>().WithMany()
            .HasForeignKey(i => i.LocationId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<IoModule>().HasOne<Location>().WithMany()
            .HasForeignKey(m => m.LocationId).OnDelete(DeleteBehavior.SetNull);
        // Ubicación de los equipos que agrupan recursos: la heredan sus canales,
        // áreas, zonas y puertas nuevas (no es la de ellos: cada uno tiene la suya).
        modelBuilder.Entity<Device>().HasOne<Location>().WithMany()
            .HasForeignKey(d => d.LocationId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<AlarmPanel>().HasOne<Location>().WithMany()
            .HasForeignKey(p => p.LocationId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<AccessDevice>().HasOne<Location>().WithMany()
            .HasForeignKey(d => d.LocationId).OnDelete(DeleteBehavior.SetNull);

        // Ficha del recurso: arco exclusivo (una sola referencia no nula, con
        // CHECK) y cascada desde el recurso, para que la ficha se vaya con él.
        modelBuilder.Entity<ResourceProfile>(e =>
        {
            e.Property(p => p.Description).HasMaxLength(1000);
            e.Property(p => p.Instructions).HasMaxLength(4000);
            e.HasOne<Channel>().WithMany().HasForeignKey(p => p.ChannelId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AccessDoor>().WithMany().HasForeignKey(p => p.AccessDoorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AlarmArea>().WithMany().HasForeignKey(p => p.AlarmAreaId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AlarmZone>().WithMany().HasForeignKey(p => p.AlarmZoneId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CercoPanel>().WithMany().HasForeignKey(p => p.CercoPanelId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Speaker>().WithMany().HasForeignKey(p => p.SpeakerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Intercom>().WithMany().HasForeignKey(p => p.IntercomId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<IoModule>().WithMany().HasForeignKey(p => p.IoModuleId).OnDelete(DeleteBehavior.Cascade);
            // Una sola ficha por recurso.
            e.HasIndex(p => p.ChannelId).IsUnique().HasFilter("\"ChannelId\" IS NOT NULL");
            e.HasIndex(p => p.AccessDoorId).IsUnique().HasFilter("\"AccessDoorId\" IS NOT NULL");
            e.HasIndex(p => p.AlarmAreaId).IsUnique().HasFilter("\"AlarmAreaId\" IS NOT NULL");
            e.HasIndex(p => p.AlarmZoneId).IsUnique().HasFilter("\"AlarmZoneId\" IS NOT NULL");
            e.HasIndex(p => p.CercoPanelId).IsUnique().HasFilter("\"CercoPanelId\" IS NOT NULL");
            e.HasIndex(p => p.SpeakerId).IsUnique().HasFilter("\"SpeakerId\" IS NOT NULL");
            e.HasIndex(p => p.IntercomId).IsUnique().HasFilter("\"IntercomId\" IS NOT NULL");
            e.HasIndex(p => p.IoModuleId).IsUnique().HasFilter("\"IoModuleId\" IS NOT NULL");
            e.ToTable(t => t.HasCheckConstraint("CK_ResourceProfiles_OneResource",
                "num_nonnulls(\"ChannelId\", \"AccessDoorId\", \"AlarmAreaId\", \"AlarmZoneId\", " +
                "\"CercoPanelId\", \"SpeakerId\", \"IntercomId\", \"IoModuleId\") = 1"));
        });

        modelBuilder.Entity<ResourceProfileCamera>(e =>
        {
            e.HasOne(c => c.Profile)
                .WithMany(p => p.Cameras)
                .HasForeignKey(c => c.ResourceProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            // Si la cámara deja de existir, deja de estar asociada.
            e.HasOne(c => c.Channel)
                .WithMany()
                .HasForeignKey(c => c.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => new { c.ResourceProfileId, c.ChannelId }).IsUnique();
        });
    }
}
