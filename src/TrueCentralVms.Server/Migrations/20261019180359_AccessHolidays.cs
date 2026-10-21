using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AccessHolidays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HolidayMode",
                table: "AccessSchedules",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.AddColumn<string>(
                name: "AppliedHolidayHash",
                table: "AccessDevices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HolidayNote",
                table: "AccessDevices",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccessHolidays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Yearly = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessHolidays", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessHolidays_StartDate",
                table: "AccessHolidays",
                column: "StartDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessHolidays");

            migrationBuilder.DropColumn(
                name: "HolidayMode",
                table: "AccessSchedules");

            migrationBuilder.DropColumn(
                name: "AppliedHolidayHash",
                table: "AccessDevices");

            migrationBuilder.DropColumn(
                name: "HolidayNote",
                table: "AccessDevices");
        }
    }
}
