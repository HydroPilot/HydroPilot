using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddLotRecipe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "EcOffset",
                table: "LotSetpointHistories",
                type: "decimal(4,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PhOffset",
                table: "LotSetpointHistories",
                type: "decimal(4,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LotRecipes",
                columns: table => new
                {
                    LotId = table.Column<int>(type: "int", nullable: false),
                    HistoryPhOffset = table.Column<decimal>(type: "decimal(4,2)", nullable: false),
                    HistoryEcOffset = table.Column<decimal>(type: "decimal(4,2)", nullable: false),
                    PhOffset = table.Column<decimal>(type: "decimal(4,2)", nullable: false),
                    EcOffset = table.Column<decimal>(type: "decimal(4,2)", nullable: false),
                    Trust = table.Column<decimal>(type: "decimal(3,2)", nullable: false),
                    SimilarLots = table.Column<int>(type: "int", nullable: false),
                    Evidence = table.Column<decimal>(type: "decimal(6,2)", nullable: false),
                    ScoreTrend = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastChangeAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LotRecipes", x => x.LotId);
                    table.ForeignKey(
                        name: "FK_LotRecipes_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LotRecipes");

            migrationBuilder.DropColumn(
                name: "EcOffset",
                table: "LotSetpointHistories");

            migrationBuilder.DropColumn(
                name: "PhOffset",
                table: "LotSetpointHistories");
        }
    }
}
