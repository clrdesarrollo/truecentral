using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class LicenseQuotaMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DisabledByLicense",
                table: "Workflows",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DisabledByLicense",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DisabledByLicense",
                table: "Speakers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DisabledByLicense",
                table: "IoModules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DisabledByLicense",
                table: "Intercoms",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AnprDisabledByLicense",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DisabledByLicense",
                table: "Decoders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DisabledByLicense",
                table: "CercoPanels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DisabledByLicense",
                table: "AlarmPanels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DisabledByLicense",
                table: "AccessDevices",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisabledByLicense",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "DisabledByLicense",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DisabledByLicense",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "DisabledByLicense",
                table: "IoModules");

            migrationBuilder.DropColumn(
                name: "DisabledByLicense",
                table: "Intercoms");

            migrationBuilder.DropColumn(
                name: "AnprDisabledByLicense",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "DisabledByLicense",
                table: "Decoders");

            migrationBuilder.DropColumn(
                name: "DisabledByLicense",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "DisabledByLicense",
                table: "AlarmPanels");

            migrationBuilder.DropColumn(
                name: "DisabledByLicense",
                table: "AccessDevices");
        }
    }
}
