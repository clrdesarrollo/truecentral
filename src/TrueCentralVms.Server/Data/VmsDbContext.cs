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
