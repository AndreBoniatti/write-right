using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WriteRight.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Exercises",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceLanguage = table.Column<string>(type: "TEXT", nullable: false),
                    TargetLanguage = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: true),
                    Theme = table.Column<string>(type: "TEXT", nullable: true),
                    SourceText = table.Column<string>(type: "TEXT", nullable: false),
                    UserTranslation = table.Column<string>(type: "TEXT", nullable: false),
                    CorrectedText = table.Column<string>(type: "TEXT", nullable: false),
                    OverallComment = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exercises", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Errors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExerciseAttemptId = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Original = table.Column<string>(type: "TEXT", nullable: false),
                    Correction = table.Column<string>(type: "TEXT", nullable: false),
                    Explanation = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Errors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Errors_Exercises_ExerciseAttemptId",
                        column: x => x.ExerciseAttemptId,
                        principalTable: "Exercises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Errors_Category",
                table: "Errors",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Errors_ExerciseAttemptId",
                table: "Errors",
                column: "ExerciseAttemptId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Errors");

            migrationBuilder.DropTable(
                name: "Exercises");
        }
    }
}
