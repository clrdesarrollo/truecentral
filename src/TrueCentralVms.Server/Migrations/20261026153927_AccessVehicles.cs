using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AccessVehicles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessVehicles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccessPersonId = table.Column<int>(type: "integer", nullable: false),
                    Plate = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Brand = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Color = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Notes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessVehicles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessVehicles_AccessPersons_AccessPersonId",
                        column: x => x.AccessPersonId,
                        principalTable: "AccessPersons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleAccessRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AccessPoint = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Result = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Plate = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Confidence = table.Column<int>(type: "integer", nullable: true),
                    PlateStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DeviceId = table.Column<int>(type: "integer", nullable: true),
                    ChannelId = table.Column<int>(type: "integer", nullable: true),
                    CameraName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AccessVehicleId = table.Column<int>(type: "integer", nullable: true),
                    VehicleLabel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AccessPersonId = table.Column<int>(type: "integer", nullable: true),
                    PersonName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmployeeNo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Department = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    WorkflowId = table.Column<int>(type: "integer", nullable: true),
                    WorkflowName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PlateEventId = table.Column<long>(type: "bigint", nullable: true),
                    PlateImagePath = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleAccessRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessVehicles_AccessPersonId",
                table: "AccessVehicles",
                column: "AccessPersonId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessVehicles_Plate",
                table: "AccessVehicles",
                column: "Plate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleAccessRecords_AccessPersonId_At",
                table: "VehicleAccessRecords",
                columns: new[] { "AccessPersonId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleAccessRecords_At",
                table: "VehicleAccessRecords",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleAccessRecords_Plate_At",
                table: "VehicleAccessRecords",
                columns: new[] { "Plate", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessVehicles");

            migrationBuilder.DropTable(
                name: "VehicleAccessRecords");
        }
    }
}
