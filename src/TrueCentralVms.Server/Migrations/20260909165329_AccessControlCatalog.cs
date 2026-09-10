using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class AccessControlCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOpen",
                table: "AccessDoors",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "AccessDoors",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "StateReadAt",
                table: "AccessDoors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastEventAt",
                table: "AccessDevices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccessEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccessDeviceId = table.Column<int>(type: "integer", nullable: false),
                    DeviceName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DoorNumber = table.Column<int>(type: "integer", nullable: true),
                    DoorName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Credential = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    EmployeeNo = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PersonName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AccessPersonId = table.Column<int>(type: "integer", nullable: true),
                    CardNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MajorType = table.Column<int>(type: "integer", nullable: true),
                    MinorType = table.Column<int>(type: "integer", nullable: true),
                    RawJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessEvents_AccessDevices_AccessDeviceId",
                        column: x => x.AccessDeviceId,
                        principalTable: "AccessDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessPersons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EmployeeNo = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Department = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Position = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    PinCiphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    SyncState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SyncError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessPersons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessPlanSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SegmentsJson = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessPlanSlots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PlanNumber = table.Column<int>(type: "integer", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessCards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccessPersonId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessCards_AccessPersons_AccessPersonId",
                        column: x => x.AccessPersonId,
                        principalTable: "AccessPersons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessPersonDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccessPersonId = table.Column<int>(type: "integer", nullable: false),
                    AccessDeviceId = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PendingRemoval = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessPersonDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessPersonDevices_AccessDevices_AccessDeviceId",
                        column: x => x.AccessDeviceId,
                        principalTable: "AccessDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessPersonDevices_AccessPersons_AccessPersonId",
                        column: x => x.AccessPersonId,
                        principalTable: "AccessPersons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessLevels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    AccessScheduleId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessLevels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessLevels_AccessSchedules_AccessScheduleId",
                        column: x => x.AccessScheduleId,
                        principalTable: "AccessSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessScheduleSegments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccessScheduleId = table.Column<int>(type: "integer", nullable: false),
                    Day = table.Column<int>(type: "integer", nullable: false),
                    StartMinutes = table.Column<int>(type: "integer", nullable: false),
                    EndMinutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessScheduleSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessScheduleSegments_AccessSchedules_AccessScheduleId",
                        column: x => x.AccessScheduleId,
                        principalTable: "AccessSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessLevelDoors",
                columns: table => new
                {
                    AccessLevelId = table.Column<int>(type: "integer", nullable: false),
                    AccessDoorId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessLevelDoors", x => new { x.AccessLevelId, x.AccessDoorId });
                    table.ForeignKey(
                        name: "FK_AccessLevelDoors_AccessDoors_AccessDoorId",
                        column: x => x.AccessDoorId,
                        principalTable: "AccessDoors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessLevelDoors_AccessLevels_AccessLevelId",
                        column: x => x.AccessLevelId,
                        principalTable: "AccessLevels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessLevelPersons",
                columns: table => new
                {
                    AccessLevelId = table.Column<int>(type: "integer", nullable: false),
                    AccessPersonId = table.Column<int>(type: "integer", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessLevelPersons", x => new { x.AccessLevelId, x.AccessPersonId });
                    table.ForeignKey(
                        name: "FK_AccessLevelPersons_AccessLevels_AccessLevelId",
                        column: x => x.AccessLevelId,
                        principalTable: "AccessLevels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessLevelPersons_AccessPersons_AccessPersonId",
                        column: x => x.AccessPersonId,
                        principalTable: "AccessPersons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessCards_AccessPersonId",
                table: "AccessCards",
                column: "AccessPersonId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessCards_Number",
                table: "AccessCards",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessEvents_AccessDeviceId_Timestamp",
                table: "AccessEvents",
                columns: new[] { "AccessDeviceId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessEvents_AccessPersonId",
                table: "AccessEvents",
                column: "AccessPersonId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessEvents_Timestamp",
                table: "AccessEvents",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AccessLevelDoors_AccessDoorId",
                table: "AccessLevelDoors",
                column: "AccessDoorId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessLevelPersons_AccessPersonId",
                table: "AccessLevelPersons",
                column: "AccessPersonId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessLevels_AccessScheduleId",
                table: "AccessLevels",
                column: "AccessScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessLevels_Name",
                table: "AccessLevels",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessPersonDevices_AccessDeviceId",
                table: "AccessPersonDevices",
                column: "AccessDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessPersonDevices_AccessPersonId_AccessDeviceId",
                table: "AccessPersonDevices",
                columns: new[] { "AccessPersonId", "AccessDeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessPersons_EmployeeNo",
                table: "AccessPersons",
                column: "EmployeeNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessPersons_LastName_FirstName",
                table: "AccessPersons",
                columns: new[] { "LastName", "FirstName" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessPlanSlots_Hash",
                table: "AccessPlanSlots",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessPlanSlots_Number",
                table: "AccessPlanSlots",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessSchedules_Name",
                table: "AccessSchedules",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessScheduleSegments_AccessScheduleId_Day",
                table: "AccessScheduleSegments",
                columns: new[] { "AccessScheduleId", "Day" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessCards");

            migrationBuilder.DropTable(
                name: "AccessEvents");

            migrationBuilder.DropTable(
                name: "AccessLevelDoors");

            migrationBuilder.DropTable(
                name: "AccessLevelPersons");

            migrationBuilder.DropTable(
                name: "AccessPersonDevices");

            migrationBuilder.DropTable(
                name: "AccessPlanSlots");

            migrationBuilder.DropTable(
                name: "AccessScheduleSegments");

            migrationBuilder.DropTable(
                name: "AccessLevels");

            migrationBuilder.DropTable(
                name: "AccessPersons");

            migrationBuilder.DropTable(
                name: "AccessSchedules");

            migrationBuilder.DropColumn(
                name: "IsOpen",
                table: "AccessDoors");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "AccessDoors");

            migrationBuilder.DropColumn(
                name: "StateReadAt",
                table: "AccessDoors");

            migrationBuilder.DropColumn(
                name: "LastEventAt",
                table: "AccessDevices");
        }
    }
}
