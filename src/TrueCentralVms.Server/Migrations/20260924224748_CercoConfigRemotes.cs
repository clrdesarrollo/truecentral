using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class CercoConfigRemotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Arming",
                table: "CercoPanels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Valores por defecto = firmware, para que los paneles ya existentes queden
            // con una configuración válida (no 0 / "" que el enum no puede leer).
            migrationBuilder.AddColumn<bool>(
                name: "Chirp",
                table: "CercoPanels",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConfigSynced",
                table: "CercoPanels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ExitDelaySeconds",
                table: "CercoPanels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HvLevel",
                table: "CercoPanels",
                type: "integer",
                nullable: false,
                defaultValue: 21);

            migrationBuilder.AddColumn<string>(
                name: "KeyMode",
                table: "CercoPanels",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Off");

            migrationBuilder.AddColumn<bool>(
                name: "KeyOn",
                table: "CercoPanels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RfLearning",
                table: "CercoPanels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SirenSeconds",
                table: "CercoPanels",
                type: "integer",
                nullable: false,
                defaultValue: 180);

            migrationBuilder.AddColumn<int>(
                name: "Zone0Adc",
                table: "CercoPanels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Zone0BlocksArm",
                table: "CercoPanels",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Zone0Mode",
                table: "CercoPanels",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Off");

            migrationBuilder.CreateTable(
                name: "CercoRemotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CercoPanelId = table.Column<int>(type: "integer", nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Bits = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CercoRemotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CercoRemotes_CercoPanels_CercoPanelId",
                        column: x => x.CercoPanelId,
                        principalTable: "CercoPanels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CercoRemotes_CercoPanelId_Slot",
                table: "CercoRemotes",
                columns: new[] { "CercoPanelId", "Slot" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CercoRemotes");

            migrationBuilder.DropColumn(
                name: "Arming",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "Chirp",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "ConfigSynced",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "ExitDelaySeconds",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "HvLevel",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "KeyMode",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "KeyOn",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "RfLearning",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "SirenSeconds",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "Zone0Adc",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "Zone0BlocksArm",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "Zone0Mode",
                table: "CercoPanels");
        }
    }
}
