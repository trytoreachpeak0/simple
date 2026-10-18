using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Simple.Server.Accounts;
using Simple.Server.Events;
using Simple.Server.Listeners;
using Simple.Server.OnboardTerminals;

namespace Simple.Server.Persistence;

public sealed class SimpleDbContext(DbContextOptions<SimpleDbContext> options) : DbContext(options), IDataProtectionKeyContext
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

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<LoginRecord> Logins => Set<LoginRecord>();

    /// <summary>The keys that encrypt login cookies, each itself encrypted (see LoginSetup).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>The console's certificate, once made (ADR 0046); its key is in the Windows key store.</summary>
    public DbSet<ConsoleCertificateRecord> ConsoleCertificates => Set<ConsoleCertificateRecord>();

    /// <summary>The terminal key confirmed for each vehicle (ADR 0025).</summary>
    public DbSet<OnboardTerminalKey> OnboardTerminalKeys => Set<OnboardTerminalKey>();

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
            e.Property(x => x.Account).HasMaxLength(Account.NameMaxLength);
            e.Property(x => x.RemoteAddress).HasMaxLength(64);
            e.HasIndex(x => x.OccurredAt);
        });
        modelBuilder.Entity<Account>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(Account.NameMaxLength);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Project).HasMaxLength(Account.NameMaxLength);
            e.Property(x => x.PasswordHash).HasMaxLength(200);
        });
        modelBuilder.Entity<ConsoleCertificateRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.ToTable("ConsoleCertificates", t => t.HasCheckConstraint("CK_ConsoleCertificates_TheOne", $"[Id] = {ConsoleCertificateRecord.TheOne}"));
        });
        modelBuilder.Entity<OnboardTerminalKey>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityColumn();
            // One key per vehicle, one vehicle per key.
            // Compared exactly, as the registry compares them (ordinal).
            e.Property(x => x.VehicleId).HasMaxLength(OnboardTerminalRegistry.MaxVehicleIdLength).UseCollation("Latin1_General_BIN2");
            e.HasIndex(x => x.VehicleId).IsUnique();
            e.Property(x => x.KeyPin).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_BIN2");
            e.HasIndex(x => x.KeyPin).IsUnique();
            e.Property(x => x.ConfirmedBy).HasMaxLength(Account.NameMaxLength);
        });
        modelBuilder.Entity<LoginRecord>(e =>
        {
            e.ToTable("Logins");
            e.HasKey(x => x.KeyHash);
            // Hex compares exactly; a case-insensitive collation would not hurt it, but says the wrong thing.
            e.Property(x => x.KeyHash).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_BIN2");
            e.Property(x => x.Entry).HasMaxLength(20);
            e.Property(x => x.RemoteAddress).HasMaxLength(64);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
