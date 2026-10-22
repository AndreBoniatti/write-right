using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WriteRight.Api.Data;

namespace WriteRight.Tests.Migrations;

/// <summary>
/// A migration reescreve todas as datas do banco e roda sozinha no boot. Se o texto
/// convertido não for o que o EF lê como DateTime, o histórico inteiro muda de horário
/// (ou de dia) sem erro nenhum.
/// </summary>
public sealed class StoreDatesAsUtcDateTimeTests : IDisposable
{
    private const string Before = "20261020015112_FoldStudyItemsIntoPatterns";
    private const string Stored = "2026-10-21 21:07:00.1234567+00:00";

    private static readonly DateTimeOffset Instant = DateTimeOffset.Parse(Stored);

    private static readonly (string Table, string Column)[] Dates =
    [
        ("Exercises", "CreatedAt"), ("Exercises", "CompletedAt"), ("Exercises", "DeletedAt"),
        ("Cards", "CreatedAt"), ("Cards", "DueAt"), ("Cards", "LastReviewedAt"),
        ("CardReviews", "ReviewedAt"),
        ("LlmCalls", "CreatedAt"),
        ("Analyses", "GeneratedAt"),
    ];

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly WriteRightDbContext _ctx;
    private readonly IMigrator _migrator;

    public StoreDatesAsUtcDateTimeTests()
    {
        _connection.Open();
        _ctx = new WriteRightDbContext(
            new DbContextOptionsBuilder<WriteRightDbContext>().UseSqlite(_connection).Options);
        _migrator = _ctx.GetService<IMigrator>();
        _migrator.Migrate(Before);

        Execute($"""
            INSERT INTO Exercises (Id, Status, SourceLanguage, TargetLanguage, SourceText, UserTranslation,
                                   CorrectedText, CreatedAt, CompletedAt, DeletedAt)
            VALUES (1, 'Completed', 'Portuguese', 'English', 's', 't', 'c', '{Stored}', '{Stored}', '{Stored}');
            INSERT INTO Exercises (Id, Status, SourceLanguage, TargetLanguage, SourceText, UserTranslation,
                                   CorrectedText, CreatedAt, CompletedAt, DeletedAt)
            VALUES (2, 'InProgress', 'Portuguese', 'English', 's', '', '', '{Stored}', NULL, NULL);
            INSERT INTO Cards (Id, SourceLanguage, TargetLanguage, Prompt, Answer, Hint, YourAttempt, Category,
                               State, DueAt, IntervalDays, Ease, Reps, Lapses, CreatedAt, LastReviewedAt)
            VALUES (1, 'Portuguese', 'English', 'p', 'a', 'h', 'y', 'WordChoice',
                    'Review', '{Stored}', 1, 2.5, 1, 0, '{Stored}', '{Stored}');
            INSERT INTO CardReviews (VocabCardId, ReviewedAt, TypedAnswer, WasCorrect, Rating, IntervalBefore, IntervalAfter)
            VALUES (1, '{Stored}', 'a', 1, 'Easy', 0, 1);
            INSERT INTO LlmCalls (Operation, Model, InputTokens, OutputTokens, CacheWriteTokens, CacheReadTokens,
                                  CreatedAt, DurationMs)
            VALUES ('Correction', 'm', 1, 1, 0, 0, '{Stored}', 0);
            INSERT INTO Analyses (GeneratedAt, PracticesAnalyzed, ErrorsAnalyzed, PatternsJson)
            VALUES ('{Stored}', 1, 1, '[]');
            """);
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private List<string?> Values(string table, string column)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""SELECT "{column}" FROM "{table}" ORDER BY rowid""";
        using var reader = command.ExecuteReader();
        var values = new List<string?>();
        while (reader.Read()) values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return values;
    }

    [Fact]
    public void Up_drops_the_offset_from_every_date_and_keeps_nulls()
    {
        _migrator.Migrate();

        foreach (var (table, column) in Dates)
            Assert.All(Values(table, column), v => Assert.True(v is null or "2026-10-21 21:07:00.1234567", $"{table}.{column}: {v}"));

        Assert.Null(Values("Exercises", "CompletedAt")[1]);
    }

    [Fact]
    public void Up_keeps_the_same_instant_when_read_through_the_model()
    {
        _migrator.Migrate();

        var practice = _ctx.Exercises.IgnoreQueryFilters().Single(p => p.Id == 1);
        Assert.Equal(Instant, practice.CreatedAt);
        Assert.Equal(Instant, practice.CompletedAt);
        Assert.Equal(TimeSpan.Zero, practice.CreatedAt.Offset);
        Assert.Equal(Instant, _ctx.Cards.Single().DueAt);
    }

    [Fact]
    public void Up_converts_another_offset_to_utc_instead_of_leaving_it_to_be_misread()
    {
        Execute("""
            INSERT INTO Analyses (Id, GeneratedAt, PracticesAnalyzed, ErrorsAnalyzed, PatternsJson)
            VALUES (99, '2026-10-21 18:07:00-03:00', 1, 1, '[]');
            """);

        _migrator.Migrate();

        Assert.Equal(DateTimeOffset.Parse("2026-10-21 21:07:00+00:00"), _ctx.Analyses.Single(a => a.Id == 99).GeneratedAt);
    }

    [Fact]
    public void Down_restores_the_original_text()
    {
        _migrator.Migrate();
        _migrator.Migrate(Before);

        foreach (var (table, column) in Dates)
            Assert.All(Values(table, column), v => Assert.True(v is null or Stored, $"{table}.{column}: {v}"));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _connection.Dispose();
    }
}
