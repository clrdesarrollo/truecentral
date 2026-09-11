using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AlarmPanelGatewayKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "GatewayKeyCiphertext",
                table: "AlarmPanels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GatewayProtocol",
                table: "AlarmPanels",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GatewayKeyCiphertext",
                table: "AlarmPanels");

            migrationBuilder.DropColumn(
                name: "GatewayProtocol",
                table: "AlarmPanels");
        }
    }
}
