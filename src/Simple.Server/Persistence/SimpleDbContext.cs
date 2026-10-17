using Microsoft.EntityFrameworkCore;
using Simple.Server.Events;

namespace Simple.Server.Persistence;

public sealed class SimpleDbContext(DbContextOptions<SimpleDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Database trigger that rejects every UPDATE and DELETE on the event
    /// record, whichever code path issues it (created by the first migration).
    /// </summary>
    public const string EventsAppendOnlyTrigger = "TR_Events_AppendOnly";

    /// <summary>
    /// Database trigger that rejects an INSERT on the event record made
    /// without holding <see cref="EventsAppendLock"/> (see EventStore.AppendAsync).
    /// </summary>
    public const string EventsAppendUnderLockTrigger = "TR_Events_AppendUnderLock";

    /// <summary>Application lock that serializes appends to the event record.</summary>
    public const string EventsAppendLock = "Simple.Events.Append";

    public DbSet<EventRecord> Events => Set<EventRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<EventRecord>(e =>
        {
            // Declaring the trigger makes EF Core avoid OUTPUT without INTO,
            // which SQL Server refuses on a table with triggers.
            e.ToTable("Events", t =>
            {
                t.HasTrigger(EventsAppendOnlyTrigger);
                t.HasTrigger(EventsAppendUnderLockTrigger);
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityColumn();
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Source).HasMaxLength(50);
            e.HasIndex(x => x.OccurredAt);
        });
    }
}
