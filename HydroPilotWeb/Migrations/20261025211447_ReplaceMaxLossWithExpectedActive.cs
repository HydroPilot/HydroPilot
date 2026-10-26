using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceMaxLossWithExpectedActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // La meta pasa de "pérdidas máximas" a "plantas activas al final": 10 % de pérdidas = 90 % activas.
            migrationBuilder.RenameColumn(
                name: "MaxLossPercent",
                table: "Lots",
                newName: "ExpectedActivePercent");

            migrationBuilder.Sql(
                "UPDATE Lots SET ExpectedActivePercent = 100 - ExpectedActivePercent WHERE ExpectedActivePercent IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE Lots SET ExpectedActivePercent = 100 - ExpectedActivePercent WHERE ExpectedActivePercent IS NOT NULL;");

            migrationBuilder.RenameColumn(
                name: "ExpectedActivePercent",
                table: "Lots",
                newName: "MaxLossPercent");
        }
    }
}
