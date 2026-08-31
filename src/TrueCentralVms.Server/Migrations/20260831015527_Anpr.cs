using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class Anpr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AnprEnabled",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PlateEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    ChannelNumber = table.Column<int>(type: "integer", nullable: false),
                    PlateNumber = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Confidence = table.Column<int>(type: "integer", nullable: false),
                    CharConfidences = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    PlateColor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PlateType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    VehicleType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    VehicleColor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    VehicleBrand = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SpeedKmh = table.Column<int>(type: "integer", nullable: true),
                    VehicleLengthCm = table.Column<int>(type: "integer", nullable: true),
                    Direction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Lane = table.Column<int>(type: "integer", nullable: true),
                    DetectionMethod = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Violation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PlateX = table.Column<double>(type: "double precision", nullable: false),
                    PlateY = table.Column<double>(type: "double precision", nullable: false),
                    PlateWidth = table.Column<double>(type: "double precision", nullable: false),
                    PlateHeight = table.Column<double>(type: "double precision", nullable: false),
                    SceneImagePath = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    PlateImagePath = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlateEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlateEvents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlateEvents_DeviceId_ReceivedAt",
                table: "PlateEvents",
                columns: new[] { "DeviceId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlateEvents_PlateNumber",
                table: "PlateEvents",
                column: "PlateNumber");

            migrationBuilder.CreateIndex(
                name: "IX_PlateEvents_ReceivedAt",
                table: "PlateEvents",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlateEvents");

            migrationBuilder.DropColumn(
                name: "AnprEnabled",
                table: "Devices");
        }
    }
}
