using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WriteRight.Api.Migrations
{
    /// <inheritdoc />
    public partial class SeparateCardMinting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // O deck antigo vai embora inteiro (decisão de 2026-10-07): os cards nasciam da
            // categoria da correção, e 32% deles eram ruins. As práticas antigas NÃO
            // recebem cards novos — ficam com CardsStatus nulo ("não se aplica"), e o
            // deck recomeça vazio a partir da próxima correção. Sem volta no Down.
            migrationBuilder.Sql("DELETE FROM CardReviews;");
            migrationBuilder.Sql("DELETE FROM Cards;");

            migrationBuilder.DropColumn(
                name: "SourcePhrase",
                table: "Errors");

            migrationBuilder.AddColumn<string>(
                name: "CardsStatus",
                table: "Exercises",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Alternatives",
                table: "Cards",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CardsStatus",
                table: "Exercises");

            migrationBuilder.DropColumn(
                name: "Alternatives",
                table: "Cards");

            migrationBuilder.AddColumn<string>(
                name: "SourcePhrase",
                table: "Errors",
                type: "TEXT",
                nullable: true);
        }
    }
}
