using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Shared.Cards;
using WriteRight.Shared.Taxonomy;

namespace WriteRight.Api.Services;

/// <summary>Desfecho de uma operação sobre um card.</summary>
public enum CardOutcome
{
    Ok,
    NotFound,
    /// <summary>O card saiu da rotação (descartado) e não aceita mais revisão.</summary>
    Inactive,
}

/// <summary>
/// O deck de vocabulário: cunha cards a partir dos erros reais, entrega a sessão
/// de revisão, agenda e registra.
///
/// Cunha SÓ erros de vocabulário/estilo. Erro de gramática é regra, e regra
/// generaliza — quem cuida disso é o loop de categorias, que dirige a geração do
/// próximo texto. Item léxico não generaliza: por isso precisa de deck.
/// </summary>
public sealed class CardService
{
    private readonly WriteRightDbContext _db;

    public CardService(WriteRightDbContext db) => _db = db;

    /// <summary>
    /// Categorias que viram card, derivadas do catálogo — assim uma categoria nova
    /// no grupo Vocabulário passa a cunhar sozinha, sem lista paralela pra esquecer.
    /// </summary>
    public static IReadOnlySet<ErrorCategory> MintableCategories { get; } =
        ErrorCatalog.All
            .Where(i => i.Group is ErrorGroup.Vocabulary or ErrorGroup.Style)
            .Select(i => i.Category)
            .ToHashSet();

    /// <summary>
    /// Cunha os cards de uma prática já corrigida. Não salva — os cards entram no
    /// change tracker e são persistidos na MESMA transação de quem chamou.
    /// </summary>
    public async Task<int> MintForPracticeAsync(ExerciseAttempt practice, CancellationToken ct = default)
    {
        var candidates = practice.Errors
            .Where(e => MintableCategories.Contains(e.Category))
            .ToList();

        if (candidates.Count == 0) return 0;

        // A chave de deduplicação é a resposta NORMALIZADA, que não desce pro SQL —
        // então materializa e cruza em memória (volume pessoal; mesmo compromisso do
        // resto do app). Inclui DESCARTADOS de propósito: se o usuário jogou um card
        // fora, errar de novo não pode ressuscitá-lo, senão o descarte não significa nada.
        var keys = candidates.Select(c => AnswerMatch.Normalize(c.Correction)).ToHashSet();
        var existing = (await _db.Cards.ToListAsync(ct))
            .Where(c => keys.Contains(AnswerMatch.Normalize(c.Answer)))
            .GroupBy(c => AnswerMatch.Normalize(c.Answer))
            .ToDictionary(g => g.Key, g => g.First());

        var now = DateTimeOffset.UtcNow;
        var minted = 0;

        foreach (var error in candidates)
        {
            var prompt = ClozeBuilder.Build(practice.CorrectedText, error.Correction);
            if (prompt is null) continue; // sem frase utilizável, o card não nasce

            var hint = Clean(error.SourcePhrase);
            var key = AnswerMatch.Normalize(error.Correction);

            if (existing.TryGetValue(key, out var card))
            {
                if (card.State == CardState.Discarded) continue;

                // Reincidência: errar de novo, escrevendo de verdade, é a evidência
                // mais forte que existe de que o item não entrou — mais forte que
                // qualquer botão de auto-avaliação. Reprograma pelo MESMO caminho de
                // um erro na revisão, pra "esquecer" ter uma definição só.
                //
                // Mas NÃO gera linha em CardReviews: o log responde "quando um card
                // volta depois de N dias, qual a taxa de acerto?", e um fracasso que
                // aconteceu na escrita — sem o card ter sido mostrado — não tem
                // intervalo pra atribuir. Misturar os dois corromperia justamente a
                // estatística que justifica o log existir.
                Apply(card, CardScheduler.Next(Snapshot(card), wasCorrect: false, CardRating.Again, now));

                // Conteúdo atualizado pro fracasso mais recente: mesmo item léxico,
                // contexto mais fresco. O histórico do card não se perde (ele guarda
                // resposta digitada, não enunciado).
                //
                // Mas só troca se o par novo estiver COMPLETO: enunciado novo com
                // dica velha apontaria pra uma frase que não é mais a da tela. Sem
                // dica, o par antigo (coerente) fica, e a reincidência é registrada
                // do mesmo jeito — o sinal não se perde.
                if (hint is not null)
                {
                    card.Prompt = prompt;
                    card.Hint = hint;
                    card.YourAttempt = error.Original;
                }
                continue;
            }

            // Sem dica o card NÃO NASCE. A lacuna sozinha não tem resposta única —
            // "at the ___ center" aceita qualquer coisa —, então o card seria errado
            // pra sempre, contando lapso e sujando a estatística que diz se o
            // agendador funciona. Mesmo critério do ClozeBuilder: card ruim é pior
            // que card nenhum. Custa ~2% dos erros de vocabulário, e vale.
            if (hint is null) continue;

            var fresh = new VocabCard
            {
                SourceLanguage = practice.SourceLanguage,
                TargetLanguage = practice.TargetLanguage,
                Prompt = prompt,
                Answer = error.Correction.Trim(),
                Hint = hint,
                YourAttempt = error.Original,
                Category = error.Category,
                CreatedAt = now,
                DueAt = now, // card novo nasce vencido: revisável na mesma hora
            };

            _db.Cards.Add(fresh);
            existing[key] = fresh; // dois erros iguais na mesma prática não viram dois cards
            minted++;
        }

        return minted;
    }

    /// <summary>
    /// A fila do dia: tudo que está vencido, um card por frase (ver <see cref="Queue"/>),
    /// em ordem sorteada.
    ///
    /// SEM teto diário de propósito: o que não for revisado continua vencido, e o
    /// usuário para quando quiser. Um limite exigiria guardar "quantos hoje", que é
    /// estado novo pra resolver um problema que ainda não existe — e o corte por
    /// frase já espalha o volume sozinho.
    /// </summary>
    public async Task<IReadOnlyList<CardReviewItem>> GetDueAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Materializa e ordena em memória: o SQLite não ordena DateTimeOffset direito
        // (mesmo motivo do ListPracticesAsync). Volume pessoal, custo irrelevante.
        //
        // Traz os APOSENTADOS junto: eles não entram na fila, mas o Queue precisa
        // vê-los pra saber se um irmão da frase já foi revisado hoje.
        var cards = await _db.Cards
            .Where(c => c.State != CardState.Discarded)
            .ToListAsync(ct);

        // Sorteia a ordem. Sem isto o desempate é o Id, e como a cunhagem carimba
        // DueAt no mesmo instante, o Id decide sozinho: a sessão começaria sempre
        // pela mesma frase, e você passaria a lembrar da resposta pela POSIÇÃO em
        // vez de pelo inglês. O dia continua mandando — card atrasado há mais tempo
        // vem antes —, o sorteio só desempata dentro do mesmo dia.
        //
        // O sorteio fica aqui, e não em Queue, de propósito: Queue também alimenta o
        // contador do deck, que precisa ser determinístico.
        return Queue(cards, now)
            .OrderBy(c => c.DueAt.Date)
            .ThenBy(_ => Random.Shared.Next())
            .Select(ToReviewItem)
            .ToList();
    }

    /// <summary>
    /// A fila propriamente dita, sobre cards já materializados. Uma regra só, usada
    /// pela sessão e pelo contador do deck — dois cálculos dariam dois números
    /// diferentes pra "quanto tenho pra revisar", que é a pergunta que o usuário faz.
    ///
    /// Uma frase entrega <b>um card por DIA</b>, não um por consulta. A diferença é
    /// tudo: revisar um card e receber o irmão em seguida seria esconder a resposta
    /// e devolvê-la trinta segundos depois de você tê-la lido — os dois enunciados
    /// são a mesma frase com a lacuna em lugares diferentes, então cada um imprime
    /// a resposta do outro. Só o dia seguinte esfria essa leitura.
    ///
    /// O dia é o LOCAL, não o UTC: "hoje" é o dia de quem estuda.
    /// </summary>
    private static List<VocabCard> Queue(IEnumerable<VocabCard> cards, DateTimeOffset now)
    {
        var hoje = now.ToLocalTime().Date;

        return cards
            // Só DESCARTADOS saem antes do agrupamento. Aposentados ficam: um card
            // pode se aposentar na revisão de HOJE, e se ele saísse aqui, o irmão
            // dele deixaria de estar bloqueado e apareceria na mesma sessão — logo
            // depois de a resposta dele ter sido lida no enunciado que você acabou
            // de responder. Descartado é diferente: está fora, e o que ele fez antes
            // de sair não bloqueia mais ninguém.
            .Where(c => c.State != CardState.Discarded)
            .GroupBy(SentenceKey)
            // A frase inteira sai do dia assim que um card dela é revisado — inclusive
            // o card revisado, que já foi reagendado e não está mais vencido.
            .Where(g => !g.Any(c => c.LastReviewedAt?.ToLocalTime().Date == hoje))
            .Select(g => g
                .Where(c => c.State != CardState.Retired && c.DueAt <= now)
                // Menos revisado primeiro: faz os irmãos se revezarem dia após dia,
                // em vez de o mesmo card (o de Id menor) ganhar sempre e os outros
                // nunca aparecerem.
                .OrderBy(c => c.Reps)
                .ThenBy(c => c.Id)
                .FirstOrDefault())
            .OfType<VocabCard>()
            .ToList();
    }

    /// <summary>
    /// Identifica a frase de origem de um card, reconstruindo-a: a lacuna preenchida
    /// com a resposta devolve exatamente a frase de onde o card saiu. Derivar em vez
    /// de guardar uma coluna faz isto valer também pros cards já cunhados, sem
    /// migration nem passe de correção nos dados.
    /// </summary>
    private static string SentenceKey(VocabCard card) =>
        AnswerMatch.Normalize(card.Prompt.Replace(Cloze.Blank, card.Answer));

    /// <summary>
    /// Confere a resposta e REVELA — sem mudar nada. A separação entre conferir e
    /// agendar existe porque o "quase" é adjudicado pelo usuário: ele vê o diff e
    /// decide. Agendar aqui tiraria essa decisão dele.
    /// </summary>
    public async Task<(CardOutcome Outcome, CardCheckResult? Result)> CheckAsync(
        int id, string typedAnswer, CancellationToken ct = default)
    {
        var card = await _db.Cards.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (card is null) return (CardOutcome.NotFound, null);
        if (card.State == CardState.Discarded) return (CardOutcome.Inactive, null);

        // As três hipóteses de intervalo, pra os botões dizerem o que fazem antes de
        // serem clicados. Nada é gravado — é o mesmo agendador rodando a seco.
        var atual = Snapshot(card);
        var now = DateTimeOffset.UtcNow;
        double Quando(bool acertou, CardRating rating) =>
            CardScheduler.Next(atual, acertou, rating, now).IntervalDays;

        return (CardOutcome.Ok, new CardCheckResult(
            AnswerMatch.Check(typedAnswer, card.Answer),
            card.Answer,
            card.YourAttempt,
            AgainDays: Quando(false, CardRating.Again),
            HardDays: Quando(true, CardRating.Hard),
            EasyDays: Quando(true, CardRating.Easy)));
    }

    /// <summary>Fecha a revisão: reprograma o card e grava a linha do log.</summary>
    public async Task<(CardOutcome Outcome, CardReviewResult? Result)> ReviewAsync(
        int id, CardReviewRequest request, CancellationToken ct = default)
    {
        var card = await _db.Cards.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (card is null) return (CardOutcome.NotFound, null);
        if (card.State == CardState.Discarded) return (CardOutcome.Inactive, null);

        var now = DateTimeOffset.UtcNow;
        var before = Snapshot(card);

        // Errou → o rating é Again, não o que veio no corpo: fácil/difícil só existe
        // sobre acerto, e aceitar "Easy" num erro deixaria o ease subir errado.
        var rating = request.WasCorrect ? request.Rating : CardRating.Again;
        var after = CardScheduler.Next(before, request.WasCorrect, rating, now);

        Apply(card, after);
        card.LastReviewedAt = now;

        _db.CardReviews.Add(new CardReview
        {
            VocabCardId = card.Id,
            ReviewedAt = now,
            TypedAnswer = request.TypedAnswer,
            WasCorrect = request.WasCorrect,
            Rating = rating,
            IntervalBefore = before.IntervalDays,
            IntervalAfter = after.IntervalDays,
        });

        await _db.SaveChangesAsync(ct);

        var remaining = (await GetDueAsync(ct)).Count;
        return (CardOutcome.Ok, new CardReviewResult(
            after.State, after.IntervalDays, after.DueAt, remaining));
    }

    /// <summary>
    /// Descarta um card ruim (erro de digitação classificado como vocabulário, trecho
    /// sem resposta única). Não apaga: um card apagado voltaria a nascer no próximo
    /// erro igual, e o descarte precisa ser definitivo pra significar alguma coisa.
    /// </summary>
    public async Task<CardOutcome> DiscardAsync(int id, CancellationToken ct = default)
    {
        var card = await _db.Cards.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (card is null) return CardOutcome.NotFound;

        card.State = CardState.Discarded;
        await _db.SaveChangesAsync(ct);
        return CardOutcome.Ok;
    }

    /// <summary>O deck inteiro: contadores + cards, do mais recente pro mais antigo.</summary>
    public async Task<DeckView> GetDeckAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var cards = await _db.Cards.ToListAsync(ct);

        var active = cards.Where(c => c.State != CardState.Discarded).ToList();
        var summary = new DeckSummary(
            Total: active.Count,
            New: active.Count(c => c.State == CardState.New),
            // Mesma fila da sessão, e não "todo card com DueAt vencido": os irmãos
            // enterrados não são revisáveis agora, e contá-los aqui faria o deck
            // anunciar um número que a sessão não entrega.
            Due: Queue(active, now).Count,
            Learning: active.Count(c => c.State == CardState.Learning),
            Review: active.Count(c => c.State == CardState.Review),
            Retired: active.Count(c => c.State == CardState.Retired),
            Leeches: active.Count(c => CardScheduler.IsLeech(c.Lapses)));

        var rows = cards
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Select(c => new DeckCard(
                c.Id, c.Prompt, c.Answer, c.Hint, c.YourAttempt,
                c.Category, c.State,
                c.State == CardState.New ? null : c.DueAt,
                c.IntervalDays, c.Reps, c.Lapses, CardScheduler.IsLeech(c.Lapses)))
            .ToList();

        return new DeckView(summary, rows);
    }

    private static CardSchedule Snapshot(VocabCard c) =>
        new(c.State, c.IntervalDays, c.Ease, c.Reps, c.Lapses, c.DueAt);

    private static void Apply(VocabCard card, CardSchedule s)
    {
        card.State = s.State;
        card.IntervalDays = s.IntervalDays;
        card.Ease = s.Ease;
        card.Reps = s.Reps;
        card.Lapses = s.Lapses;
        card.DueAt = s.DueAt;
    }

    private static CardReviewItem ToReviewItem(VocabCard c) => new(
        c.Id, c.Prompt, c.Hint, c.Category, c.SourceLanguage, c.TargetLanguage,
        c.State, c.Reps, c.Lapses);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
