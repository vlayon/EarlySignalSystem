using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EarlySignalSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddSignalType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignalType",
                table: "Signals",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignalType",
                table: "Signals");
        }
    }
}
