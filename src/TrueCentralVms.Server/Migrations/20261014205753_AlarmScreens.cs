using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AlarmScreens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AcknowledgedScreenId",
                table: "WorkflowAlerts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScreenIds",
                table: "WorkflowAlerts",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Screens",
                table: "WorkflowAlerts",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowOnScreens",
                table: "WorkflowAlerts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Las alertas existentes salieron todas en la ventana de alarma: true.
            // (Va solo en la migración y no como valor por omisión del modelo:
            // con HasDefaultValue(true), EF omitiría el false al insertar y la
            // base lo convertiría en true.)
            migrationBuilder.AddColumn<bool>(
                name: "ShowPopup",
                table: "WorkflowAlerts",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "AlarmScreens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MachineId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Hostname = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AppVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PairingCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Volume = table.Column<int>(type: "integer", nullable: false),
                    Layout = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AudioOutput = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AudioOutputsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmScreens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlarmScreenSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    SiteName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RepeatBeepSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxReminders = table.Column<int>(type: "integer", nullable: false),
                    AlarmSound = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HighPrioritySound = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReminderSound = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResyncHours = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmScreenSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlarmScreenDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlertId = table.Column<long>(type: "bigint", nullable: false),
                    ScreenId = table.Column<int>(type: "integer", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmScreenDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlarmScreenDeliveries_AlarmScreens_ScreenId",
                        column: x => x.ScreenId,
                        principalTable: "AlarmScreens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlarmScreenDeliveries_WorkflowAlerts_AlertId",
                        column: x => x.AlertId,
                        principalTable: "WorkflowAlerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmScreenDeliveries_AlertId_ScreenId",
                table: "AlarmScreenDeliveries",
                columns: new[] { "AlertId", "ScreenId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlarmScreenDeliveries_ScreenId",
                table: "AlarmScreenDeliveries",
                column: "ScreenId");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmScreens_MachineId",
                table: "AlarmScreens",
                column: "MachineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlarmScreens_TokenHash",
                table: "AlarmScreens",
                column: "TokenHash",
                unique: true,
                filter: "\"TokenHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlarmScreenDeliveries");

            migrationBuilder.DropTable(
                name: "AlarmScreenSettings");

            migrationBuilder.DropTable(
                name: "AlarmScreens");

            migrationBuilder.DropColumn(
                name: "AcknowledgedScreenId",
                table: "WorkflowAlerts");

            migrationBuilder.DropColumn(
                name: "ScreenIds",
                table: "WorkflowAlerts");

            migrationBuilder.DropColumn(
                name: "Screens",
                table: "WorkflowAlerts");

            migrationBuilder.DropColumn(
                name: "ShowOnScreens",
                table: "WorkflowAlerts");

            migrationBuilder.DropColumn(
                name: "ShowPopup",
                table: "WorkflowAlerts");
        }
    }
}
