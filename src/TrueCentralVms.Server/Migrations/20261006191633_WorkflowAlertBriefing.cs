using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowAlertBriefing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Instructions",
                table: "WorkflowAlerts",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationPath",
                table: "WorkflowAlerts",
                type: "character varying(1100)",
                maxLength: 1100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceKey",
                table: "WorkflowAlerts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAlerts_ResourceKey_RaisedAt",
                table: "WorkflowAlerts",
                columns: new[] { "ResourceKey", "RaisedAt" },
                filter: "\"ResourceKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowAlerts_ResourceKey_RaisedAt",
                table: "WorkflowAlerts");

            migrationBuilder.DropColumn(
                name: "Instructions",
                table: "WorkflowAlerts");

            migrationBuilder.DropColumn(
                name: "LocationPath",
                table: "WorkflowAlerts");

            migrationBuilder.DropColumn(
                name: "ResourceKey",
                table: "WorkflowAlerts");
        }
    }
}
