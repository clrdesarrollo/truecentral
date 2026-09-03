using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AlarmPanels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlarmEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlarmPanelId = table.Column<int>(type: "integer", nullable: false),
                    PanelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AreaNumber = table.Column<int>(type: "integer", nullable: true),
                    AreaName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ZoneNumber = table.Column<int>(type: "integer", nullable: true),
                    ZoneName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Operator = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    RawJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlarmPanels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DriverKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    UseHttps = table.Column<bool>(type: "boolean", nullable: false),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PasswordCiphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SerialNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FirmwareVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStateAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmPanels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlarmAreas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlarmPanelId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ArmState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    InAlarm = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmAreas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlarmAreas_AlarmPanels_AlarmPanelId",
                        column: x => x.AlarmPanelId,
                        principalTable: "AlarmPanels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlarmZones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlarmPanelId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    AreaNumber = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ZoneType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    DetectorType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Bypassed = table.Column<bool>(type: "boolean", nullable: false),
                    Armed = table.Column<bool>(type: "boolean", nullable: false),
                    InAlarm = table.Column<bool>(type: "boolean", nullable: false),
                    Tamper = table.Column<bool>(type: "boolean", nullable: false),
                    LowBattery = table.Column<bool>(type: "boolean", nullable: false),
                    Signal = table.Column<int>(type: "integer", nullable: true),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmZones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlarmZones_AlarmPanels_AlarmPanelId",
                        column: x => x.AlarmPanelId,
                        principalTable: "AlarmPanels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmAreas_AlarmPanelId_Number",
                table: "AlarmAreas",
                columns: new[] { "AlarmPanelId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_AlarmPanelId_ReceivedAt",
                table: "AlarmEvents",
                columns: new[] { "AlarmPanelId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_Kind_ReceivedAt",
                table: "AlarmEvents",
                columns: new[] { "Kind", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_ReceivedAt",
                table: "AlarmEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmPanels_Host_Port",
                table: "AlarmPanels",
                columns: new[] { "Host", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlarmZones_AlarmPanelId_Number",
                table: "AlarmZones",
                columns: new[] { "AlarmPanelId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlarmAreas");

            migrationBuilder.DropTable(
                name: "AlarmEvents");

            migrationBuilder.DropTable(
                name: "AlarmZones");

            migrationBuilder.DropTable(
                name: "AlarmPanels");
        }
    }
}
