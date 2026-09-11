using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AlarmPanelUniqueByGatewayDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AlarmPanels_Host_Port",
                table: "AlarmPanels");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmPanels_Host_Port",
                table: "AlarmPanels",
                columns: new[] { "Host", "Port" },
                unique: true,
                filter: "\"GatewayDeviceId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmPanels_Host_Port_GatewayDeviceId",
                table: "AlarmPanels",
                columns: new[] { "Host", "Port", "GatewayDeviceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AlarmPanels_Host_Port",
                table: "AlarmPanels");

            migrationBuilder.DropIndex(
                name: "IX_AlarmPanels_Host_Port_GatewayDeviceId",
                table: "AlarmPanels");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmPanels_Host_Port",
                table: "AlarmPanels",
                columns: new[] { "Host", "Port" },
                unique: true);
        }
    }
}
