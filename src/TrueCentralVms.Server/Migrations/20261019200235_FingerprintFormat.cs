using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <inheritdoc />
    public partial class FingerprintFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccessFingerprints_AccessPersonId_Number",
                table: "AccessFingerprints");

            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "AccessFingerprints",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "hikvision");

            migrationBuilder.CreateIndex(
                name: "IX_AccessFingerprints_AccessPersonId_Number_Format",
                table: "AccessFingerprints",
                columns: new[] { "AccessPersonId", "Number", "Format" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccessFingerprints_AccessPersonId_Number_Format",
                table: "AccessFingerprints");

            migrationBuilder.DropColumn(
                name: "Format",
                table: "AccessFingerprints");

            migrationBuilder.CreateIndex(
                name: "IX_AccessFingerprints_AccessPersonId_Number",
                table: "AccessFingerprints",
                columns: new[] { "AccessPersonId", "Number" },
                unique: true);
        }
    }
}
