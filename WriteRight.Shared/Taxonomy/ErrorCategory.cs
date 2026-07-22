namespace WriteRight.Shared.Taxonomy;

/// <summary>
/// Categorias fixas de erro de escrita — o ativo central do WriteRight.
///
/// Toda correção da IA é <b>obrigada</b> a classificar cada erro em UMA destas
/// (via structured output). Isso é o que permite acumular o perfil de fraquezas
/// do usuário e direcionar a geração dos próximos exercícios.
///
/// Regras:
///  • Lista FECHADA. Cada categoria nova fragmenta os dados históricos — não
///    adicionar sem intenção clara.
///  • Persistir como STRING no banco (ver DbContext), nunca como int, pra os
///    dados ficarem legíveis e resistirem a reordenação do enum.
///  • Uma categoria primária por erro; desempate = a mais específica.
/// </summary>
public enum ErrorCategory
{
    // ── Estrutura / gramática ──────────────────────────────
    SubjectOmission,
    VerbTense,
    VerbForm,
    Agreement,
    Article,
    Preposition,
    Pronoun,
    WordOrder,
    NumberCountability,
    MissingOrExtraWord,

    // ── Vocabulário / sentido ──────────────────────────────
    WordChoice,
    FalseCognate,
    Collocation,
    LiteralTranslation,

    // ── Mecânica ───────────────────────────────────────────
    Spelling,
    Capitalization,
    Punctuation,

    // ── Estilo ─────────────────────────────────────────────
    Naturalness,

    // ── Escape (usar só quando nada mais encaixa; monitorar a taxa) ──
    Other,
}
