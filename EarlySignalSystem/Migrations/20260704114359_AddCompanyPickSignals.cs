using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EarlySignalSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyPickSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyPickSignals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyPickId = table.Column<int>(type: "int", nullable: false),
                    SignalId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyPickSignals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyPickSignals_CompanyPicks_CompanyPickId",
                        column: x => x.CompanyPickId,
                        principalTable: "CompanyPicks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CompanyPickSignals_Signals_SignalId",
                        column: x => x.SignalId,
                        principalTable: "Signals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyPickSignals_CompanyPickId",
                table: "CompanyPickSignals",
                column: "CompanyPickId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyPickSignals_SignalId",
                table: "CompanyPickSignals",
                column: "SignalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyPickSignals");
        }
    }
}
