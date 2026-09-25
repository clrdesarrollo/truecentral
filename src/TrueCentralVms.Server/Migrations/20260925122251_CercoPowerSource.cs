using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class CercoPowerSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PowerDropPermille",
                table: "CercoPanels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PowerSource",
                table: "CercoPanels",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<int>(
                name: "ReturnUs",
                table: "CercoPanels",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PowerDropPermille",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "PowerSource",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "ReturnUs",
                table: "CercoPanels");
        }
    }
}
