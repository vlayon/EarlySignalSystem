using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EarlySignalSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddRunLogJobName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JobName",
                table: "RunLogs",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JobName",
                table: "RunLogs");
        }
    }
}
