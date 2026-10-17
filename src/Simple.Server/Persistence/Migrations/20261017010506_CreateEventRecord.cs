using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Simple.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CreateEventRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Data = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_OccurredAt",
                table: "Events",
                column: "OccurredAt");

            // The event record is append-only (ADR 0019), enforced in the
            // database so that no code path can change or remove an entry.
            migrationBuilder.Sql($"""
                CREATE TRIGGER [{SimpleDbContext.EventsAppendOnlyTrigger}] ON [Events]
                INSTEAD OF UPDATE, DELETE
                AS
                    THROW 50001, 'The event record is append-only: entries cannot be changed or removed.', 1;
                """);

            // Appends hold an exclusive application lock until commit, so ids
            // follow commit order and a reader's cursor never skips an event
            // that commits late. An insert without the lock is refused.
            migrationBuilder.Sql($"""
                CREATE TRIGGER [{SimpleDbContext.EventsAppendUnderLockTrigger}] ON [Events]
                AFTER INSERT
                AS
                    IF APPLOCK_MODE('public', N'{SimpleDbContext.EventsAppendLock}', 'Transaction') <> 'Exclusive'
                        THROW 50002, 'Events are appended under the application lock {SimpleDbContext.EventsAppendLock} so that ids follow commit order: use EventStore.AppendAsync.', 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
