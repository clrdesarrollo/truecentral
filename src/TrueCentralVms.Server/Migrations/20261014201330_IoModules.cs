using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class IoModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ResourceProfiles_OneResource",
                table: "ResourceProfiles");

            migrationBuilder.AddColumn<int>(
                name: "IoModuleId",
                table: "ResourceProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IoModules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DriverKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    ModelKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Model = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ReportedModel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PollIntervalMs = table.Column<int>(type: "integer", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IoModules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IoModules_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "IoPoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IoModuleId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Inverted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IoPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IoPoints_IoModules_IoModuleId",
                        column: x => x.IoModuleId,
                        principalTable: "IoModules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceProfiles_IoModuleId",
                table: "ResourceProfiles",
                column: "IoModuleId",
                unique: true,
                filter: "\"IoModuleId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ResourceProfiles_OneResource",
                table: "ResourceProfiles",
                sql: "num_nonnulls(\"ChannelId\", \"AccessDoorId\", \"AlarmAreaId\", \"AlarmZoneId\", \"CercoPanelId\", \"SpeakerId\", \"IntercomId\", \"IoModuleId\") = 1");

            migrationBuilder.CreateIndex(
                name: "IX_IoModules_Host_Port",
                table: "IoModules",
                columns: new[] { "Host", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IoModules_LocationId",
                table: "IoModules",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_IoPoints_IoModuleId_Kind_Number",
                table: "IoPoints",
                columns: new[] { "IoModuleId", "Kind", "Number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ResourceProfiles_IoModules_IoModuleId",
                table: "ResourceProfiles",
                column: "IoModuleId",
                principalTable: "IoModules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // El operador abre puertas a distancia: también opera los relés
            // (barreras, portones). Se agrega al rol de sistema Operador, que en
            // las instalaciones nuevas y en las existentes ya viene de UserRoles.
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleId", "Permission")
                SELECT r."Id", 'io.operate' FROM "Roles" r
                WHERE r."SystemKey" = 'operator'
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM "RolePermissions" WHERE "Permission" IN ('io.operate', 'io.configure');""");

            migrationBuilder.DropForeignKey(
                name: "FK_ResourceProfiles_IoModules_IoModuleId",
                table: "ResourceProfiles");

            migrationBuilder.DropTable(
                name: "IoPoints");

            migrationBuilder.DropTable(
                name: "IoModules");

            migrationBuilder.DropIndex(
                name: "IX_ResourceProfiles_IoModuleId",
                table: "ResourceProfiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ResourceProfiles_OneResource",
                table: "ResourceProfiles");

            migrationBuilder.DropColumn(
                name: "IoModuleId",
                table: "ResourceProfiles");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ResourceProfiles_OneResource",
                table: "ResourceProfiles",
                sql: "num_nonnulls(\"ChannelId\", \"AccessDoorId\", \"AlarmAreaId\", \"AlarmZoneId\", \"CercoPanelId\", \"SpeakerId\", \"IntercomId\") = 1");
        }
    }
}
