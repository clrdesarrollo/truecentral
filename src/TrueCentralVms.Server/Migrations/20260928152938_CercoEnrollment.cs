using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class CercoEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EnrollAttempts",
                table: "CercoPanels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "EnrollCodeCiphertext",
                table: "CercoPanels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EnrollExpiresAt",
                table: "CercoPanels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Enrolled",
                table: "CercoPanels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Paneles provisionados con la versión anterior (PSK cargada a mano): ya enrolados.
            migrationBuilder.Sql("UPDATE \"CercoPanels\" SET \"Enrolled\" = TRUE WHERE length(\"PskCiphertext\") > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnrollAttempts",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "EnrollCodeCiphertext",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "EnrollExpiresAt",
                table: "CercoPanels");

            migrationBuilder.DropColumn(
                name: "Enrolled",
                table: "CercoPanels");
        }
    }
}
