using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Simple.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SiteVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Site = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Parameters_DepartureNothingLoaded = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_DepartureLoadingHalfway = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_DepartureBetweenSublots = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_DepartureAllLoaded = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_NoProgressAlarm = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_LockReadingStaleAfter = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_TerminalFaultAfter = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_ClassThreeRetriggerWindow = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_AutoRecoveryWait = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_HangFaultAfter = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_ResumeFaultAfter = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_ForeignOrderFaultAfter = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_FaultCancelReadBackAlarm = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_BrakeWindow = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_BrakeCount = table.Column<int>(type: "int", nullable: false),
                    Parameters_RetryMaxDelay = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_UnreachableAlarmAfter = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_MesStaleAfter = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_IoModuleFaultAfter = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_VehicleOfflineFaultAfter = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_TrafficBlockedFaultAfter = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_EmergencyStopResendMaxDelay = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_AlarmEscalateAfter = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_UrgentChargeLevel = table.Column<int>(type: "int", nullable: false),
                    Parameters_ChargeNeededLevel = table.Column<int>(type: "int", nullable: false),
                    Parameters_ReadyLevel = table.Column<int>(type: "int", nullable: false),
                    Parameters_FullLevel = table.Column<int>(type: "int", nullable: false),
                    Parameters_ChargingStallWindow = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_ChargingStallRise = table.Column<int>(type: "int", nullable: false),
                    Parameters_EventRetentionDays = table.Column<int>(type: "int", nullable: false),
                    Parameters_UnlockRequestMaxAge = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_OnboardInputMaxAge = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_ButtonsGreyAfter = table.Column<long>(type: "bigint", nullable: false),
                    Parameters_DisconnectedAfter = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SiteAreas",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "int", nullable: false),
                    Area = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Zone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true, collation: "Latin1_General_BIN2"),
                    DoorSide = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true, collation: "Latin1_General_BIN2"),
                    NotServed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteAreas", x => new { x.VersionId, x.Area });
                    table.ForeignKey(
                        name: "FK_SiteAreas_SiteVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "SiteVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SiteFixedStations",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "int", nullable: false),
                    Project = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    TaskType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Map = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Station = table.Column<int>(type: "int", nullable: false),
                    AllLoaded = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteFixedStations", x => new { x.VersionId, x.Project, x.TaskType, x.Map });
                    table.ForeignKey(
                        name: "FK_SiteFixedStations_SiteVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "SiteVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SiteProjects",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteProjects", x => new { x.VersionId, x.Code });
                    table.ForeignKey(
                        name: "FK_SiteProjects_SiteVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "SiteVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SiteTaskTypes",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Pickup = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Unload = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteTaskTypes", x => new { x.VersionId, x.Code });
                    table.ForeignKey(
                        name: "FK_SiteTaskTypes_SiteVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "SiteVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SiteTaskTypeSwitches",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "int", nullable: false),
                    Project = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    TaskType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteTaskTypeSwitches", x => new { x.VersionId, x.Project, x.TaskType });
                    table.ForeignKey(
                        name: "FK_SiteTaskTypeSwitches_SiteVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "SiteVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SiteVehicleChargers",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "int", nullable: false),
                    VehicleId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Map = table.Column<int>(type: "int", nullable: false),
                    Station = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteVehicleChargers", x => new { x.VersionId, x.VehicleId, x.Position });
                    table.ForeignKey(
                        name: "FK_SiteVehicleChargers_SiteVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "SiteVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SiteVehicles",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "int", nullable: false),
                    VehicleId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    RiotDeviceKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Project = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Zone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    WaitingMap = table.Column<int>(type: "int", nullable: false),
                    WaitingStation = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteVehicles", x => new { x.VersionId, x.VehicleId });
                    table.ForeignKey(
                        name: "FK_SiteVehicles_SiteVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "SiteVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SiteVehicleSlotGroups",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "int", nullable: false),
                    VehicleId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteVehicleSlotGroups", x => new { x.VersionId, x.VehicleId, x.Position });
                    table.ForeignKey(
                        name: "FK_SiteVehicleSlotGroups_SiteVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "SiteVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SiteVehicleSlots",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "int", nullable: false),
                    VehicleId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Slot = table.Column<int>(type: "int", nullable: false),
                    Group = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Lock = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    LightCurtain = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Unlock = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteVehicleSlots", x => new { x.VersionId, x.VehicleId, x.Slot });
                    table.ForeignKey(
                        name: "FK_SiteVehicleSlots_SiteVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "SiteVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SiteVehicleTaskTypes",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "int", nullable: false),
                    VehicleId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    TaskType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteVehicleTaskTypes", x => new { x.VersionId, x.VehicleId, x.TaskType });
                    table.ForeignKey(
                        name: "FK_SiteVehicleTaskTypes_SiteVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "SiteVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SiteZones",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Project = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                    DemandOrder = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    WaitCap = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteZones", x => new { x.VersionId, x.Code });
                    table.ForeignKey(
                        name: "FK_SiteZones_SiteVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "SiteVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiteVersions_Version",
                table: "SiteVersions",
                column: "Version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteAreas");

            migrationBuilder.DropTable(
                name: "SiteFixedStations");

            migrationBuilder.DropTable(
                name: "SiteProjects");

            migrationBuilder.DropTable(
                name: "SiteTaskTypes");

            migrationBuilder.DropTable(
                name: "SiteTaskTypeSwitches");

            migrationBuilder.DropTable(
                name: "SiteVehicleChargers");

            migrationBuilder.DropTable(
                name: "SiteVehicles");

            migrationBuilder.DropTable(
                name: "SiteVehicleSlotGroups");

            migrationBuilder.DropTable(
                name: "SiteVehicleSlots");

            migrationBuilder.DropTable(
                name: "SiteVehicleTaskTypes");

            migrationBuilder.DropTable(
                name: "SiteZones");

            migrationBuilder.DropTable(
                name: "SiteVersions");
        }
    }
}
