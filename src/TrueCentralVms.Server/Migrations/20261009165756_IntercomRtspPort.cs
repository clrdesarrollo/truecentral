using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class IntercomRtspPort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Los frentes ya registrados quedan con el RTSP de fábrica (su cámara
            // propia pasa a verse sin registrarla en Fuentes de video).
            migrationBuilder.AddColumn<int>(
                name: "RtspPort",
                table: "Intercoms",
                type: "integer",
                nullable: false,
                defaultValue: 554);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RtspPort",
                table: "Intercoms");
        }
    }
}
