using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class EquipmentLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "Devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "AlarmPanels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "AccessDevices",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_LocationId",
                table: "Devices",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmPanels_LocationId",
                table: "AlarmPanels",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessDevices_LocationId",
                table: "AccessDevices",
                column: "LocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccessDevices_Locations_LocationId",
                table: "AccessDevices",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AlarmPanels_Locations_LocationId",
                table: "AlarmPanels",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_Locations_LocationId",
                table: "Devices",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Ubicación de cada equipo, antes de borrar los textos libres:
            //  - Equipo de acceso: donde están sus puertas si están todas en una
            //    misma ubicación; si no, la raíz con el nombre de su texto
            //    "Ubicación" (como en la conversión de la fase 1, que la crea si
            //    el equipo se dio de alta después). Sus puertas por ubicar quedan
            //    con él.
            //  - Cerco todavía por ubicar: la raíz con el nombre de su "Sitio".
            //  - Grabador y panel de alarma: donde están sus canales, o sus áreas
            //    y zonas, si todos los ubicados comparten una misma ubicación. Sus
            //    recursos por ubicar no se tocan.
            migrationBuilder.Sql("""
                INSERT INTO "Locations" ("Name", "Kind", "CreatedAt", "UpdatedAt")
                SELECT MIN(t.txt), 'Sector', now(), now()
                FROM (
                    SELECT left(btrim(a."Location"), 128) AS txt FROM "AccessDevices" a
                    WHERE a."Location" IS NOT NULL AND btrim(a."Location") <> ''
                      AND (SELECT count(DISTINCT d."LocationId") FROM "AccessDoors" d WHERE d."AccessDeviceId" = a."Id") <> 1
                    UNION ALL
                    SELECT left(btrim(p."Site"), 128) FROM "CercoPanels" p
                    WHERE p."Site" IS NOT NULL AND btrim(p."Site") <> '' AND p."LocationId" IS NULL
                ) t
                WHERE NOT EXISTS (SELECT 1 FROM "Locations" l
                                  WHERE l."ParentId" IS NULL AND lower(l."Name") = lower(t.txt))
                GROUP BY lower(t.txt);

                UPDATE "AccessDevices" a SET "LocationId" = s.loc
                FROM (SELECT "AccessDeviceId" AS id, MIN("LocationId") AS loc FROM "AccessDoors"
                      WHERE "LocationId" IS NOT NULL
                      GROUP BY "AccessDeviceId" HAVING count(DISTINCT "LocationId") = 1) s
                WHERE a."Id" = s.id;

                UPDATE "AccessDevices" a SET "LocationId" = l."Id"
                FROM "Locations" l
                WHERE a."LocationId" IS NULL AND a."Location" IS NOT NULL AND btrim(a."Location") <> ''
                  AND l."ParentId" IS NULL AND lower(l."Name") = lower(left(btrim(a."Location"), 128));

                UPDATE "AccessDoors" d SET "LocationId" = a."LocationId"
                FROM "AccessDevices" a
                WHERE d."AccessDeviceId" = a."Id" AND d."LocationId" IS NULL AND a."LocationId" IS NOT NULL;

                UPDATE "CercoPanels" p SET "LocationId" = l."Id"
                FROM "Locations" l
                WHERE p."LocationId" IS NULL AND p."Site" IS NOT NULL AND btrim(p."Site") <> ''
                  AND l."ParentId" IS NULL AND lower(l."Name") = lower(left(btrim(p."Site"), 128));

                UPDATE "Devices" v SET "LocationId" = s.loc
                FROM (SELECT "DeviceId" AS id, MIN("LocationId") AS loc FROM "Channels"
                      WHERE "LocationId" IS NOT NULL
                      GROUP BY "DeviceId" HAVING count(DISTINCT "LocationId") = 1) s
                WHERE v."Id" = s.id;

                UPDATE "AlarmPanels" p SET "LocationId" = s.loc
                FROM (SELECT x.pid AS id, MIN(x.loc) AS loc FROM (
                          SELECT "AlarmPanelId" AS pid, "LocationId" AS loc FROM "AlarmAreas" WHERE "LocationId" IS NOT NULL
                          UNION ALL
                          SELECT "AlarmPanelId", "LocationId" FROM "AlarmZones" WHERE "LocationId" IS NOT NULL) x
                      GROUP BY x.pid HAVING count(DISTINCT x.loc) = 1) s
                WHERE p."Id" = s.id;
                """);

            migrationBuilder.DropColumn(
                name: "Site",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "AccessDevices");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Site",
                table: "CercoPanels",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "AccessDevices",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            // De vuelta a texto libre: el nombre de la ubicación del equipo.
            migrationBuilder.Sql("""
                UPDATE "AccessDevices" a SET "Location" = l."Name"
                FROM "Locations" l WHERE a."LocationId" = l."Id";

                UPDATE "CercoPanels" p SET "Site" = l."Name"
                FROM "Locations" l WHERE p."LocationId" = l."Id";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_AccessDevices_Locations_LocationId",
                table: "AccessDevices");

            migrationBuilder.DropForeignKey(
                name: "FK_AlarmPanels_Locations_LocationId",
                table: "AlarmPanels");

            migrationBuilder.DropForeignKey(
                name: "FK_Devices_Locations_LocationId",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Devices_LocationId",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_AlarmPanels_LocationId",
                table: "AlarmPanels");

            migrationBuilder.DropIndex(
                name: "IX_AccessDevices_LocationId",
                table: "AccessDevices");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "AlarmPanels");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "AccessDevices");
        }
    }
}
