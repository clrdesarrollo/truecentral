using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class FleetEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FleetEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VehicleId = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: true),
                    GeofenceId = table.Column<int>(type: "integer", nullable: true),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    SpeedKmh = table.Column<float>(type: "real", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedByUserId = table.Column<int>(type: "integer", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AckNote = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FleetEvents_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Geofences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PointsJson = table.Column<string>(type: "text", nullable: false),
                    RadiusM = table.Column<double>(type: "double precision", nullable: false),
                    Color = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AlertOnEnter = table.Column<bool>(type: "boolean", nullable: false),
                    AlertOnExit = table.Column<bool>(type: "boolean", nullable: false),
                    SpeedLimitKmh = table.Column<int>(type: "integer", nullable: true),
                    VehicleIdsJson = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Geofences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FleetEvents_AcknowledgedAt",
                table: "FleetEvents",
                column: "AcknowledgedAt",
                filter: "\"AcknowledgedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FleetEvents_At",
                table: "FleetEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_FleetEvents_VehicleId_At",
                table: "FleetEvents",
                columns: new[] { "VehicleId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Geofences_Name",
                table: "Geofences",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FleetEvents");

            migrationBuilder.DropTable(
                name: "Geofences");
        }
    }
}
