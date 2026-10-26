using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class PatrolRounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PatrolAgents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EnrollmentCodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EnrollmentExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MachineName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RemoteAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolAgents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolAgents_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PatrolCheckpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    TagId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    AccessDoorId = table.Column<int>(type: "integer", nullable: true),
                    LocationId = table.Column<int>(type: "integer", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    DisabledByLicense = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolCheckpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolCheckpoints_AccessDoors_AccessDoorId",
                        column: x => x.AccessDoorId,
                        principalTable: "AccessDoors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PatrolCheckpoints_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PatrolGuards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccessPersonId = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeactivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeactivatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DeactivationReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolGuards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolGuards_AccessPersons_AccessPersonId",
                        column: x => x.AccessPersonId,
                        principalTable: "AccessPersons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatrolReaders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Serial = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DriverKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    DisabledByLicense = table.Column<bool>(type: "boolean", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: true),
                    AgentId = table.Column<int>(type: "integer", nullable: true),
                    Docked = table.Column<bool>(type: "boolean", nullable: false),
                    DockedChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDownloadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastClockSetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastMemoryBlocks = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolReaders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolReaders_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PatrolReaders_PatrolAgents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "PatrolAgents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PatrolTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TagId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PatrolGuardId = table.Column<int>(type: "integer", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolTags_PatrolGuards_PatrolGuardId",
                        column: x => x.PatrolGuardId,
                        principalTable: "PatrolGuards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PatrolAgentCommands",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AgentId = table.Column<int>(type: "integer", nullable: false),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: true),
                    Message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolAgentCommands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolAgentCommands_PatrolAgents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "PatrolAgents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PatrolAgentCommands_PatrolReaders_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "PatrolReaders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatrolDownloads",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<int>(type: "integer", nullable: true),
                    Origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Blocks = table.Column<int>(type: "integer", nullable: false),
                    Reads = table.Column<int>(type: "integer", nullable: false),
                    NewReads = table.Column<int>(type: "integer", nullable: false),
                    NewPasses = table.Column<int>(type: "integer", nullable: false),
                    ClockSet = table.Column<bool>(type: "boolean", nullable: false),
                    Erased = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolDownloads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolDownloads_PatrolReaders_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "PatrolReaders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatrolRawReads",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReaderId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DownloadId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolRawReads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolRawReads_PatrolDownloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "PatrolDownloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PatrolRawReads_PatrolReaders_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "PatrolReaders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatrolPasses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CheckpointId = table.Column<int>(type: "integer", nullable: false),
                    PatrolGuardId = table.Column<int>(type: "integer", nullable: true),
                    PatrolTagId = table.Column<int>(type: "integer", nullable: true),
                    GuardName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    GuardEmployeeNo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReaderId = table.Column<int>(type: "integer", nullable: true),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CheckpointReadId = table.Column<long>(type: "bigint", nullable: true),
                    GuardReadId = table.Column<long>(type: "bigint", nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Voided = table.Column<bool>(type: "boolean", nullable: false),
                    VoidReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    EditedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EditReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatrolPasses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatrolPasses_PatrolCheckpoints_CheckpointId",
                        column: x => x.CheckpointId,
                        principalTable: "PatrolCheckpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatrolPasses_PatrolGuards_PatrolGuardId",
                        column: x => x.PatrolGuardId,
                        principalTable: "PatrolGuards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PatrolPasses_PatrolRawReads_CheckpointReadId",
                        column: x => x.CheckpointReadId,
                        principalTable: "PatrolRawReads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PatrolPasses_PatrolRawReads_GuardReadId",
                        column: x => x.GuardReadId,
                        principalTable: "PatrolRawReads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PatrolPasses_PatrolReaders_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "PatrolReaders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PatrolPasses_PatrolTags_PatrolTagId",
                        column: x => x.PatrolTagId,
                        principalTable: "PatrolTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolAgentCommands_AgentId_CompletedAt",
                table: "PatrolAgentCommands",
                columns: new[] { "AgentId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolAgentCommands_ReaderId",
                table: "PatrolAgentCommands",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolAgents_EnrollmentCodeHash",
                table: "PatrolAgents",
                column: "EnrollmentCodeHash",
                filter: "\"EnrollmentCodeHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolAgents_LocationId",
                table: "PatrolAgents",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolAgents_TokenHash",
                table: "PatrolAgents",
                column: "TokenHash",
                unique: true,
                filter: "\"TokenHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolCheckpoints_AccessDoorId",
                table: "PatrolCheckpoints",
                column: "AccessDoorId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolCheckpoints_LocationId",
                table: "PatrolCheckpoints",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolCheckpoints_TagId",
                table: "PatrolCheckpoints",
                column: "TagId",
                unique: true,
                filter: "\"TagId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolDownloads_ReaderId_StartedAt",
                table: "PatrolDownloads",
                columns: new[] { "ReaderId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolGuards_AccessPersonId",
                table: "PatrolGuards",
                column: "AccessPersonId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatrolPasses_CheckpointId_ReadAt",
                table: "PatrolPasses",
                columns: new[] { "CheckpointId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolPasses_CheckpointReadId",
                table: "PatrolPasses",
                column: "CheckpointReadId",
                unique: true,
                filter: "\"CheckpointReadId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolPasses_GuardReadId",
                table: "PatrolPasses",
                column: "GuardReadId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolPasses_PatrolGuardId_ReadAt",
                table: "PatrolPasses",
                columns: new[] { "PatrolGuardId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolPasses_PatrolTagId",
                table: "PatrolPasses",
                column: "PatrolTagId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolPasses_ReadAt",
                table: "PatrolPasses",
                column: "ReadAt");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolPasses_ReaderId",
                table: "PatrolPasses",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRawReads_DownloadId",
                table: "PatrolRawReads",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRawReads_ReaderId_ReadAt",
                table: "PatrolRawReads",
                columns: new[] { "ReaderId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRawReads_ReaderId_TagId_ReadAt",
                table: "PatrolRawReads",
                columns: new[] { "ReaderId", "TagId", "ReadAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatrolRawReads_TagId",
                table: "PatrolRawReads",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolReaders_AgentId",
                table: "PatrolReaders",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolReaders_LocationId",
                table: "PatrolReaders",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolReaders_Serial",
                table: "PatrolReaders",
                column: "Serial",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatrolTags_PatrolGuardId",
                table: "PatrolTags",
                column: "PatrolGuardId");

            migrationBuilder.CreateIndex(
                name: "IX_PatrolTags_TagId",
                table: "PatrolTags",
                column: "TagId",
                unique: true);

            // El operador ve la bitácora de rondas y la exporta (OperatorDefaults
            // solo vale en instalaciones nuevas).
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleId", "Permission")
                SELECT r."Id", p FROM "Roles" r, unnest(ARRAY['patrol.view', 'patrol.export']) p
                WHERE r."SystemKey" = 'operator'
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM "RolePermissions" WHERE "Permission" LIKE 'patrol.%';""");

            migrationBuilder.DropTable(
                name: "PatrolAgentCommands");

            migrationBuilder.DropTable(
                name: "PatrolPasses");

            migrationBuilder.DropTable(
                name: "PatrolCheckpoints");

            migrationBuilder.DropTable(
                name: "PatrolRawReads");

            migrationBuilder.DropTable(
                name: "PatrolTags");

            migrationBuilder.DropTable(
                name: "PatrolDownloads");

            migrationBuilder.DropTable(
                name: "PatrolGuards");

            migrationBuilder.DropTable(
                name: "PatrolReaders");

            migrationBuilder.DropTable(
                name: "PatrolAgents");
        }
    }
}
