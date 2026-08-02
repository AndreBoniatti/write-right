using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WriteRight.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Analyses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PracticesAnalyzed = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorsAnalyzed = table.Column<int>(type: "INTEGER", nullable: false),
                    PatternsJson = table.Column<string>(type: "TEXT", nullable: false),
                    StudyItemsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Analyses", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Analyses");
        }
    }
}
