using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class Dashboards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RestrictToRoles",
                table: "LiveViews",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<int>(
                name: "ChannelId",
                table: "LiveViewItems",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "LiveViewItems",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "camera");

            migrationBuilder.AddColumn<string>(
                name: "Ref",
                table: "LiveViewItems",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RefId",
                table: "LiveViewItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "LiveViewItems",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LiveViewRoles",
                columns: table => new
                {
                    LiveViewId = table.Column<int>(type: "integer", nullable: false),
                    RoleId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveViewRoles", x => new { x.LiveViewId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_LiveViewRoles_LiveViews_LiveViewId",
                        column: x => x.LiveViewId,
                        principalTable: "LiveViews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LiveViewRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LiveViewRoles_RoleId",
                table: "LiveViewRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_WebLinks_Name",
                table: "WebLinks",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiveViewRoles");

            migrationBuilder.DropTable(
                name: "WebLinks");

            migrationBuilder.DropColumn(
                name: "RestrictToRoles",
                table: "LiveViews");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "LiveViewItems");

            migrationBuilder.DropColumn(
                name: "Ref",
                table: "LiveViewItems");

            migrationBuilder.DropColumn(
                name: "RefId",
                table: "LiveViewItems");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "LiveViewItems");

            migrationBuilder.AlterColumn<int>(
                name: "ChannelId",
                table: "LiveViewItems",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
