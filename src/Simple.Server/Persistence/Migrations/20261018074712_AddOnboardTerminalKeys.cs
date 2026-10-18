using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Simple.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardTerminalKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OnboardTerminalKeys",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VehicleId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_BIN2"),
                    KeyPin = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_BIN2"),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConfirmedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardTerminalKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnboardTerminalKeys_KeyPin",
                table: "OnboardTerminalKeys",
                column: "KeyPin",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnboardTerminalKeys_VehicleId",
                table: "OnboardTerminalKeys",
                column: "VehicleId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnboardTerminalKeys");
        }
    }
}
