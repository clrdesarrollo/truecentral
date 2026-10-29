using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class PersonalWebLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebLinks_Name",
                table: "WebLinks");

            migrationBuilder.AddColumn<int>(
                name: "OwnerUserId",
                table: "WebLinks",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LayoutName",
                table: "LiveViews",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.CreateIndex(
                name: "IX_WebLinks_Name",
                table: "WebLinks",
                column: "Name",
                unique: true,
                filter: "\"OwnerUserId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WebLinks_OwnerUserId_Name",
                table: "WebLinks",
                columns: new[] { "OwnerUserId", "Name" },
                unique: true,
                filter: "\"OwnerUserId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_WebLinks_Users_OwnerUserId",
                table: "WebLinks",
                column: "OwnerUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WebLinks_Users_OwnerUserId",
                table: "WebLinks");

            migrationBuilder.DropIndex(
                name: "IX_WebLinks_Name",
                table: "WebLinks");

            migrationBuilder.DropIndex(
                name: "IX_WebLinks_OwnerUserId_Name",
                table: "WebLinks");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "WebLinks");

            migrationBuilder.AlterColumn<string>(
                name: "LayoutName",
                table: "LiveViews",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.CreateIndex(
                name: "IX_WebLinks_Name",
                table: "WebLinks",
                column: "Name",
                unique: true);
        }
    }
}
