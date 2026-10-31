using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AlertEventClip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClipPostSeconds",
                table: "WorkflowAlerts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClipPreSeconds",
                table: "WorkflowAlerts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EventAt",
                table: "WorkflowAlerts",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EventDeviceAt",
                table: "WorkflowAlerts",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EventDeviceId",
                table: "WorkflowAlerts",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClipPostSeconds",
                table: "WorkflowAlerts");

            migrationBuilder.DropColumn(
                name: "ClipPreSeconds",
                table: "WorkflowAlerts");

            migrationBuilder.DropColumn(
                name: "EventAt",
                table: "WorkflowAlerts");

            migrationBuilder.DropColumn(
                name: "EventDeviceAt",
                table: "WorkflowAlerts");

            migrationBuilder.DropColumn(
                name: "EventDeviceId",
                table: "WorkflowAlerts");
        }
    }
}
