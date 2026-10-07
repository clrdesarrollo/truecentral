using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class Locations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "Speakers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "Intercoms",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "Channels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "CercoPanels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "AlarmZones",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "AlarmAreas",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "AccessDoors",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Locations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ParentId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Locations_Locations_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_LocationId",
                table: "Speakers",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Intercoms_LocationId",
                table: "Intercoms",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_LocationId",
                table: "Channels",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_CercoPanels_LocationId",
                table: "CercoPanels",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmZones_LocationId",
                table: "AlarmZones",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmAreas_LocationId",
                table: "AlarmAreas",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessDoors_LocationId",
                table: "AccessDoors",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_ParentId",
                table: "Locations",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccessDoors_Locations_LocationId",
                table: "AccessDoors",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AlarmAreas_Locations_LocationId",
                table: "AlarmAreas",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AlarmZones_Locations_LocationId",
                table: "AlarmZones",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CercoPanels_Locations_LocationId",
                table: "CercoPanels",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Locations_LocationId",
                table: "Channels",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Intercoms_Locations_LocationId",
                table: "Intercoms",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Speakers_Locations_LocationId",
                table: "Speakers",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Los textos libres que ya había (ubicación de los equipos de
            // acceso, sitio de los cercos) se convierten en ubicaciones raíz y
            // sus recursos quedan ubicados ahí, para que el árbol no parta vacío.
            // Un mismo texto (sin distinguir mayúsculas) da una sola ubicación.
            // Los campos de texto originales se conservan tal cual.
            migrationBuilder.Sql("""
                INSERT INTO "Locations" ("Name", "Kind", "CreatedAt", "UpdatedAt")
                SELECT MIN(t.txt), 'Sector', now(), now()
                FROM (
                    SELECT left(btrim("Location"), 128) AS txt FROM "AccessDevices"
                    WHERE "Location" IS NOT NULL AND btrim("Location") <> ''
                    UNION ALL
                    SELECT left(btrim("Site"), 128) FROM "CercoPanels"
                    WHERE "Site" IS NOT NULL AND btrim("Site") <> ''
                ) t
                GROUP BY lower(t.txt);

                UPDATE "AccessDoors" d SET "LocationId" = l."Id"
                FROM "AccessDevices" a, "Locations" l
                WHERE d."AccessDeviceId" = a."Id" AND a."Location" IS NOT NULL
                  AND l."ParentId" IS NULL AND lower(l."Name") = lower(left(btrim(a."Location"), 128));

                UPDATE "CercoPanels" p SET "LocationId" = l."Id"
                FROM "Locations" l
                WHERE p."Site" IS NOT NULL
                  AND l."ParentId" IS NULL AND lower(l."Name") = lower(left(btrim(p."Site"), 128));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccessDoors_Locations_LocationId",
                table: "AccessDoors");

            migrationBuilder.DropForeignKey(
                name: "FK_AlarmAreas_Locations_LocationId",
                table: "AlarmAreas");

            migrationBuilder.DropForeignKey(
                name: "FK_AlarmZones_Locations_LocationId",
                table: "AlarmZones");

            migrationBuilder.DropForeignKey(
                name: "FK_CercoPanels_Locations_LocationId",
                table: "CercoPanels");

            migrationBuilder.DropForeignKey(
                name: "FK_Channels_Locations_LocationId",
                table: "Channels");

            migrationBuilder.DropForeignKey(
                name: "FK_Intercoms_Locations_LocationId",
                table: "Intercoms");

            migrationBuilder.DropForeignKey(
                name: "FK_Speakers_Locations_LocationId",
                table: "Speakers");

            migrationBuilder.DropTable(
                name: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Speakers_LocationId",
                table: "Speakers");

            migrationBuilder.DropIndex(
                name: "IX_Intercoms_LocationId",
                table: "Intercoms");

            migrationBuilder.DropIndex(
                name: "IX_Channels_LocationId",
                table: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_CercoPanels_LocationId",
                table: "CercoPanels");

            migrationBuilder.DropIndex(
                name: "IX_AlarmZones_LocationId",
                table: "AlarmZones");

            migrationBuilder.DropIndex(
                name: "IX_AlarmAreas_LocationId",
                table: "AlarmAreas");

            migrationBuilder.DropIndex(
                name: "IX_AccessDoors_LocationId",
                table: "AccessDoors");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Intercoms");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "AlarmZones");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "AlarmAreas");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "AccessDoors");
        }
    }
}
