using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AccessControlDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DriverKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    UseHttps = table.Column<bool>(type: "boolean", nullable: false),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PasswordCiphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    Location = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SerialNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FirmwareVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MacAddress = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SupportsRemoteControl = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsEvents = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsCards = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsFingerprint = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsFace = table.Column<bool>(type: "boolean", nullable: false),
                    UserCapacity = table.Column<int>(type: "integer", nullable: true),
                    CardCapacity = table.Column<int>(type: "integer", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessDoors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccessDeviceId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessDoors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessDoors_AccessDevices_AccessDeviceId",
                        column: x => x.AccessDeviceId,
                        principalTable: "AccessDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessDevices_Host_Port",
                table: "AccessDevices",
                columns: new[] { "Host", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessDoors_AccessDeviceId_Number",
                table: "AccessDoors",
                columns: new[] { "AccessDeviceId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessDoors");

            migrationBuilder.DropTable(
                name: "AccessDevices");
        }
    }
}
