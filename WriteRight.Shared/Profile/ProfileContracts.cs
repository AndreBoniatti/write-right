using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Shared.Profile;

/// <summary>Quantos erros caíram em cada gravidade — o que as barras empilhadas do perfil desenham.</summary>
public sealed record SeverityCounts(int BreaksMeaning, int Understandable, int Polish)
{
    /// <summary>Derivado das três contagens — por isso fica fora do JSON.</summary>
    [JsonIgnore]
    public int Total => BreaksMeaning + Understandable + Polish;

    public static SeverityCounts Of(IEnumerable<ErrorSeverity> severities)
    {
        int breaks = 0, understandable = 0, polish = 0;
        foreach (var s in severities)
        {
            switch (s)
            {
                case ErrorSeverity.BreaksMeaning: breaks++; break;
                case ErrorSeverity.Understandable: understandable++; break;
                case ErrorSeverity.Polish: polish++; break;
            }
        }
        return new(breaks, understandable, polish);
    }

    /// <summary>
    /// As três contagens do mais grave pro mais leve — a ordem da pilha no gráfico de
    /// evolução e da barra das categorias. Uma ordem só pros dois não divergirem.
    /// </summary>
    public IEnumerable<(ErrorSeverity Severity, int Count)> MostSevereFirst()
    {
        yield return (ErrorSeverity.BreaksMeaning, BreaksMeaning);
        yield return (ErrorSeverity.Understandable, Understandable);
        yield return (ErrorSeverity.Polish, Polish);
    }
}

/// <summary>
/// Uma categoria no perfil de fraquezas. <see cref="Count"/> é o volume bruto
/// (quantas vezes o erro apareceu); <see cref="Score"/> é o peso por severidade
/// (Quebra o sentido ×3, Compreensível ×2, Lapidação ×1). A ordenação e a
/// prioridade de estudo usam o <b>Score</b> — o que atrapalha vem antes do que é
/// só frequente. O <see cref="Count"/> fica visível pra dar a noção de volume, e
/// <see cref="BySeverity"/> mostra de onde o peso veio.
/// </summary>
public sealed record CategoryWeight(ErrorCategory Category, int Count, int Score, SeverityCounts BySeverity);

/// <summary>
/// Uma visão agregada do perfil: o histórico inteiro (<c>Lifetime</c>) ou o
/// recorte das práticas mais recentes (<c>Recent</c>).
/// </summary>
/// <param name="Attempts">Quantas práticas concluídas entraram nesta visão.</param>
/// <param name="TotalErrors">Total de erros (volume bruto) na visão.</param>
/// <param name="ByCategory">Categorias, da de maior peso (Score) pra menor.</param>
public sealed record ProfileView(
    int Attempts,
    int TotalErrors,
    IReadOnlyList<CategoryWeight> ByCategory);

/// <summary>
/// Uma prática concluída na linha do tempo do perfil — o insumo do gráfico de evolução.
/// </summary>
/// <param name="PracticeId">A prática.</param>
/// <param name="CompletedAt">Quando foi concluída.</param>
/// <param name="Level">Nível escolhido pra ela (o tooltip mostra: nível mais fácil muda a régua).</param>
/// <param name="Words">Palavras da tradução do usuário — o denominador da taxa de erros.</param>
/// <param name="Errors">Erros da correção, por gravidade.</param>
public sealed record PracticePoint(
    int PracticeId,
    DateTimeOffset CompletedAt,
    CefrLevel? Level,
    int Words,
    SeverityCounts Errors);

/// <summary>
/// Perfil de fraquezas do usuário — duas visões do mesmo dado, ponderadas por
/// severidade: <see cref="Lifetime"/> (todo o histórico; é o insumo do gerador
/// adaptativo) e <see cref="Recent"/> (as últimas práticas, onde ele vaza agora) —
/// mais a <see cref="Timeline"/>, uma linha por prática concluída, da mais antiga
/// pra mais nova.
/// </summary>
public sealed record ErrorProfile(
    ProfileView Lifetime,
    ProfileView Recent,
    IReadOnlyList<PracticePoint> Timeline);

/// <summary>
/// Um erro real que o usuário cometeu numa dada categoria — o material da tela de
/// revisão ("meus erros de Preposição"). Vem das práticas já concluídas; nenhuma IA
/// nova é chamada, é só releitura do que a correção registrou na hora.
/// </summary>
/// <param name="PracticeId">Prática de origem (pra reler no contexto, se quiser).</param>
/// <param name="CompletedAt">Quando aquela prática foi concluída.</param>
/// <param name="Severity">Gravidade do erro.</param>
/// <param name="Original">O trecho errado, como o usuário escreveu.</param>
/// <param name="Correction">O trecho corrigido.</param>
/// <param name="Explanation">A explicação didática (o porquê), em português.</param>
public sealed record CategoryError(
    int PracticeId,
    DateTimeOffset CompletedAt,
    ErrorSeverity Severity,
    string Original,
    string Correction,
    string Explanation);
