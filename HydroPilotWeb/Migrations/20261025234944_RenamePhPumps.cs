using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class RenamePhPumps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Bombas de pH: "pH Up" / "pH Down" pasan a "Subir pH" / "Bajar pH" (solo si conservan el nombre de fábrica).
            migrationBuilder.Sql("UPDATE Actuators SET Name = 'Subir pH' WHERE Kind = 'ph-up' AND Name = 'pH Up';");
            migrationBuilder.Sql("UPDATE Actuators SET Name = 'Bajar pH' WHERE Kind = 'ph-down' AND Name = 'pH Down';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Actuators SET Name = 'pH Up' WHERE Kind = 'ph-up' AND Name = 'Subir pH';");
            migrationBuilder.Sql("UPDATE Actuators SET Name = 'pH Down' WHERE Kind = 'ph-down' AND Name = 'Bajar pH';");
        }
    }
}
