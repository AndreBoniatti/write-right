using Microsoft.EntityFrameworkCore;

namespace WriteRight.Api.Data;

public class WriteRightDbContext : DbContext
{
    /// <summary>
    /// Nome do filtro de exclusão lógica. Nomeado pra que quem precisa enxergar as
    /// excluídas desligue SÓ ele (<c>IgnoreQueryFilters([SoftDeleteFilter])</c>): um
    /// <c>IgnoreQueryFilters()</c> sem nome desligaria também qualquer filtro que vier
    /// depois — o de usuário, por exemplo, se o app deixar de ser de uma pessoa só.
    /// </summary>
    public const string SoftDeleteFilter = "SoftDelete";

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
        exercise.Property(e => e.CardsStatus).HasConversion<string>();

        // Blindagem no banco (defense-in-depth): origem e alvo nunca podem ser
        // iguais. A validação da aplicação dá o erro amigável; isto é a rede de
        // segurança de última instância, mesmo que algo passe por fora do serviço.
        exercise.ToTable(t => t.HasCheckConstraint(
            "CK_Exercise_LanguagesDiffer", "SourceLanguage <> TargetLanguage"));

        // Exclusão lógica: a prática excluída some de toda consulta sem que ninguém
        // precise lembrar de filtrar. Quem precisa dela desliga o filtro pelo nome — e
        // são só as leituras de FATO: o dia praticado (série) e o gasto (custo médio).
        exercise.HasQueryFilter(SoftDeleteFilter, p => p.DeletedAt == null);

        var error = modelBuilder.Entity<ExerciseError>();

        // Enums como STRING — legível no banco, resiliente a reordenação do enum.
        error.Property(e => e.Category).HasConversion<string>();
        error.Property(e => e.Severity).HasConversion<string>();

        // Índice pra a agregação do perfil (contar erros por categoria).
        error.HasIndex(e => e.Category);

        // Restrict, não Cascade: nada é apagado de verdade. Se algum dia alguém fizer um
        // DELETE físico numa prática, o banco recusa em vez de levar os erros junto.
        error.HasOne(e => e.ExerciseAttempt)
             .WithMany(a => a.Errors)
             .HasForeignKey(e => e.ExerciseAttemptId)
             .OnDelete(DeleteBehavior.Restrict);

        // Os erros seguem a prática: os de uma prática excluída saem do perfil, da
        // revisão por categoria e das PRÓXIMAS análises (a já gerada guarda a evidência
        // como cópia, de propósito). É também o filtro que o EF pede quando o lado
        // principal de uma relação obrigatória tem filtro próprio. Mesmo nome: desligar
        // a exclusão lógica desliga os dois juntos.
        error.HasQueryFilter(SoftDeleteFilter, e => e.ExerciseAttempt!.DeletedAt == null);

        var call = modelBuilder.Entity<LlmCall>();
        call.Property(c => c.Operation).HasConversion<string>();

        // Índice por data: a consulta que importa depois é "gasto no período"
        // (fatura, cota).
        call.HasIndex(c => c.CreatedAt);

        // FKs opcionais (geração que falhou não tem prática; análise sem lastro não
        // tem registro) e Restrict: o gasto aconteceu, e nenhuma exclusão pode levá-lo.
        call.HasOne<ExerciseAttempt>()
            .WithMany()
            .HasForeignKey(c => c.PracticeId)
            .OnDelete(DeleteBehavior.Restrict);
        call.HasOne<AnalysisRecord>()
            .WithMany()
            .HasForeignKey(c => c.AnalysisId)
            .OnDelete(DeleteBehavior.Restrict);

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

        // Restrict pelo mesmo motivo dos erros: card nunca é apagado (descartar só
        // marca), e o log de revisões é o único jeito de saber se o agendador funciona.
        // Um DELETE físico por engano tem que falhar, não levar o histórico junto.
        review.HasOne(r => r.VocabCard)
              .WithMany(c => c.Reviews)
              .HasForeignKey(r => r.VocabCardId)
              .OnDelete(DeleteBehavior.Restrict);
    }
}
