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
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<StreamSession> StreamSessions => Set<StreamSession>();

    /// <summary>Reconocimientos de patentes (módulo Aplicaciones → ANPR).</summary>
    public DbSet<PlateEvent> PlateEvents => Set<PlateEvent>();

    /// <summary>Bitácora de auditoría (solo-agregar; ver Services\AuditService).</summary>
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    // Paneles de alarma (centrales de intrusión)
    public DbSet<AlarmPanel> AlarmPanels => Set<AlarmPanel>();
    public DbSet<AlarmArea> AlarmAreas => Set<AlarmArea>();
    public DbSet<AlarmZone> AlarmZones => Set<AlarmZone>();
    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();

    // Parlantes IP
    public DbSet<Speaker> Speakers => Set<Speaker>();

    // Automatizaciones (workflows): disparador + acciones, y su historial
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowAction> WorkflowActions => Set<WorkflowAction>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    /// <summary>Alertas mostradas a los operadores y su acuse de recibo.</summary>
    public DbSet<WorkflowAlert> WorkflowAlerts => Set<WorkflowAlert>();
    /// <summary>Servidor de correo saliente: fila única (Id = 1).</summary>
    public DbSet<SmtpSettings> SmtpSettings => Set<SmtpSettings>();

    // Muro de video
    public DbSet<Decoder> Decoders => Set<Decoder>();
    public DbSet<VideoWall> Walls => Set<VideoWall>();
    public DbSet<WallScreen> WallScreens => Set<WallScreen>();
    public DbSet<ScreenWindow> ScreenWindows => Set<ScreenWindow>();
    public DbSet<WallFloatingWindow> WallFloatingWindows => Set<WallFloatingWindow>();
    public DbSet<WallLayoutPreset> WallLayouts => Set<WallLayoutPreset>();

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
            e.HasIndex(d => new { d.Host, d.SdkPort }).IsUnique();
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
            e.Property(s => s.Name).HasMaxLength(128);
            e.Property(s => s.DriverKey).HasMaxLength(32);
            e.Property(s => s.Host).HasMaxLength(255);
            e.Property(s => s.Username).HasMaxLength(64);
            e.Property(s => s.GroupName).HasMaxLength(64);
            e.Property(s => s.Model).HasMaxLength(64);
            e.Property(s => s.SerialNumber).HasMaxLength(64);
            e.Property(s => s.FirmwareVersion).HasMaxLength(64);
            e.Property(s => s.LastError).HasMaxLength(512);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(s => new { s.Host, s.Port }).IsUnique();
        });

        modelBuilder.Entity<AlarmPanel>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(128);
            e.Property(p => p.DriverKey).HasMaxLength(32);
            e.Property(p => p.Host).HasMaxLength(255);
            e.Property(p => p.Username).HasMaxLength(64);
            e.Property(p => p.GatewayDeviceId).HasMaxLength(128);
            e.Property(p => p.Model).HasMaxLength(64);
            e.Property(p => p.SerialNumber).HasMaxLength(64);
            e.Property(p => p.FirmwareVersion).HasMaxLength(64);
            e.Property(p => p.LastError).HasMaxLength(512);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(p => new { p.Host, p.Port }).IsUnique();
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

        // -------------------------------------------------------------------
        // Automatizaciones (workflows)
        // -------------------------------------------------------------------
        modelBuilder.Entity<Workflow>(e =>
        {
            e.Property(w => w.Name).HasMaxLength(128);
            e.Property(w => w.Description).HasMaxLength(512);
            e.Property(w => w.TriggerType).HasMaxLength(32);
            e.Property(w => w.CreatedBy).HasMaxLength(64);
            e.HasIndex(w => w.Name).IsUnique();
            // El motor consulta las habilitadas de un disparador en cada evento.
            e.HasIndex(w => new { w.TriggerType, w.Enabled });
        });

        modelBuilder.Entity<WorkflowAction>(e =>
        {
            e.Property(a => a.Type).HasMaxLength(32);
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
            // Las pendientes se consultan en cada arranque de cliente; el
            // registro se lee por fecha descendente.
            e.HasIndex(a => a.RaisedAt);
            e.HasIndex(a => new { a.AcknowledgedAt, a.RaisedAt });
        });

        modelBuilder.Entity<SmtpSettings>(e =>
        {
            e.Property(s => s.Host).HasMaxLength(255);
            e.Property(s => s.Username).HasMaxLength(128);
            e.Property(s => s.FromAddress).HasMaxLength(255);
            e.Property(s => s.FromName).HasMaxLength(128);
            e.Property(s => s.Security).HasConversion<string>().HasMaxLength(16);
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
    }
}
