using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;

namespace WriteRight.Tests.Support;

/// <summary>
/// Banco SQLite in-memory para testes. Mantém a conexão <b>aberta</b> (senão o
/// <c>:memory:</c> some) e cria contextos novos sob demanda — assim um teste pode
/// gravar com um contexto e ler com outro, provando que os dados foram de fato ao
/// banco, não ao cache de identidade do EF.
///
/// Usa SQLite real de propósito (e não o provider EF InMemory): exercita os
/// value-converters enum-as-string e o comportamento relacional de verdade.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<WriteRightDbContext> _options;

    public TestDatabase()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<WriteRightDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new WriteRightDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    /// <summary>Um contexto novo apontando pro mesmo banco in-memory.</summary>
    public WriteRightDbContext NewContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
