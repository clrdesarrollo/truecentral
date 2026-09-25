using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class CercoPanels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CercoEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CercoPanelId = table.Column<int>(type: "integer", nullable: false),
                    PanelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ZoneNumber = table.Column<int>(type: "integer", nullable: true),
                    ZoneName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Verified = table.Column<bool>(type: "boolean", nullable: false),
                    RawJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CercoEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CercoPanels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    PskCiphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    Site = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Armed = table.Column<bool>(type: "boolean", nullable: false),
                    Siren = table.Column<bool>(type: "boolean", nullable: false),
                    HvOk = table.Column<bool>(type: "boolean", nullable: false),
                    FenceOk = table.Column<bool>(type: "boolean", nullable: false),
                    ArcFault = table.Column<bool>(type: "boolean", nullable: false),
                    Voltage = table.Column<int>(type: "integer", nullable: true),
                    GroundMs = table.Column<int>(type: "integer", nullable: true),
                    Equipo = table.Column<int>(type: "integer", nullable: true),
                    Rssi = table.Column<int>(type: "integer", nullable: true),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Firmware = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Mac = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStateAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CmdSeq = table.Column<long>(type: "bigint", nullable: false),
                    LastEventSeq = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CercoPanels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CercoZones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CercoPanelId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    InAlarm = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CercoZones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CercoZones_CercoPanels_CercoPanelId",
                        column: x => x.CercoPanelId,
                        principalTable: "CercoPanels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CercoEvents_CercoPanelId_ReceivedAt",
                table: "CercoEvents",
                columns: new[] { "CercoPanelId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CercoEvents_Kind_ReceivedAt",
                table: "CercoEvents",
                columns: new[] { "Kind", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CercoEvents_ReceivedAt",
                table: "CercoEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CercoPanels_DeviceId",
                table: "CercoPanels",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CercoZones_CercoPanelId_Number",
                table: "CercoZones",
                columns: new[] { "CercoPanelId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CercoEvents");

            migrationBuilder.DropTable(
                name: "CercoZones");

            migrationBuilder.DropTable(
                name: "CercoPanels");
        }
    }
}
