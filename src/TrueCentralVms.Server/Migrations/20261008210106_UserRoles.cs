using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class UserRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SystemKey = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "integer", nullable: false),
                    Permission = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.Permission });
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    RoleId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_SystemKey",
                table: "Roles",
                column: "SystemKey",
                unique: true,
                filter: "\"SystemKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            // Roles de sistema. El Operador parte con lo que podía hacer el
            // operador antes de los roles (lista congelada aquí: el catálogo
            // del código puede cambiar después sin reescribir esta migración).
            migrationBuilder.Sql("""
                INSERT INTO "Roles" ("Name", "Description", "SystemKey", "CreatedAt", "UpdatedAt") VALUES
                  ('Administrador', 'Todos los permisos, también los de versiones futuras. No se edita ni se borra.', 'admin', now(), now()),
                  ('Operador', 'Rol de sistema para la operación diaria. Se puede ajustar, no borrar.', 'operator', now(), now());

                INSERT INTO "RolePermissions" ("RoleId", "Permission")
                SELECT r."Id", p.key
                FROM "Roles" r,
                     unnest(ARRAY['live.view','live.ptz','live.views','playback.view','playback.export',
                                  'wall.operate','wall.layouts','events.attend','locations.command',
                                  'alarms.monitor','alarms.operate','cerco.monitor','cerco.operate',
                                  'access.monitor','access.doors','access.records.export','anpr.view',
                                  'speakers.play','intercom.answer','persons.view','workflows.view',
                                  'maintenance.view']) AS p(key)
                WHERE r."SystemKey" = 'operator';

                -- Cada usuario queda con el rol de sistema de su nivel actual.
                INSERT INTO "UserRoles" ("UserId", "RoleId")
                SELECT u."Id", r."Id"
                FROM "Users" u
                JOIN "Roles" r ON r."SystemKey" = CASE WHEN u."Role" = 'Admin' THEN 'admin' ELSE 'operator' END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "Roles");
        }
    }
}
