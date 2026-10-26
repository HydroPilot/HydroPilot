using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddRecentVisitsAndSensorChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserRecentVisits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    LotId = table.Column<int>(type: "int", nullable: true),
                    NodeId = table.Column<int>(type: "int", nullable: true),
                    VisitedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRecentVisits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserRecentVisits_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRecentVisits_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserRecentVisits_LotId",
                table: "UserRecentVisits",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRecentVisits_UserId_VisitedAtUtc",
                table: "UserRecentVisits",
                columns: new[] { "UserId", "VisitedAtUtc" });

            // Días entre chequeos (calibración) por tipo de sensor; se editan en Ajustes > Parámetros.
            foreach (var (type, days) in new[] { ("pH", 14), ("CE", 30), ("Temperatura", 90), ("Humedad", 90) })
            {
                migrationBuilder.Sql(
                    $"IF NOT EXISTS (SELECT 1 FROM AppSettings WHERE [Key] = N'SensorCheckDays:{type}') " +
                    $"INSERT INTO AppSettings ([Key], [Value]) VALUES (N'SensorCheckDays:{type}', N'{days}');");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppSettings WHERE [Key] LIKE N'SensorCheckDays:%';");

            migrationBuilder.DropTable(
                name: "UserRecentVisits");
        }
    }
}
