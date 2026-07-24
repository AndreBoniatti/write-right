using Microsoft.EntityFrameworkCore;

namespace WriteRight.Api.Data;

public class WriteRightDbContext : DbContext
{
    public WriteRightDbContext(DbContextOptions<WriteRightDbContext> options) : base(options) { }

    public DbSet<ExerciseAttempt> Exercises => Set<ExerciseAttempt>();
    public DbSet<ExerciseError> Errors => Set<ExerciseError>();

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
    }
}
