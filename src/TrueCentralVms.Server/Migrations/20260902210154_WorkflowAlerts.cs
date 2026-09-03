using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<long>(type: "bigint", nullable: true),
                    WorkflowId = table.Column<int>(type: "integer", nullable: false),
                    WorkflowName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ImagePath = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Sound = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SoundRepeat = table.Column<int>(type: "integer", nullable: false),
                    TriggerSummary = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequiresAck = table.Column<bool>(type: "boolean", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedByUserId = table.Column<int>(type: "integer", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AcknowledgedFrom = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    AcknowledgedIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowAlerts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAlerts_AcknowledgedAt_RaisedAt",
                table: "WorkflowAlerts",
                columns: new[] { "AcknowledgedAt", "RaisedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAlerts_RaisedAt",
                table: "WorkflowAlerts",
                column: "RaisedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowAlerts");
        }
    }
}
