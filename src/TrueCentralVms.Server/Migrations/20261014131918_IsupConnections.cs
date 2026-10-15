using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class IsupConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Speakers_Host_Port",
                table: "Speakers");

            migrationBuilder.DropIndex(
                name: "IX_Intercoms_Host_Port",
                table: "Intercoms");

            migrationBuilder.DropIndex(
                name: "IX_Devices_Host_SdkPort",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_AlarmPanels_Host_Port",
                table: "AlarmPanels");

            migrationBuilder.DropIndex(
                name: "IX_AlarmPanels_Host_Port_GatewayDeviceId",
                table: "AlarmPanels");

            migrationBuilder.DropIndex(
                name: "IX_AccessDevices_Host_Port",
                table: "AccessDevices");

            migrationBuilder.RenameColumn(
                name: "GatewayKeyCiphertext",
                table: "AlarmPanels",
                newName: "IsupKeyCiphertext");

            migrationBuilder.AddColumn<string>(
                name: "IsupDeviceId",
                table: "Speakers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "IsupKeyCiphertext",
                table: "Speakers",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsupDeviceId",
                table: "Intercoms",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "IsupKeyCiphertext",
                table: "Intercoms",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsupDeviceId",
                table: "Devices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "IsupKeyCiphertext",
                table: "Devices",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsupDeviceId",
                table: "AlarmPanels",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsupDeviceId",
                table: "AccessDevices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "IsupKeyCiphertext",
                table: "AccessDevices",
                type: "bytea",
                nullable: true);

            // Paneles que reportaban a la receptora de Hikvision (driver hikvision-iprp, ya retirado): pasan a conectarse por ISUP
            // directo al servidor con el mismo ID y la misma clave. El servicio ISUP escucha también el 7661, donde se registraban en
            // la receptora, así que los que apuntaban a este servidor vuelven solos. Los que guardaban el uuid de la receptora en vez
            // del ID ISUP, o repiten un ID, quedan sin ID y deshabilitados hasta que alguien lo escriba.
            migrationBuilder.Sql("""
                UPDATE "AlarmPanels" SET
                    "IsupDeviceId" = CASE WHEN length("GatewayDeviceId") <= 32
                                           AND "GatewayDeviceId" !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-'
                                          THEN "GatewayDeviceId" END,
                    "DriverKey" = 'hikvision-isup',
                    "UseHttps" = false,
                    "Username" = '',
                    "LastError" = CASE WHEN "GatewayProtocol" = 'otap'
                        THEN 'El panel reportaba por OTAP a la receptora de Hikvision, que ya no se usa: configúrelo con ISUP 5.0 hacia este servidor.'
                        ELSE 'El panel ahora se conecta por ISUP directo a este servidor (la receptora de Hikvision ya no se usa).' END
                WHERE "DriverKey" = 'hikvision-iprp';
                UPDATE "AlarmPanels" p SET "IsupDeviceId" = NULL
                WHERE p."IsupDeviceId" IS NOT NULL
                  AND EXISTS (SELECT 1 FROM "AlarmPanels" q WHERE q."IsupDeviceId" = p."IsupDeviceId" AND q."Id" < p."Id");
                UPDATE "AlarmPanels" SET "Host" = '', "Port" = 0
                WHERE "DriverKey" = 'hikvision-isup' AND "IsupDeviceId" IS NOT NULL;
                UPDATE "AlarmPanels" SET "Host" = 'sin-id-isup-' || "Id", "Port" = 0, "Enabled" = false,
                    "LastError" = 'Falta el ID ISUP del panel (la receptora de Hikvision ya no se usa): edítelo y escriba su ID y clave ISUP.'
                WHERE "DriverKey" = 'hikvision-isup' AND "IsupDeviceId" IS NULL;
                """);

            migrationBuilder.DropColumn(
                name: "GatewayDeviceId",
                table: "AlarmPanels");

            migrationBuilder.DropColumn(
                name: "GatewayProtocol",
                table: "AlarmPanels");

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_Host_Port",
                table: "Speakers",
                columns: new[] { "Host", "Port" },
                unique: true,
                filter: "\"IsupDeviceId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_IsupDeviceId",
                table: "Speakers",
                column: "IsupDeviceId",
                unique: true,
                filter: "\"IsupDeviceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Intercoms_Host_Port",
                table: "Intercoms",
                columns: new[] { "Host", "Port" },
                unique: true,
                filter: "\"IsupDeviceId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Intercoms_IsupDeviceId",
                table: "Intercoms",
                column: "IsupDeviceId",
                unique: true,
                filter: "\"IsupDeviceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Host_SdkPort",
                table: "Devices",
                columns: new[] { "Host", "SdkPort" },
                unique: true,
                filter: "\"IsupDeviceId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_IsupDeviceId",
                table: "Devices",
                column: "IsupDeviceId",
                unique: true,
                filter: "\"IsupDeviceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmPanels_Host_Port",
                table: "AlarmPanels",
                columns: new[] { "Host", "Port" },
                unique: true,
                filter: "\"IsupDeviceId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmPanels_IsupDeviceId",
                table: "AlarmPanels",
                column: "IsupDeviceId",
                unique: true,
                filter: "\"IsupDeviceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccessDevices_Host_Port",
                table: "AccessDevices",
                columns: new[] { "Host", "Port" },
                unique: true,
                filter: "\"IsupDeviceId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccessDevices_IsupDeviceId",
                table: "AccessDevices",
                column: "IsupDeviceId",
                unique: true,
                filter: "\"IsupDeviceId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Speakers_Host_Port",
                table: "Speakers");

            migrationBuilder.DropIndex(
                name: "IX_Speakers_IsupDeviceId",
                table: "Speakers");

            migrationBuilder.DropIndex(
                name: "IX_Intercoms_Host_Port",
                table: "Intercoms");

            migrationBuilder.DropIndex(
                name: "IX_Intercoms_IsupDeviceId",
                table: "Intercoms");

            migrationBuilder.DropIndex(
                name: "IX_Devices_Host_SdkPort",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Devices_IsupDeviceId",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_AlarmPanels_Host_Port",
                table: "AlarmPanels");

            migrationBuilder.DropIndex(
                name: "IX_AlarmPanels_IsupDeviceId",
                table: "AlarmPanels");

            migrationBuilder.DropIndex(
                name: "IX_AccessDevices_Host_Port",
                table: "AccessDevices");

            migrationBuilder.DropIndex(
                name: "IX_AccessDevices_IsupDeviceId",
                table: "AccessDevices");

            migrationBuilder.DropColumn(
                name: "IsupDeviceId",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "IsupKeyCiphertext",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "IsupDeviceId",
                table: "Intercoms");

            migrationBuilder.DropColumn(
                name: "IsupKeyCiphertext",
                table: "Intercoms");

            migrationBuilder.DropColumn(
                name: "IsupDeviceId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "IsupKeyCiphertext",
                table: "Devices");


            migrationBuilder.DropColumn(
                name: "IsupDeviceId",
                table: "AccessDevices");

            migrationBuilder.DropColumn(
                name: "IsupKeyCiphertext",
                table: "AccessDevices");

            migrationBuilder.RenameColumn(
                name: "IsupKeyCiphertext",
                table: "AlarmPanels",
                newName: "GatewayKeyCiphertext");

            migrationBuilder.AddColumn<string>(
                name: "GatewayDeviceId",
                table: "AlarmPanels",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GatewayProtocol",
                table: "AlarmPanels",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "AlarmPanels" SET "GatewayDeviceId" = "IsupDeviceId", "GatewayProtocol" = 'isup'
                WHERE "IsupDeviceId" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "IsupDeviceId",
                table: "AlarmPanels");

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_Host_Port",
                table: "Speakers",
                columns: new[] { "Host", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Intercoms_Host_Port",
                table: "Intercoms",
                columns: new[] { "Host", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Host_SdkPort",
                table: "Devices",
                columns: new[] { "Host", "SdkPort" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_AccessDevices_Host_Port",
                table: "AccessDevices",
                columns: new[] { "Host", "Port" },
                unique: true);
        }
    }
}
