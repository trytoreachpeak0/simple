using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Simple.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConsoleCertificate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsoleCertificates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Certificate = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsoleCertificates", x => x.Id);
                    table.CheckConstraint("CK_ConsoleCertificates_TheOne", "[Id] = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsoleCertificates");
        }
    }
}
