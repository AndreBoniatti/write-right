using Microsoft.EntityFrameworkCore;

namespace WriteRight.Api.Data;

public class WriteRightDbContext : DbContext
{
    public WriteRightDbContext(DbContextOptions<WriteRightDbContext> options) : base(options) { }

    public DbSet<ExerciseAttempt> Exercises => Set<ExerciseAttempt>();
    public DbSet<ExerciseError> Errors => Set<ExerciseError>();
    public DbSet<AnalysisRecord> Analyses => Set<AnalysisRecord>();
    public DbSet<LlmCall> LlmCalls => Set<LlmCall>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var exercise = modelBuilder.Entity<ExerciseAttempt>();
        exercise.Property(e => e.Status).HasConversion<string>();
        exercise.Property(e => e.SourceLanguage).HasConversion<string>();
        exercise.Property(e => e.TargetLanguage).HasConversion<string>();
        exercise.Property(e => e.Level).HasConversion<string>();

        // Blindagem no banco (defense-in-depth): origem e alvo nunca podem ser
        // iguais. A validação da aplicação dá o erro amigável; isto é a rede de
        // segurança de última instância, mesmo que algo passe por fora do serviço.
        exercise.ToTable(t => t.HasCheckConstraint(
            "CK_Exercise_LanguagesDiffer", "SourceLanguage <> TargetLanguage"));

        var error = modelBuilder.Entity<ExerciseError>();

        // Enums como STRING — legível no banco, resiliente a reordenação do enum.
        error.Property(e => e.Category).HasConversion<string>();
        error.Property(e => e.Severity).HasConversion<string>();

        // Índice pra a agregação do perfil (contar erros por categoria).
        error.HasIndex(e => e.Category);

        error.HasOne(e => e.ExerciseAttempt)
             .WithMany(a => a.Errors)
             .HasForeignKey(e => e.ExerciseAttemptId)
             .OnDelete(DeleteBehavior.Cascade);

        var call = modelBuilder.Entity<LlmCall>();
        call.Property(c => c.Operation).HasConversion<string>();

        // Índice por data: a consulta que importa depois é "gasto no período"
        // (fatura, cota). PracticeId/AnalysisId ficam SEM FK de propósito —
        // ver o resumo de LlmCall.
        call.HasIndex(c => c.CreatedAt);

        // Custo como TEXT (padrão do SQLite pra decimal): round-trip exato, sem o
        // erro de arredondamento que double traria em dinheiro. A contrapartida é
        // que SUM/ORDER BY não descem pro SQL — agrega-se em memória, igual ao
        // resto do app (mesmo motivo do ListPracticesAsync).
        call.Property(c => c.CostUsd).HasColumnType("TEXT");
    }
}
