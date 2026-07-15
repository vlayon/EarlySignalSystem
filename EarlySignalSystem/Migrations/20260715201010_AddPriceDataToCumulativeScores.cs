using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EarlySignalSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceDataToCumulativeScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSignalDate",
                table: "CumulativeScores",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LatestPrice",
                table: "CumulativeScores",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LatestPriceDate",
                table: "CumulativeScores",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PriceChangePercent",
                table: "CumulativeScores",
                type: "decimal(9,2)",
                precision: 9,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PriceOnFirstSignalDate",
                table: "CumulativeScores",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstSignalDate",
                table: "CumulativeScores");

            migrationBuilder.DropColumn(
                name: "LatestPrice",
                table: "CumulativeScores");

            migrationBuilder.DropColumn(
                name: "LatestPriceDate",
                table: "CumulativeScores");

            migrationBuilder.DropColumn(
                name: "PriceChangePercent",
                table: "CumulativeScores");

            migrationBuilder.DropColumn(
                name: "PriceOnFirstSignalDate",
                table: "CumulativeScores");
        }
    }
}
