using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AccessDeviceLiveEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Los equipos que ya estaban siguen como hasta ahora: con escucha en vivo.
            migrationBuilder.AddColumn<bool>(
                name: "LiveEvents",
                table: "AccessDevices",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LiveEvents",
                table: "AccessDevices");
        }
    }
}
