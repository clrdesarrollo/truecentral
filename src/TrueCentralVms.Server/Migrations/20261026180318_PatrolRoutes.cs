using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class PatrolRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PatrolRoutes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    OrderMode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ReminderMinutes = table.Column<int>(type: "integer", nullable: false),
                    StartWindowMinutes = table.Column<int>(type: "integer", nullable: false),
                    RepeatMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolRoutes_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PatrolRouteAgents",
                columns: table => new
                {
                    RouteId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolRouteAgents", x => new { x.RouteId, x.AgentId });
                    table.ForeignKey(
                        name: "FK_PatrolRouteAgents_PatrolAgents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "PatrolAgents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PatrolRouteAgents_PatrolRoutes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "PatrolRoutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatrolRouteStops",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RouteId = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    CheckpointId = table.Column<int>(type: "integer", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    OffsetMinutes = table.Column<int>(type: "integer", nullable: true),
                    ToleranceMinutes = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolRouteStops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolRouteStops_PatrolCheckpoints_CheckpointId",
                        column: x => x.CheckpointId,
                        principalTable: "PatrolCheckpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatrolRouteStops_PatrolRoutes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "PatrolRoutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatrolSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RouteId = table.Column<int>(type: "integer", nullable: false),
                    DaysMask = table.Column<int>(type: "integer", nullable: false),
                    StartMinutes = table.Column<int>(type: "integer", nullable: false),
                    SkipHolidays = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolSchedules_PatrolRoutes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "PatrolRoutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatrolRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RouteId = table.Column<int>(type: "integer", nullable: false),
                    ScheduleId = table.Column<int>(type: "integer", nullable: true),
                    ExpectedStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartDeadline = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DueEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActualStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActualEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReaderId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Cause = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LastReminderAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EvaluatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolRuns_PatrolReaders_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "PatrolReaders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PatrolRuns_PatrolRoutes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "PatrolRoutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PatrolRuns_PatrolSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "PatrolSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PatrolRunStops",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    CheckpointId = table.Column<int>(type: "integer", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    ExpectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ToleranceMinutes = table.Column<int>(type: "integer", nullable: true),
                    PassId = table.Column<long>(type: "bigint", nullable: true),
                    ActualAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolRunStops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolRunStops_PatrolCheckpoints_CheckpointId",
                        column: x => x.CheckpointId,
                        principalTable: "PatrolCheckpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatrolRunStops_PatrolPasses_PassId",
                        column: x => x.PassId,
                        principalTable: "PatrolPasses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PatrolRunStops_PatrolRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "PatrolRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRouteAgents_AgentId",
                table: "PatrolRouteAgents",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRoutes_LocationId",
                table: "PatrolRoutes",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRouteStops_CheckpointId",
                table: "PatrolRouteStops",
                column: "CheckpointId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRouteStops_RouteId_Sequence",
                table: "PatrolRouteStops",
                columns: new[] { "RouteId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRuns_ReaderId",
                table: "PatrolRuns",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRuns_RouteId_ExpectedStart",
                table: "PatrolRuns",
                columns: new[] { "RouteId", "ExpectedStart" });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRuns_ScheduleId_ExpectedStart",
                table: "PatrolRuns",
                columns: new[] { "ScheduleId", "ExpectedStart" },
                unique: true,
                filter: "\"ScheduleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRuns_Status_ExpectedStart",
                table: "PatrolRuns",
                columns: new[] { "Status", "ExpectedStart" });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRunStops_CheckpointId",
                table: "PatrolRunStops",
                column: "CheckpointId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRunStops_PassId",
                table: "PatrolRunStops",
                column: "PassId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRunStops_RunId_Sequence",
                table: "PatrolRunStops",
                columns: new[] { "RunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatrolSchedules_RouteId",
                table: "PatrolSchedules",
                column: "RouteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatrolRouteAgents");

            migrationBuilder.DropTable(
                name: "PatrolRouteStops");

            migrationBuilder.DropTable(
                name: "PatrolRunStops");

            migrationBuilder.DropTable(
                name: "PatrolRuns");

            migrationBuilder.DropTable(
                name: "PatrolSchedules");

            migrationBuilder.DropTable(
                name: "PatrolRoutes");
        }
    }
}
