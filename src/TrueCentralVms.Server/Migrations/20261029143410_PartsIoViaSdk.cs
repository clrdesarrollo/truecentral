using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class PartsIoViaSdk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IoViaSdk",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IoViaSdk",
                table: "AccessDevices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Los equipos que quedaron sin entradas ni salidas se revisan otra vez: ahora también por el SDK.
            migrationBuilder.Sql("UPDATE \"Devices\" SET \"PartsChecked\" = false WHERE \"IoInputs\" IS NULL AND \"IoOutputs\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"AccessDevices\" SET \"PartsChecked\" = false WHERE \"IoInputs\" IS NULL AND \"IoOutputs\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IoViaSdk",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "IoViaSdk",
                table: "AccessDevices");
        }
    }
}
