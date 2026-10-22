using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WriteRight.Api.Migrations
{
    /// <inheritdoc />
    public partial class FoldStudyItemsIntoPatterns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Cada item de estudo passa a morar dentro do seu padrão, e a coluna sai sem
            // levar dado junto. Nas análises gravadas, o pareamento é pela posição: o
            // prompt antigo pedia um item por padrão, na mesma ordem (conferido título a
            // título nas análises existentes em 2026-10-19). O ORDER BY é explícito porque
            // a ordem dos padrões é a de importância, e o SQLite não garante a de um agregado.
            migrationBuilder.Sql("""
                UPDATE Analyses SET PatternsJson = (
                    SELECT json_group_array(json(
                        CASE WHEN s.value IS NULL THEN p.value
                             ELSE json_set(p.value, '$.study', json(s.value)) END) ORDER BY p.key)
                    FROM json_each(Analyses.PatternsJson) AS p
                    LEFT JOIN json_each(Analyses.StudyItemsJson) AS s ON s.key = p.key
                );
                """);

            migrationBuilder.DropColumn(
                name: "StudyItemsJson",
                table: "Analyses");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StudyItemsJson",
                table: "Analyses",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            // As duas expressões leem o PatternsJson de antes do UPDATE.
            migrationBuilder.Sql("""
                UPDATE Analyses SET
                    StudyItemsJson = (
                        SELECT json_group_array(json(json_extract(value, '$.study')) ORDER BY key)
                        FROM json_each(Analyses.PatternsJson)
                        WHERE json_extract(value, '$.study') IS NOT NULL),
                    PatternsJson = (
                        SELECT json_group_array(json(json_remove(value, '$.study')) ORDER BY key)
                        FROM json_each(Analyses.PatternsJson));
                """);
        }
    }
}
