using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WriteRight.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmCallDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DurationMs",
                table: "LlmCalls",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "LlmCalls");
        }
    }
}
