using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowAlertRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecipientUserIds",
                table: "WorkflowAlerts",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Recipients",
                table: "WorkflowAlerts",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecipientUserIds",
                table: "WorkflowAlerts");

            migrationBuilder.DropColumn(
                name: "Recipients",
                table: "WorkflowAlerts");
        }
    }
}
