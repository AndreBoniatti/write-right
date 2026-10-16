using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WriteRight.Api.Migrations
{
    /// <inheritdoc />
    public partial class SoftDeletePractices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CardReviews_Cards_VocabCardId",
                table: "CardReviews");

            migrationBuilder.DropForeignKey(
                name: "FK_Errors_Exercises_ExerciseAttemptId",
                table: "Errors");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Exercises",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LlmCalls_AnalysisId",
                table: "LlmCalls",
                column: "AnalysisId");

            migrationBuilder.CreateIndex(
                name: "IX_LlmCalls_PracticeId",
                table: "LlmCalls",
                column: "PracticeId");

            migrationBuilder.AddForeignKey(
                name: "FK_CardReviews_Cards_VocabCardId",
                table: "CardReviews",
                column: "VocabCardId",
                principalTable: "Cards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Errors_Exercises_ExerciseAttemptId",
                table: "Errors",
                column: "ExerciseAttemptId",
                principalTable: "Exercises",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LlmCalls_Analyses_AnalysisId",
                table: "LlmCalls",
                column: "AnalysisId",
                principalTable: "Analyses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LlmCalls_Exercises_PracticeId",
                table: "LlmCalls",
                column: "PracticeId",
                principalTable: "Exercises",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CardReviews_Cards_VocabCardId",
                table: "CardReviews");

            migrationBuilder.DropForeignKey(
                name: "FK_Errors_Exercises_ExerciseAttemptId",
                table: "Errors");

            migrationBuilder.DropForeignKey(
                name: "FK_LlmCalls_Analyses_AnalysisId",
                table: "LlmCalls");

            migrationBuilder.DropForeignKey(
                name: "FK_LlmCalls_Exercises_PracticeId",
                table: "LlmCalls");

            migrationBuilder.DropIndex(
                name: "IX_LlmCalls_AnalysisId",
                table: "LlmCalls");

            migrationBuilder.DropIndex(
                name: "IX_LlmCalls_PracticeId",
                table: "LlmCalls");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Exercises");

            migrationBuilder.AddForeignKey(
                name: "FK_CardReviews_Cards_VocabCardId",
                table: "CardReviews",
                column: "VocabCardId",
                principalTable: "Cards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Errors_Exercises_ExerciseAttemptId",
                table: "Errors",
                column: "ExerciseAttemptId",
                principalTable: "Exercises",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
