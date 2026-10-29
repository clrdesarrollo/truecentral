using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AccessTerminalCamera : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccessDeviceId",
                table: "Devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CameraChecked",
                table: "AccessDevices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CameraNote",
                table: "AccessDevices",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CameraRtspPort",
                table: "AccessDevices",
                type: "integer",
                nullable: true);

            // Los terminales que ya estaban muestran su cámara apenas se sepa que la tienen
            // (el servidor se lo pregunta en su primer sondeo: CameraChecked).
            migrationBuilder.AddColumn<bool>(
                name: "UseCamera",
                table: "AccessDevices",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_AccessDeviceId",
                table: "Devices",
                column: "AccessDeviceId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_AccessDevices_AccessDeviceId",
                table: "Devices",
                column: "AccessDeviceId",
                principalTable: "AccessDevices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Devices_AccessDevices_AccessDeviceId",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Devices_AccessDeviceId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "AccessDeviceId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "CameraChecked",
                table: "AccessDevices");

            migrationBuilder.DropColumn(
                name: "CameraNote",
                table: "AccessDevices");

            migrationBuilder.DropColumn(
                name: "CameraRtspPort",
                table: "AccessDevices");

            migrationBuilder.DropColumn(
                name: "UseCamera",
                table: "AccessDevices");
        }
    }
}
