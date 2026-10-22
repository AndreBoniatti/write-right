using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WriteRight.Api.Data;

namespace WriteRight.Tests.Migrations;

/// <summary>
/// A migration que move cada item de estudo pra dentro do seu padrão reescreve TODA
/// análise gravada, roda sozinha no boot (<c>Database.Migrate</c>) e derruba a coluna
/// antiga. Um caminho JSON errado apagaria as regras do histórico sem erro nenhum; daí
/// rodar a migration de verdade, contra o SQLite que vai junto com o app.
/// </summary>
public sealed class FoldStudyItemsIntoPatternsTests : IDisposable
{
    private const string Before = "20261016030320_AddLlmCallDuration";

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly WriteRightDbContext _ctx;
    private readonly IMigrator _migrator;

    public FoldStudyItemsIntoPatternsTests()
    {
        _connection.Open();
        _ctx = new WriteRightDbContext(
            new DbContextOptionsBuilder<WriteRightDbContext>().UseSqlite(_connection).Options);
        _migrator = _ctx.GetService<IMigrator>();
        _migrator.Migrate(Before);
    }

    private static string Pattern(string title) =>
        new JsonObject { ["title"] = title, ["diagnosis"] = "d", ["categories"] = new JsonArray(), ["evidence"] = new JsonArray() }
            .ToJsonString();

    private static string Study(string title) =>
        new JsonObject { ["kind"] = "Rule", ["title"] = title, ["content"] = "c" }.ToJsonString();

    private void InsertOld(string patternsJson, string studyItemsJson) =>
        _ctx.Database.ExecuteSql($"""
            INSERT INTO Analyses (GeneratedAt, PracticesAnalyzed, ErrorsAnalyzed, PatternsJson, StudyItemsJson)
            VALUES ('2026-08-06 01:30:17+00:00', 19, 70, {patternsJson}, {studyItemsJson})
            """);

    private List<string> Column(string column)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM Analyses ORDER BY Id";
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    [Fact]
    public void Up_puts_each_study_item_inside_the_pattern_at_the_same_position()
    {
        InsertOld(
            $"[{Pattern("p1")},{Pattern("p2")},{Pattern("p3")}]",
            $"[{Study("s1")},{Study("s2")},{Study("s3")}]");

        _migrator.Migrate();

        var patterns = JsonNode.Parse(Column("PatternsJson").Single())!.AsArray();
        Assert.Equal(["p1", "p2", "p3"], patterns.Select(p => (string)p!["title"]!));
        Assert.Equal(["s1", "s2", "s3"], patterns.Select(p => (string)p!["study"]!["title"]!));
        Assert.Equal("Rule", (string)patterns[0]!["study"]!["kind"]!); // objeto, não string escapada
    }

    [Fact]
    public void Up_leaves_a_pattern_without_a_matching_item_with_no_study()
    {
        InsertOld($"[{Pattern("p1")},{Pattern("p2")}]", $"[{Study("s1")}]");
        InsertOld("[]", "[]");

        _migrator.Migrate();

        var rows = Column("PatternsJson");
        var first = JsonNode.Parse(rows[0])!.AsArray();
        Assert.Equal("s1", (string)first[0]!["study"]!["title"]!);
        Assert.Null(first[1]!["study"]);
        Assert.Equal("[]", rows[1]);
    }

    [Fact]
    public void Up_drops_the_old_column()
    {
        _migrator.Migrate();

        var columns = _ctx.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Analyses')").ToList();
        Assert.DoesNotContain("StudyItemsJson", columns);
    }

    [Fact]
    public void Down_restores_the_original_rows()
    {
        var patterns = $"[{Pattern("p1")},{Pattern("p2")}]";
        var studies = $"[{Study("s1")},{Study("s2")}]";
        InsertOld(patterns, studies);

        _migrator.Migrate();
        _migrator.Migrate(Before);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(patterns), JsonNode.Parse(Column("PatternsJson").Single())));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(studies), JsonNode.Parse(Column("StudyItemsJson").Single())));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _connection.Dispose();
    }
}
