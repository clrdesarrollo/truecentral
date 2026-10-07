using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class DeviceClockMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClockAutoCorrect",
                table: "AccessDevices",
                type: "boolean",
                nullable: false,
                // Los equipos que ya existen quedan con la corrección automática
                // encendida, igual que uno nuevo.
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "DeviceClockPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NtpServer = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    NtpIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    AutoCorrect = table.Column<bool>(type: "boolean", nullable: false),
                    ThresholdSeconds = table.Column<int>(type: "integer", nullable: false),
                    CheckMinutes = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceClockPolicies", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceClockPolicies");

            migrationBuilder.DropColumn(
                name: "ClockAutoCorrect",
                table: "AccessDevices");
        }
    }
}
