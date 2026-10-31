using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class LocationSortOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LocationOrder",
                table: "Speakers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Locations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LocationOrder",
                table: "IoModules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationOrder",
                table: "Intercoms",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationOrder",
                table: "Channels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationOrder",
                table: "CercoPanels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationOrder",
                table: "AlarmZones",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationOrder",
                table: "AlarmAreas",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationOrder",
                table: "AccessDoors",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocationOrder",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "LocationOrder",
                table: "IoModules");

            migrationBuilder.DropColumn(
                name: "LocationOrder",
                table: "Intercoms");

            migrationBuilder.DropColumn(
                name: "LocationOrder",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "LocationOrder",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "LocationOrder",
                table: "AlarmZones");

            migrationBuilder.DropColumn(
                name: "LocationOrder",
                table: "AlarmAreas");

            migrationBuilder.DropColumn(
                name: "LocationOrder",
                table: "AccessDoors");
        }
    }
}
