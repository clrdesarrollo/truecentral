using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class VideoWall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Decoders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DriverKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PasswordCiphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Decoders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Walls",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DecoderId = table.Column<int>(type: "integer", nullable: false),
                    Rows = table.Column<int>(type: "integer", nullable: false),
                    Columns = table.Column<int>(type: "integer", nullable: false),
                    FullscreenWindowId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Walls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Walls_Decoders_DecoderId",
                        column: x => x.DecoderId,
                        principalTable: "Decoders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WallFloatingWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VideoWallId = table.Column<int>(type: "integer", nullable: false),
                    X = table.Column<double>(type: "double precision", nullable: false),
                    Y = table.Column<double>(type: "double precision", nullable: false),
                    W = table.Column<double>(type: "double precision", nullable: false),
                    H = table.Column<double>(type: "double precision", nullable: false),
                    DecodeChannel = table.Column<int>(type: "integer", nullable: false),
                    AssignedChannelId = table.Column<int>(type: "integer", nullable: true),
                    AssignedStreamType = table.Column<int>(type: "integer", nullable: false),
                    ExternalUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ExternalLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    HomeX = table.Column<double>(type: "double precision", nullable: true),
                    HomeY = table.Column<double>(type: "double precision", nullable: true),
                    HomeW = table.Column<double>(type: "double precision", nullable: true),
                    HomeH = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallFloatingWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallFloatingWindows_Channels_AssignedChannelId",
                        column: x => x.AssignedChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WallFloatingWindows_Walls_VideoWallId",
                        column: x => x.VideoWallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WallLayouts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VideoWallId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallLayouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallLayouts_Walls_VideoWallId",
                        column: x => x.VideoWallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WallScreens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VideoWallId = table.Column<int>(type: "integer", nullable: false),
                    Row = table.Column<int>(type: "integer", nullable: false),
                    Col = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayChannel = table.Column<int>(type: "integer", nullable: false),
                    WindowMode = table.Column<int>(type: "integer", nullable: false),
                    FullscreenWindowId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallScreens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallScreens_Walls_VideoWallId",
                        column: x => x.VideoWallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WallLayoutItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WallLayoutPresetId = table.Column<int>(type: "integer", nullable: false),
                    Row = table.Column<int>(type: "integer", nullable: false),
                    Col = table.Column<int>(type: "integer", nullable: false),
                    WindowIndex = table.Column<int>(type: "integer", nullable: false),
                    ChannelId = table.Column<int>(type: "integer", nullable: false),
                    StreamType = table.Column<int>(type: "integer", nullable: false),
                    SpanCols = table.Column<int>(type: "integer", nullable: false),
                    SpanRows = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallLayoutItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallLayoutItems_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WallLayoutItems_WallLayouts_WallLayoutPresetId",
                        column: x => x.WallLayoutPresetId,
                        principalTable: "WallLayouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WallLayoutScreens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WallLayoutPresetId = table.Column<int>(type: "integer", nullable: false),
                    Row = table.Column<int>(type: "integer", nullable: false),
                    Col = table.Column<int>(type: "integer", nullable: false),
                    WindowMode = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallLayoutScreens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallLayoutScreens_WallLayouts_WallLayoutPresetId",
                        column: x => x.WallLayoutPresetId,
                        principalTable: "WallLayouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScreenWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WallScreenId = table.Column<int>(type: "integer", nullable: false),
                    WindowIndex = table.Column<int>(type: "integer", nullable: false),
                    DecodeChannel = table.Column<int>(type: "integer", nullable: false),
                    SpanCols = table.Column<int>(type: "integer", nullable: false),
                    SpanRows = table.Column<int>(type: "integer", nullable: false),
                    AssignedChannelId = table.Column<int>(type: "integer", nullable: true),
                    AssignedStreamType = table.Column<int>(type: "integer", nullable: false),
                    ExternalUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ExternalLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreenWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScreenWindows_Channels_AssignedChannelId",
                        column: x => x.AssignedChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ScreenWindows_WallScreens_WallScreenId",
                        column: x => x.WallScreenId,
                        principalTable: "WallScreens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Decoders_Host_Port",
                table: "Decoders",
                columns: new[] { "Host", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScreenWindows_AssignedChannelId",
                table: "ScreenWindows",
                column: "AssignedChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ScreenWindows_WallScreenId_WindowIndex",
                table: "ScreenWindows",
                columns: new[] { "WallScreenId", "WindowIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WallFloatingWindows_AssignedChannelId",
                table: "WallFloatingWindows",
                column: "AssignedChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_WallFloatingWindows_VideoWallId",
                table: "WallFloatingWindows",
                column: "VideoWallId");

            migrationBuilder.CreateIndex(
                name: "IX_WallLayoutItems_ChannelId",
                table: "WallLayoutItems",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_WallLayoutItems_WallLayoutPresetId",
                table: "WallLayoutItems",
                column: "WallLayoutPresetId");

            migrationBuilder.CreateIndex(
                name: "IX_WallLayouts_VideoWallId_Name",
                table: "WallLayouts",
                columns: new[] { "VideoWallId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WallLayoutScreens_WallLayoutPresetId",
                table: "WallLayoutScreens",
                column: "WallLayoutPresetId");

            migrationBuilder.CreateIndex(
                name: "IX_Walls_DecoderId",
                table: "Walls",
                column: "DecoderId");

            migrationBuilder.CreateIndex(
                name: "IX_WallScreens_VideoWallId_Row_Col",
                table: "WallScreens",
                columns: new[] { "VideoWallId", "Row", "Col" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScreenWindows");

            migrationBuilder.DropTable(
                name: "WallFloatingWindows");

            migrationBuilder.DropTable(
                name: "WallLayoutItems");

            migrationBuilder.DropTable(
                name: "WallLayoutScreens");

            migrationBuilder.DropTable(
                name: "WallScreens");

            migrationBuilder.DropTable(
                name: "WallLayouts");

            migrationBuilder.DropTable(
                name: "Walls");

            migrationBuilder.DropTable(
                name: "Decoders");
        }
    }
}
