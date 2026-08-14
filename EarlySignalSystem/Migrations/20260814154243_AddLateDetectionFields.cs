using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EarlySignalSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddLateDetectionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LateDetectionFlag",
                table: "CumulativeScores",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LateDetectionReason",
                table: "CumulativeScores",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LateDetectionFlag",
                table: "CumulativeScores");

            migrationBuilder.DropColumn(
                name: "LateDetectionReason",
                table: "CumulativeScores");
        }
    }
}
