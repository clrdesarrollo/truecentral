using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class VideoDeviceParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeviceId",
                table: "Speakers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeviceId",
                table: "IoModules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasSpeaker",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "HttpPort",
                table: "Devices",
                type: "integer",
                nullable: false,
                defaultValue: 80);

            migrationBuilder.AddColumn<int>(
                name: "IoInputs",
                table: "Devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IoOutputs",
                table: "Devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PartsChecked",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PartsNote",
                table: "Devices",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseIo",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseSpeaker",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_DeviceId",
                table: "Speakers",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IoModules_DeviceId",
                table: "IoModules",
                column: "DeviceId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_IoModules_Devices_DeviceId",
                table: "IoModules",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Speakers_Devices_DeviceId",
                table: "Speakers",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_IoModules_Devices_DeviceId",
                table: "IoModules");

            migrationBuilder.DropForeignKey(
                name: "FK_Speakers_Devices_DeviceId",
                table: "Speakers");

            migrationBuilder.DropIndex(
                name: "IX_Speakers_DeviceId",
                table: "Speakers");

            migrationBuilder.DropIndex(
                name: "IX_IoModules_DeviceId",
                table: "IoModules");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "IoModules");

            migrationBuilder.DropColumn(
                name: "HasSpeaker",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "HttpPort",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "IoInputs",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "IoOutputs",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PartsChecked",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PartsNote",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UseIo",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UseSpeaker",
                table: "Devices");
        }
    }
}
