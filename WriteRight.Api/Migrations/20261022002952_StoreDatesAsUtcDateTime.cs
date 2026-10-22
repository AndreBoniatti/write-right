using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WriteRight.Api.Migrations
{
    /// <inheritdoc />
    public partial class StoreDatesAsUtcDateTime : Migration
    {
        private static readonly (string Table, string Column)[] Dates =
        [
            ("Exercises", "CreatedAt"),
            ("Exercises", "CompletedAt"),
            ("Exercises", "DeletedAt"),
            ("Cards", "CreatedAt"),
            ("Cards", "DueAt"),
            ("Cards", "LastReviewedAt"),
            ("CardReviews", "ReviewedAt"),
            ("LlmCalls", "CreatedAt"),
            ("Analyses", "GeneratedAt"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // O app grava com UtcNow ("2026-10-21 21:07:00.1234567+00:00"): tirar o offset
            // deixa o texto que o EF grava pra DateTime, sem perder precisão. Outro offset
            // (só por edição à mão) é convertido pra UTC, com precisão de milissegundo —
            // deixado como está, seria lido como hora local, deslocado sem erro nenhum.
            foreach (var (table, column) in Dates)
                migrationBuilder.Sql($"""
                    UPDATE "{table}" SET "{column}" = CASE
                        WHEN "{column}" LIKE '%+00:00' THEN substr("{column}", 1, length("{column}") - 6)
                        ELSE strftime('%Y-%m-%d %H:%M:%f', "{column}") END
                    WHERE "{column}" LIKE '%+__:__' OR "{column}" LIKE '%-__:__';
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in Dates)
                migrationBuilder.Sql($"""
                    UPDATE "{table}" SET "{column}" = "{column}" || '+00:00'
                    WHERE "{column}" IS NOT NULL;
                    """);
        }
    }
}
