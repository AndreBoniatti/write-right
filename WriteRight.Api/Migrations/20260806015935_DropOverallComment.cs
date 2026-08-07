using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WriteRight.Api.Migrations
{
    /// <inheritdoc />
    public partial class DropOverallComment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OverallComment",
                table: "Exercises");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OverallComment",
                table: "Exercises",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
