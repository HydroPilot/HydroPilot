using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class RenameCommercialStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Estados comerciales: "Baby Leaf apta" → "Baby Leaf" y "Cosecha convencional" → "Adulta"
            // (catálogo, resultado guardado de cada evaluación y textos del historial de las plantas).
            migrationBuilder.Sql("UPDATE CommercialStages SET Name = N'Baby Leaf' WHERE Name = N'Baby Leaf apta';");
            migrationBuilder.Sql("UPDATE CommercialStages SET Name = N'Adulta', Description = N'Ventana adulta con madurez confirmada.' WHERE Name = N'Cosecha convencional';");
            migrationBuilder.Sql("UPDATE CommercialStages SET Description = REPLACE(Description, N'o apta sin confirmar', N'o Baby Leaf sin confirmar') WHERE Description LIKE N'%o apta sin confirmar%';");
            migrationBuilder.Sql("UPDATE BabyLeafConfigs SET Description = REPLACE(Description, N'apta 80', N'Baby Leaf 80') WHERE Description LIKE N'%apta 80%';");

            migrationBuilder.Sql("UPDATE BabyLeafEvaluations SET Result = N'Baby Leaf' WHERE Result = N'Baby Leaf apta';");
            migrationBuilder.Sql("UPDATE BabyLeafEvaluations SET Result = N'Adulta' WHERE Result = N'Cosecha convencional';");

            migrationBuilder.Sql("UPDATE PlantStageHistories SET Reason = N'Cosecha masiva de Baby Leafs' WHERE Reason = N'Cosecha masiva de aptas Baby Leaf';");
            migrationBuilder.Sql("UPDATE PlantStageHistories SET Reason = N'Cosecha masiva de adultas' WHERE Reason = N'Cosecha masiva convencional';");
            migrationBuilder.Sql("UPDATE PlantStageHistories SET Reason = REPLACE(Reason, N'Ventana convencional', N'Ventana adulta') WHERE Reason LIKE N'Ventana convencional%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE PlantStageHistories SET Reason = REPLACE(Reason, N'Ventana adulta', N'Ventana convencional') WHERE Reason LIKE N'Ventana adulta%';");
            migrationBuilder.Sql("UPDATE PlantStageHistories SET Reason = N'Cosecha masiva convencional' WHERE Reason = N'Cosecha masiva de adultas';");
            migrationBuilder.Sql("UPDATE PlantStageHistories SET Reason = N'Cosecha masiva de aptas Baby Leaf' WHERE Reason = N'Cosecha masiva de Baby Leafs';");

            migrationBuilder.Sql("UPDATE BabyLeafEvaluations SET Result = N'Cosecha convencional' WHERE Result = N'Adulta';");
            migrationBuilder.Sql("UPDATE BabyLeafEvaluations SET Result = N'Baby Leaf apta' WHERE Result = N'Baby Leaf';");

            migrationBuilder.Sql("UPDATE BabyLeafConfigs SET Description = REPLACE(Description, N'Baby Leaf 80', N'apta 80') WHERE Description LIKE N'%Baby Leaf 80%';");
            migrationBuilder.Sql("UPDATE CommercialStages SET Description = REPLACE(Description, N'o Baby Leaf sin confirmar', N'o apta sin confirmar') WHERE Description LIKE N'%o Baby Leaf sin confirmar%';");
            migrationBuilder.Sql("UPDATE CommercialStages SET Name = N'Cosecha convencional', Description = N'Ventana convencional con madurez confirmada.' WHERE Name = N'Adulta';");
            migrationBuilder.Sql("UPDATE CommercialStages SET Name = N'Baby Leaf apta' WHERE Name = N'Baby Leaf';");
        }
    }
}
