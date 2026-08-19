using Microsoft.EntityFrameworkCore;

namespace WriteRight.Api.Data;

public class WriteRightDbContext : DbContext
{
    public WriteRightDbContext(DbContextOptions<WriteRightDbContext> options) : base(options) { }

    public DbSet<ExerciseAttempt> Exercises => Set<ExerciseAttempt>();
    public DbSet<ExerciseError> Errors => Set<ExerciseError>();
    public DbSet<AnalysisRecord> Analyses => Set<AnalysisRecord>();
    public DbSet<LlmCall> LlmCalls => Set<LlmCall>();
    public DbSet<VocabCard> Cards => Set<VocabCard>();
    public DbSet<CardReview> CardReviews => Set<CardReview>();

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

        var card = modelBuilder.Entity<VocabCard>();

        // NOT NULL no banco, não só no C#: a garantia de que todo card tem dica passa
        // a ser do schema, e não da disciplina de quem escreve o serviço. O CHECK
        // completa — sozinho, NOT NULL ainda deixaria passar string vazia, que na
        // tela dá exatamente o mesmo card sem resposta possível.
        card.Property(c => c.Hint).IsRequired();
        card.ToTable(t => t.HasCheckConstraint("CK_Card_HintNotEmpty", "trim(Hint) <> ''"));

        card.Property(c => c.State).HasConversion<string>();
        card.Property(c => c.Category).HasConversion<string>();
        card.Property(c => c.SourceLanguage).HasConversion<string>();
        card.Property(c => c.TargetLanguage).HasConversion<string>();

        // Índice pelo estado: a consulta quente é "o que está vencido" (State ativo +
        // DueAt). DueAt fora do índice de propósito — é DateTimeOffset, que o SQLite
        // não compara direito, e o filtro por data acontece em memória.
        card.HasIndex(c => c.State);

        var review = modelBuilder.Entity<CardReview>();
        review.Property(r => r.Rating).HasConversion<string>();

        // Aqui a FK EXISTE e cascateia: uma revisão sem o card que ela revisou não
        // significa nada. É o oposto do caso acima, e de propósito.
        review.HasOne(r => r.VocabCard)
              .WithMany(c => c.Reviews)
              .HasForeignKey(r => r.VocabCardId)
              .OnDelete(DeleteBehavior.Cascade);
    }
}
