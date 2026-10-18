using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Simple.Server.Sites;

/// <summary>The site configuration's part of the database model (see SiteRecords).</summary>
public static class SiteModel
{
    public const int CodeMaxLength = 100;
    public const int NameMaxLength = 200;

    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<SiteVersionRecord>(e =>
        {
            e.ToTable("SiteVersions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityColumn();
            e.Property(x => x.Site).HasMaxLength(CodeMaxLength);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Source).HasMaxLength(20);
            e.OwnsOne(x => x.Parameters, p =>
            {
                // Durations as ticks: SQL Server's time type stops at 24 hours.
                foreach (var property in typeof(SiteParameters).GetProperties().Where(x => x.PropertyType == typeof(TimeSpan)))
                {
                    p.Property(property.Name).HasConversion<long>();
                }
            });
        });

        Row<SiteProjectRecord>(modelBuilder, "SiteProjects", x => new { x.VersionId, x.Code }, e =>
        {
            e.Property(x => x.Code).HasMaxLength(CodeMaxLength);
            e.Property(x => x.Name).HasMaxLength(NameMaxLength);
        });
        Row<SiteZoneRecord>(modelBuilder, "SiteZones", x => new { x.VersionId, x.Code }, e =>
        {
            e.Property(x => x.Code).HasMaxLength(CodeMaxLength);
            e.Property(x => x.Name).HasMaxLength(NameMaxLength);
            e.Property(x => x.Project).HasMaxLength(CodeMaxLength);
            e.Property(x => x.DemandOrder).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.WaitCap).HasConversion<long>();
        });
        Row<SiteAreaRecord>(modelBuilder, "SiteAreas", x => new { x.VersionId, x.Area }, e =>
        {
            // Areas compare exactly (ADR 0002): N1-1 and n1-1 are two areas.
            e.Property(x => x.Area).HasMaxLength(CodeMaxLength).UseCollation("Latin1_General_BIN2");
            e.Property(x => x.Zone).HasMaxLength(CodeMaxLength);
            e.Property(x => x.DoorSide).HasMaxLength(CodeMaxLength);
        });
        Row<SiteTaskTypeRecord>(modelBuilder, "SiteTaskTypes", x => new { x.VersionId, x.Code }, e =>
        {
            e.Property(x => x.Code).HasMaxLength(CodeMaxLength);
            e.Property(x => x.Name).HasMaxLength(NameMaxLength);
            e.Property(x => x.Pickup).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Unload).HasConversion<string>().HasMaxLength(20);
        });
        Row<SiteTaskTypeSwitchRecord>(modelBuilder, "SiteTaskTypeSwitches", x => new { x.VersionId, x.Project, x.TaskType }, e =>
        {
            e.Property(x => x.Project).HasMaxLength(CodeMaxLength);
            e.Property(x => x.TaskType).HasMaxLength(CodeMaxLength);
        });
        Row<SiteFixedStationRecord>(modelBuilder, "SiteFixedStations", x => new { x.VersionId, x.Project, x.TaskType, x.Map }, e =>
        {
            e.Property(x => x.Project).HasMaxLength(CodeMaxLength);
            e.Property(x => x.TaskType).HasMaxLength(CodeMaxLength);
            e.Property(x => x.AllLoaded).HasConversion<long?>();
        });
        Row<SiteVehicleRecord>(modelBuilder, "SiteVehicles", x => new { x.VersionId, x.VehicleId }, e =>
        {
            e.Property(x => x.VehicleId).HasMaxLength(CodeMaxLength);
            e.Property(x => x.RiotDeviceKey).HasMaxLength(CodeMaxLength);
            e.Property(x => x.Project).HasMaxLength(CodeMaxLength);
            e.Property(x => x.Zone).HasMaxLength(CodeMaxLength);
        });
        Row<SiteVehicleTaskTypeRecord>(modelBuilder, "SiteVehicleTaskTypes", x => new { x.VersionId, x.VehicleId, x.TaskType }, e =>
        {
            e.Property(x => x.VehicleId).HasMaxLength(CodeMaxLength);
            e.Property(x => x.TaskType).HasMaxLength(CodeMaxLength);
        });
        Row<SiteVehicleSlotGroupRecord>(modelBuilder, "SiteVehicleSlotGroups", x => new { x.VersionId, x.VehicleId, x.Position }, e =>
        {
            e.Property(x => x.VehicleId).HasMaxLength(CodeMaxLength);
            e.Property(x => x.Name).HasMaxLength(CodeMaxLength);
        });
        Row<SiteVehicleSlotRecord>(modelBuilder, "SiteVehicleSlots", x => new { x.VersionId, x.VehicleId, x.Slot }, e =>
        {
            e.Property(x => x.VehicleId).HasMaxLength(CodeMaxLength);
            e.Property(x => x.Group).HasMaxLength(CodeMaxLength);
            e.Property(x => x.Lock).HasMaxLength(10);
            e.Property(x => x.LightCurtain).HasMaxLength(10);
            e.Property(x => x.Unlock).HasMaxLength(10);
        });
        Row<SiteVehicleChargerRecord>(modelBuilder, "SiteVehicleChargers", x => new { x.VersionId, x.VehicleId, x.Position }, e =>
        {
            e.Property(x => x.VehicleId).HasMaxLength(CodeMaxLength);
        });
    }

    /// <summary>A table of one version's rows: its key, and the version it belongs to.</summary>
    private static void Row<T>(ModelBuilder modelBuilder, string table, System.Linq.Expressions.Expression<Func<T, object?>> key, Action<EntityTypeBuilder<T>> configure)
        where T : class
    {
        modelBuilder.Entity<T>(e =>
        {
            e.ToTable(table);
            e.HasKey(key);
            e.HasOne<SiteVersionRecord>().WithMany().HasForeignKey("VersionId").OnDelete(DeleteBehavior.Restrict);
            configure(e);
        });
    }
}
