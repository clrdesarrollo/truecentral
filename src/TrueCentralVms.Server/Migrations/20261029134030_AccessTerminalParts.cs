using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AccessTerminalParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CameraNote",
                table: "AccessDevices",
                newName: "PartsNote");

            // CameraChecked pasa a ser PartsChecked; se vuelve a preguntar a todos los terminales
            // (ahora también por su altavoz y sus entradas y salidas). Su cámara no se duplica.
            migrationBuilder.RenameColumn(
                name: "CameraChecked",
                table: "AccessDevices",
                newName: "PartsChecked");
            migrationBuilder.Sql("UPDATE \"AccessDevices\" SET \"PartsChecked\" = false;");

            // Los terminales que ya estaban ofrecen su altavoz y sus E/S apenas se sepa que los tienen.
            migrationBuilder.AddColumn<bool>(
                name: "UseSpeaker",
                table: "AccessDevices",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "AccessDeviceId",
                table: "Speakers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AccessDeviceId",
                table: "IoModules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PasswordCiphertext",
                table: "IoModules",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<bool>(
                name: "UseHttps",
                table: "IoModules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "IoModules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "HasSpeaker",
                table: "AccessDevices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "IoInputs",
                table: "AccessDevices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IoOutputs",
                table: "AccessDevices",
                type: "integer",
                nullable: true);


            migrationBuilder.AddColumn<bool>(
                name: "UseIo",
                table: "AccessDevices",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_AccessDeviceId",
                table: "Speakers",
                column: "AccessDeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IoModules_AccessDeviceId",
                table: "IoModules",
                column: "AccessDeviceId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_IoModules_AccessDevices_AccessDeviceId",
                table: "IoModules",
                column: "AccessDeviceId",
                principalTable: "AccessDevices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Speakers_AccessDevices_AccessDeviceId",
                table: "Speakers",
                column: "AccessDeviceId",
                principalTable: "AccessDevices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_IoModules_AccessDevices_AccessDeviceId",
                table: "IoModules");

            migrationBuilder.DropForeignKey(
                name: "FK_Speakers_AccessDevices_AccessDeviceId",
                table: "Speakers");

            migrationBuilder.DropIndex(
                name: "IX_Speakers_AccessDeviceId",
                table: "Speakers");

            migrationBuilder.DropIndex(
                name: "IX_IoModules_AccessDeviceId",
                table: "IoModules");

            migrationBuilder.DropColumn(
                name: "AccessDeviceId",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "AccessDeviceId",
                table: "IoModules");

            migrationBuilder.DropColumn(
                name: "PasswordCiphertext",
                table: "IoModules");

            migrationBuilder.DropColumn(
                name: "UseHttps",
                table: "IoModules");

            migrationBuilder.DropColumn(
                name: "Username",
                table: "IoModules");

            migrationBuilder.DropColumn(
                name: "HasSpeaker",
                table: "AccessDevices");

            migrationBuilder.DropColumn(
                name: "IoInputs",
                table: "AccessDevices");

            migrationBuilder.DropColumn(
                name: "IoOutputs",
                table: "AccessDevices");

            migrationBuilder.DropColumn(
                name: "UseSpeaker",
                table: "AccessDevices");

            migrationBuilder.DropColumn(
                name: "UseIo",
                table: "AccessDevices");

            migrationBuilder.RenameColumn(
                name: "PartsChecked",
                table: "AccessDevices",
                newName: "CameraChecked");

            migrationBuilder.RenameColumn(
                name: "PartsNote",
                table: "AccessDevices",
                newName: "CameraNote");
        }
    }
}
