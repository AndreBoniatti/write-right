using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WriteRight.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPracticeStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "Exercises",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Exercises",
                type: "TEXT",
                nullable: false,
                // "Em andamento" é o estado natural de uma prática existente sem status.
                // (No fluxo real toda linha grava o Status explicitamente.)
                defaultValue: "InProgress");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "Exercises");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Exercises");
        }
    }
}
