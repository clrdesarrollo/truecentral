using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class Intercom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntercomCalls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IntercomId = table.Column<int>(type: "integer", nullable: false),
                    IntercomName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnsweredByUserId = table.Column<int>(type: "integer", nullable: true),
                    AnsweredBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Origin = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EndReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DoorOpened = table.Column<bool>(type: "boolean", nullable: false),
                    DoorOpenedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntercomCalls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Intercoms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DriverKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    HttpPort = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PasswordCiphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    GroupName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ChannelId = table.Column<int>(type: "integer", nullable: true),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SerialNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FirmwareVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DoorCount = table.Column<int>(type: "integer", nullable: false),
                    CallCenterEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Intercoms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Intercoms_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntercomCalls_IntercomId_StartedAt",
                table: "IntercomCalls",
                columns: new[] { "IntercomId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercomCalls_StartedAt",
                table: "IntercomCalls",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Intercoms_ChannelId",
                table: "Intercoms",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Intercoms_Host_Port",
                table: "Intercoms",
                columns: new[] { "Host", "Port" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntercomCalls");

            migrationBuilder.DropTable(
                name: "Intercoms");
        }
    }
}
