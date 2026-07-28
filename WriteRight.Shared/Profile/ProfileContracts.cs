using System;
using System.Collections.Generic;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Shared.Profile;

/// <summary>
/// Uma categoria no perfil de fraquezas. <see cref="Count"/> é o volume bruto
/// (quantas vezes o erro apareceu); <see cref="Score"/> é o peso por severidade
/// (Quebra o sentido ×3, Compreensível ×2, Lapidação ×1). A ordenação e a
/// prioridade de estudo usam o <b>Score</b> — o que atrapalha vem antes do que é
/// só frequente. O <see cref="Count"/> fica visível pra dar a noção de volume.
/// </summary>
public sealed record CategoryWeight(ErrorCategory Category, int Count, int Score);

/// <summary>
/// Uma visão agregada do perfil: o histórico inteiro (<c>Lifetime</c>) ou o
/// recorte das práticas mais recentes (<c>Recent</c>).
/// </summary>
/// <param name="Attempts">Quantas práticas concluídas entraram nesta visão.</param>
/// <param name="TotalErrors">Total de erros (volume bruto) na visão.</param>
/// <param name="TotalScore">Soma dos pesos por severidade na visão.</param>
/// <param name="ByCategory">Categorias, da de maior peso (Score) pra menor.</param>
public sealed record ProfileView(
    int Attempts,
    int TotalErrors,
    int TotalScore,
    IReadOnlyList<CategoryWeight> ByCategory);

/// <summary>
/// Perfil de fraquezas do usuário — duas visões do mesmo dado, ponderadas por
/// severidade: <see cref="Lifetime"/> (todo o histórico; é o insumo do gerador
/// adaptativo) e <see cref="Recent"/> (as últimas práticas, onde ele vaza agora).
/// </summary>
public sealed record ErrorProfile(
    ProfileView Lifetime,
    ProfileView Recent);

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
