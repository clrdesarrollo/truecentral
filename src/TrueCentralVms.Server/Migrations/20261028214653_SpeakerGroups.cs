using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class SpeakerGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpeakerGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeakerGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpeakerGroupMembers",
                columns: table => new
                {
                    SpeakerGroupId = table.Column<int>(type: "integer", nullable: false),
                    SpeakerId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeakerGroupMembers", x => new { x.SpeakerGroupId, x.SpeakerId });
                    table.ForeignKey(
                        name: "FK_SpeakerGroupMembers_SpeakerGroups_SpeakerGroupId",
                        column: x => x.SpeakerGroupId,
                        principalTable: "SpeakerGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SpeakerGroupMembers_Speakers_SpeakerId",
                        column: x => x.SpeakerId,
                        principalTable: "Speakers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpeakerGroupMembers_SpeakerId",
                table: "SpeakerGroupMembers",
                column: "SpeakerId");

            migrationBuilder.CreateIndex(
                name: "IX_SpeakerGroups_Name",
                table: "SpeakerGroups",
                column: "Name",
                unique: true);

            // El grupo de texto que tenía cada parlante pasa a ser un grupo de
            // verdad (mismo nombre: las automatizaciones que lo nombran siguen
            // andando). "Bodega" y "bodega" son el mismo grupo.
            migrationBuilder.Sql("""
                INSERT INTO "SpeakerGroups" ("Name", "CreatedAt", "UpdatedAt")
                SELECT MIN(TRIM("GroupName")), now(), now()
                FROM "Speakers"
                WHERE "GroupName" IS NOT NULL AND TRIM("GroupName") <> ''
                GROUP BY LOWER(TRIM("GroupName"));

                INSERT INTO "SpeakerGroupMembers" ("SpeakerGroupId", "SpeakerId")
                SELECT g."Id", s."Id"
                FROM "Speakers" s
                JOIN "SpeakerGroups" g ON LOWER(g."Name") = LOWER(TRIM(s."GroupName"))
                WHERE s."GroupName" IS NOT NULL AND TRIM(s."GroupName") <> '';
                """);

            migrationBuilder.DropColumn(
                name: "GroupName",
                table: "Speakers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpeakerGroupMembers");

            migrationBuilder.DropTable(
                name: "SpeakerGroups");

            migrationBuilder.AddColumn<string>(
                name: "GroupName",
                table: "Speakers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }
    }
}
