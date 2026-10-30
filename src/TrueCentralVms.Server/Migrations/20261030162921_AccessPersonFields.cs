using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AccessPersonFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessPersonFields",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Options = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    ShowInList = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessPersonFields", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessPersonFieldValues",
                columns: table => new
                {
                    AccessPersonId = table.Column<int>(type: "integer", nullable: false),
                    AccessPersonFieldId = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessPersonFieldValues", x => new { x.AccessPersonId, x.AccessPersonFieldId });
                    table.ForeignKey(
                        name: "FK_AccessPersonFieldValues_AccessPersonFields_AccessPersonFiel~",
                        column: x => x.AccessPersonFieldId,
                        principalTable: "AccessPersonFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessPersonFieldValues_AccessPersons_AccessPersonId",
                        column: x => x.AccessPersonId,
                        principalTable: "AccessPersons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessPersonFields_Name",
                table: "AccessPersonFields",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessPersonFieldValues_AccessPersonFieldId_Value",
                table: "AccessPersonFieldValues",
                columns: new[] { "AccessPersonFieldId", "Value" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessPersonFieldValues");

            migrationBuilder.DropTable(
                name: "AccessPersonFields");
        }
    }
}
