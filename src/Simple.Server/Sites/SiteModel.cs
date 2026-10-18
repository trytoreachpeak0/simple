using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Simple.Server.Sites;

/// <summary>The site configuration's part of the database model (see SiteRecords).</summary>
public static class SiteModel
{
    public const int CodeMaxLength = 100;
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;

    /// <summary>
    /// Codes and ids compare exactly, as the validation compares them:
    /// <c>agv01</c> and <c>AGV01</c> are two vehicles, <c>N1-1</c> and
    /// <c>n1-1</c> two areas (ADR 0002). SQL Server's default collation and EF
    /// Core's key comparison of SQL Server strings both ignore case, so both
    /// are set.
    /// </summary>
    private const string ExactCollation = "Latin1_General_BIN2";

    private static readonly ValueComparer<string> Ordinal = new(
        (a, b) => string.Equals(a, b, StringComparison.Ordinal),
        v => StringComparer.Ordinal.GetHashCode(v),
        v => v);

    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<SiteVersionRecord>(e =>
        {
            e.ToTable("SiteVersions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityColumn();
            e.HasIndex(x => x.Version).IsUnique();
            Code(e.Property(x => x.Site));
            e.Property(x => x.Description).HasMaxLength(DescriptionMaxLength);
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
            Code(e.Property(x => x.Code));
            e.Property(x => x.Name).HasMaxLength(NameMaxLength);
        });
        Row<SiteZoneRecord>(modelBuilder, "SiteZones", x => new { x.VersionId, x.Code }, e =>
        {
            Code(e.Property(x => x.Code));
            e.Property(x => x.Name).HasMaxLength(NameMaxLength);
            Code(e.Property(x => x.Project));
            e.Property(x => x.DemandOrder).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.WaitCap).HasConversion<long>();
        });
        Row<SiteAreaRecord>(modelBuilder, "SiteAreas", x => new { x.VersionId, x.Area }, e =>
        {
            Code(e.Property(x => x.Area));
            Code(e.Property(x => x.Zone!));
            Code(e.Property(x => x.DoorSide!));
        });
        Row<SiteTaskTypeRecord>(modelBuilder, "SiteTaskTypes", x => new { x.VersionId, x.Code }, e =>
        {
            Code(e.Property(x => x.Code));
            e.Property(x => x.Name).HasMaxLength(NameMaxLength);
            e.Property(x => x.Pickup).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Unload).HasConversion<string>().HasMaxLength(20);
        });
        Row<SiteTaskTypeSwitchRecord>(modelBuilder, "SiteTaskTypeSwitches", x => new { x.VersionId, x.Project, x.TaskType }, e =>
        {
            Code(e.Property(x => x.Project));
            Code(e.Property(x => x.TaskType));
        });
        Row<SiteFixedStationRecord>(modelBuilder, "SiteFixedStations", x => new { x.VersionId, x.Project, x.TaskType, x.Map }, e =>
        {
            Code(e.Property(x => x.Project));
            Code(e.Property(x => x.TaskType));
            e.Property(x => x.AllLoaded).HasConversion<long?>();
        });
        Row<SiteVehicleRecord>(modelBuilder, "SiteVehicles", x => new { x.VersionId, x.VehicleId }, e =>
        {
            Code(e.Property(x => x.VehicleId));
            Code(e.Property(x => x.RiotDeviceKey));
            Code(e.Property(x => x.Project));
            Code(e.Property(x => x.Zone));
        });
        Row<SiteVehicleTaskTypeRecord>(modelBuilder, "SiteVehicleTaskTypes", x => new { x.VersionId, x.VehicleId, x.TaskType }, e =>
        {
            Code(e.Property(x => x.VehicleId));
            Code(e.Property(x => x.TaskType));
        });
        Row<SiteVehicleSlotGroupRecord>(modelBuilder, "SiteVehicleSlotGroups", x => new { x.VersionId, x.VehicleId, x.Position }, e =>
        {
            Code(e.Property(x => x.VehicleId));
            Code(e.Property(x => x.Name));
        });
        Row<SiteVehicleSlotRecord>(modelBuilder, "SiteVehicleSlots", x => new { x.VersionId, x.VehicleId, x.Slot }, e =>
        {
            Code(e.Property(x => x.VehicleId));
            Code(e.Property(x => x.Group));
            e.Property(x => x.Lock).HasMaxLength(10);
            e.Property(x => x.LightCurtain).HasMaxLength(10);
            e.Property(x => x.Unlock).HasMaxLength(10);
        });
        Row<SiteVehicleChargerRecord>(modelBuilder, "SiteVehicleChargers", x => new { x.VersionId, x.VehicleId, x.Position }, e =>
        {
            Code(e.Property(x => x.VehicleId));
        });
    }

    private static void Code(PropertyBuilder<string> property) =>
        property.HasMaxLength(CodeMaxLength).UseCollation(ExactCollation).Metadata.SetValueComparer(Ordinal);

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
