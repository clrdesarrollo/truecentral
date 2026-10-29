using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AccessSpeakerViaSdk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SpeakerViaSdk",
                table: "AccessDevices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Los terminales que quedaron sin altavoz se revisan otra vez: ahora también se busca su voz por el SDK.
            migrationBuilder.Sql("UPDATE \"AccessDevices\" SET \"PartsChecked\" = false WHERE NOT \"HasSpeaker\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpeakerViaSdk",
                table: "AccessDevices");
        }
    }
}
