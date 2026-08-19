using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WriteRight.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddVocabCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourcePhrase",
                table: "Errors",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Cards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceLanguage = table.Column<string>(type: "TEXT", nullable: false),
                    TargetLanguage = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    Answer = table.Column<string>(type: "TEXT", nullable: false),
                    Hint = table.Column<string>(type: "TEXT", nullable: false),
                    YourAttempt = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IntervalDays = table.Column<double>(type: "REAL", nullable: false),
                    Ease = table.Column<double>(type: "REAL", nullable: false),
                    Reps = table.Column<int>(type: "INTEGER", nullable: false),
                    Lapses = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastReviewedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cards", x => x.Id);
                    table.CheckConstraint("CK_Card_HintNotEmpty", "trim(Hint) <> ''");
                });

            migrationBuilder.CreateTable(
                name: "CardReviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VocabCardId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TypedAnswer = table.Column<string>(type: "TEXT", nullable: false),
                    WasCorrect = table.Column<bool>(type: "INTEGER", nullable: false),
                    Rating = table.Column<string>(type: "TEXT", nullable: false),
                    IntervalBefore = table.Column<double>(type: "REAL", nullable: false),
                    IntervalAfter = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardReviews_Cards_VocabCardId",
                        column: x => x.VocabCardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardReviews_VocabCardId",
                table: "CardReviews",
                column: "VocabCardId");

            migrationBuilder.CreateIndex(
                name: "IX_Cards_State",
                table: "Cards",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardReviews");

            migrationBuilder.DropTable(
                name: "Cards");

            migrationBuilder.DropColumn(
                name: "SourcePhrase",
                table: "Errors");
        }
    }
}
