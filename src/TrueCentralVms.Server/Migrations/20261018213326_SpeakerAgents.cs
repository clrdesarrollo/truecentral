using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class SpeakerAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Speakers_Host_Port",
                table: "Speakers");

            migrationBuilder.AddColumn<string>(
                name: "AgentId",
                table: "Speakers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentTokenHash",
                table: "Speakers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnrollmentCodeHash",
                table: "Speakers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EnrollmentExpiresAt",
                table: "Speakers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_AgentId",
                table: "Speakers",
                column: "AgentId",
                unique: true,
                filter: "\"AgentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_AgentTokenHash",
                table: "Speakers",
                column: "AgentTokenHash",
                filter: "\"AgentTokenHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_EnrollmentCodeHash",
                table: "Speakers",
                column: "EnrollmentCodeHash",
                filter: "\"EnrollmentCodeHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_Host_Port",
                table: "Speakers",
                columns: new[] { "Host", "Port" },
                unique: true,
                filter: "\"IsupDeviceId\" IS NULL AND \"DriverKey\" <> 'truecentral-agent'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Speakers_AgentId",
                table: "Speakers");

            migrationBuilder.DropIndex(
                name: "IX_Speakers_AgentTokenHash",
                table: "Speakers");

            migrationBuilder.DropIndex(
                name: "IX_Speakers_EnrollmentCodeHash",
                table: "Speakers");

            migrationBuilder.DropIndex(
                name: "IX_Speakers_Host_Port",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "AgentTokenHash",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "EnrollmentCodeHash",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "EnrollmentExpiresAt",
                table: "Speakers");

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_Host_Port",
                table: "Speakers",
                columns: new[] { "Host", "Port" },
                unique: true,
                filter: "\"IsupDeviceId\" IS NULL");
        }
    }
}
