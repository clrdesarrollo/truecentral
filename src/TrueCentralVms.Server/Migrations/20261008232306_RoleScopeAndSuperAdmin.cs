using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class RoleScopeAndSuperAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSuperAdmin",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RestrictScope",
                table: "Roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ViewOutsideScope",
                table: "Roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "RoleLocations",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "integer", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleLocations", x => new { x.RoleId, x.LocationId });
                    table.ForeignKey(
                        name: "FK_RoleLocations_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoleLocations_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoleResources",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ResourceId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleResources", x => new { x.RoleId, x.Kind, x.ResourceId });
                    table.ForeignKey(
                        name: "FK_RoleResources_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleLocations_LocationId",
                table: "RoleLocations",
                column: "LocationId");

            // Superadministrador = el administrador creado al activar la
            // plataforma. En una instalación existente es el administrador más
            // antiguo que siga teniendo el rol Administrador (el primero que creó
            // /api/setup/admin, salvo que se le haya quitado el rol).
            migrationBuilder.Sql("""
                UPDATE "Users" SET "IsSuperAdmin" = TRUE
                WHERE "Id" = (
                    SELECT MIN(u."Id") FROM "Users" u
                    JOIN "UserRoles" ur ON ur."UserId" = u."Id"
                    JOIN "Roles" r ON r."Id" = ur."RoleId"
                    WHERE r."SystemKey" = 'admin');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoleLocations");

            migrationBuilder.DropTable(
                name: "RoleResources");

            migrationBuilder.DropColumn(
                name: "IsSuperAdmin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RestrictScope",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "ViewOutsideScope",
                table: "Roles");
        }
    }
}
