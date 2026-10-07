using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class ResourceProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResourceProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelId = table.Column<int>(type: "integer", nullable: true),
                    AccessDoorId = table.Column<int>(type: "integer", nullable: true),
                    AlarmAreaId = table.Column<int>(type: "integer", nullable: true),
                    AlarmZoneId = table.Column<int>(type: "integer", nullable: true),
                    CercoPanelId = table.Column<int>(type: "integer", nullable: true),
                    SpeakerId = table.Column<int>(type: "integer", nullable: true),
                    IntercomId = table.Column<int>(type: "integer", nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Instructions = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceProfiles", x => x.Id);
                    table.CheckConstraint("CK_ResourceProfiles_OneResource", "num_nonnulls(\"ChannelId\", \"AccessDoorId\", \"AlarmAreaId\", \"AlarmZoneId\", \"CercoPanelId\", \"SpeakerId\", \"IntercomId\") = 1");
                    table.ForeignKey(
                        name: "FK_ResourceProfiles_AccessDoors_AccessDoorId",
                        column: x => x.AccessDoorId,
                        principalTable: "AccessDoors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResourceProfiles_AlarmAreas_AlarmAreaId",
                        column: x => x.AlarmAreaId,
                        principalTable: "AlarmAreas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResourceProfiles_AlarmZones_AlarmZoneId",
                        column: x => x.AlarmZoneId,
                        principalTable: "AlarmZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResourceProfiles_CercoPanels_CercoPanelId",
                        column: x => x.CercoPanelId,
                        principalTable: "CercoPanels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResourceProfiles_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResourceProfiles_Intercoms_IntercomId",
                        column: x => x.IntercomId,
                        principalTable: "Intercoms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResourceProfiles_Speakers_SpeakerId",
                        column: x => x.SpeakerId,
                        principalTable: "Speakers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResourceProfileCameras",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ResourceProfileId = table.Column<int>(type: "integer", nullable: false),
                    ChannelId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceProfileCameras", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResourceProfileCameras_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResourceProfileCameras_ResourceProfiles_ResourceProfileId",
                        column: x => x.ResourceProfileId,
                        principalTable: "ResourceProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceProfileCameras_ChannelId",
                table: "ResourceProfileCameras",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceProfileCameras_ResourceProfileId_ChannelId",
                table: "ResourceProfileCameras",
                columns: new[] { "ResourceProfileId", "ChannelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourceProfiles_AccessDoorId",
                table: "ResourceProfiles",
                column: "AccessDoorId",
                unique: true,
                filter: "\"AccessDoorId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceProfiles_AlarmAreaId",
                table: "ResourceProfiles",
                column: "AlarmAreaId",
                unique: true,
                filter: "\"AlarmAreaId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceProfiles_AlarmZoneId",
                table: "ResourceProfiles",
                column: "AlarmZoneId",
                unique: true,
                filter: "\"AlarmZoneId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceProfiles_CercoPanelId",
                table: "ResourceProfiles",
                column: "CercoPanelId",
                unique: true,
                filter: "\"CercoPanelId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceProfiles_ChannelId",
                table: "ResourceProfiles",
                column: "ChannelId",
                unique: true,
                filter: "\"ChannelId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceProfiles_IntercomId",
                table: "ResourceProfiles",
                column: "IntercomId",
                unique: true,
                filter: "\"IntercomId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceProfiles_SpeakerId",
                table: "ResourceProfiles",
                column: "SpeakerId",
                unique: true,
                filter: "\"SpeakerId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceProfileCameras");

            migrationBuilder.DropTable(
                name: "ResourceProfiles");
        }
    }
}
