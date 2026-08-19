using Microsoft.EntityFrameworkCore;
using WriteRight.Api.Data;
using WriteRight.Api.Services;
using WriteRight.Shared;
using WriteRight.Shared.Cards;
using WriteRight.Shared.Practices;
using WriteRight.Shared.Taxonomy;
using WriteRight.Tests.Support;

namespace WriteRight.Tests.Cards;

/// <summary>
/// A cunhagem e o ciclo de revisão, contra SQLite real (value-converters de
/// verdade). O que importa aqui é o recorte: o deck existe porque o loop por
/// CATEGORIA não alcança vocabulário — item léxico não generaliza como regra —,
/// então cunhar a categoria errada descaracteriza o módulo.
/// </summary>
public sealed class CardServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();

    private CardService Service(out WriteRightDbContext ctx)
    {
        ctx = _db.NewContext();
        return new CardService(ctx);
    }

    /// <summary>
    /// Semeia uma prática concluída e cunha seus cards. Salva antes de cunhar
    /// porque a cunhagem precisa dos Ids dos erros — a mesma ordem da produção.
    /// </summary>
    private async Task<int> SeedAndMintAsync(
        string correctedText,
        params (ErrorCategory Category, string Original, string Correction, string? SourcePhrase)[] errors)
    {
        await using var ctx = _db.NewContext();

        var practice = new ExerciseAttempt
        {
            Status = PracticeStatus.Completed,
            SourceLanguage = Language.Portuguese,
            TargetLanguage = Language.English,
            SourceText = "Um texto em português.",
            UserTranslation = "A translation.",
            CorrectedText = correctedText,
            CompletedAt = DateTimeOffset.UtcNow,
            Errors = errors.Select(e => new ExerciseError
            {
                Category = e.Category,
                Severity = ErrorSeverity.Understandable,
                Original = e.Original,
                Correction = e.Correction,
                Explanation = "porquê",
                SourcePhrase = e.SourcePhrase,
            }).ToList(),
        };

        ctx.Exercises.Add(practice);
        await ctx.SaveChangesAsync();

        var minted = await new CardService(ctx).MintForPracticeAsync(practice);
        await ctx.SaveChangesAsync();
        return minted;
    }

    [Fact]
    public async Task Mints_a_card_from_a_vocabulary_error()
    {
        await SeedAndMintAsync(
            "The old buildings have colorful façades and clay roofs.",
            (ErrorCategory.WordChoice, "color façades", "colorful façades", "fachadas coloridas"));

        await using var ctx = _db.NewContext();
        var card = await ctx.Cards.SingleAsync();

        Assert.Equal("The old buildings have ___ and clay roofs.", card.Prompt);
        Assert.Equal("colorful façades", card.Answer);
        Assert.Equal("fachadas coloridas", card.Hint);
        Assert.Equal("color façades", card.YourAttempt);
        Assert.Equal(CardState.New, card.State);
    }

    /// <summary>
    /// Erro de gramática NÃO vira card. Regra generaliza (aprendeu "excited about",
    /// transfere) e quem cuida disso é o loop de categorias, que dirige a geração
    /// do próximo texto. Cunhar aqui duplicaria o mecanismo com o pior dos dois.
    /// </summary>
    [Theory]
    [InlineData(ErrorCategory.Preposition)]
    [InlineData(ErrorCategory.VerbTense)]
    [InlineData(ErrorCategory.Spelling)]
    [InlineData(ErrorCategory.Agreement)]
    public async Task Does_not_mint_grammar_or_mechanics_errors(ErrorCategory category)
    {
        var minted = await SeedAndMintAsync(
            "I was excited about the trip to the mountains.",
            (category, "excited with", "excited about", "animado com"));

        Assert.Equal(0, minted);
        await using var ctx = _db.NewContext();
        Assert.Empty(ctx.Cards);
    }

    [Fact]
    public async Task Does_not_mint_when_the_sentence_yields_no_usable_cloze()
    {
        var minted = await SeedAndMintAsync(
            "She is a hard worker and arrives early.",
            (ErrorCategory.WordChoice, "helpful person", "helpful worker", "trabalhadora"));

        Assert.Equal(0, minted);
        await using var ctx = _db.NewContext();
        Assert.Empty(ctx.Cards);
    }

    /// <summary>
    /// Sem trecho de origem o card NÃO nasce. A lacuna sozinha não tem resposta
    /// única — "at the ___ center" aceita qualquer coisa —, então o card seria
    /// errado pra sempre, contando lapso e sujando a estatística do agendador.
    /// Mesmo critério do ClozeBuilder: card ruim é pior que card nenhum.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Does_not_mint_when_the_source_phrase_is_missing(string? sourcePhrase)
    {
        var minted = await SeedAndMintAsync(
            "Sleeping well is as important as exercising regularly and eating healthily.",
            (ErrorCategory.WordChoice, "doing regular exercises", "exercising regularly", sourcePhrase));

        Assert.Equal(0, minted);
        await using var ctx = _db.NewContext();
        Assert.Empty(ctx.Cards);
    }

    /// <summary>
    /// Reincidência sem dica nova: a reprogramação acontece (o sinal de que o item
    /// não entrou é real), mas o conteúdo NÃO é trocado — enunciado novo com dica
    /// velha apontaria pra uma frase que não é mais a da tela.
    /// </summary>
    [Fact]
    public async Task A_recurrence_without_a_hint_reschedules_but_keeps_the_old_content()
    {
        await SeedAndMintAsync(
            "I have a coffee at the bar every morning before work.",
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        await using (var seed = _db.NewContext())
        {
            var card = await seed.Cards.SingleAsync();
            card.State = CardState.Review;
            card.IntervalDays = 20;
            card.Reps = 3;
            await seed.SaveChangesAsync();
        }

        await SeedAndMintAsync(
            "She decided to have a coffee while she waited for the train.",
            (ErrorCategory.Collocation, "take a coffee", "have a coffee", null));

        await using var check = _db.NewContext();
        var single = await check.Cards.SingleAsync();

        Assert.Equal(1, single.Lapses);                                   // o sinal contou
        Assert.Equal(CardScheduler.FirstStepDays, single.IntervalDays);   // e reprogramou
        Assert.Equal("I ___ at the bar every morning before work.", single.Prompt); // par antigo intacto
        Assert.Equal("tomar um café", single.Hint);
        Assert.Equal("drink a coffee", single.YourAttempt);
    }

    /// <summary>
    /// O card COPIA o conteúdo em vez de referenciar o erro. Sem isso, apagar uma
    /// prática (que tem DELETE em cascata) mataria cards em revisão há meses.
    /// </summary>
    [Fact]
    public async Task A_card_survives_the_deletion_of_the_practice_that_created_it()
    {
        await SeedAndMintAsync(
            "Yesterday I spent the whole afternoon looking for my keys.",
            (ErrorCategory.WordChoice, "stayed the whole afternoon", "spent the whole afternoon", "passei a tarde toda"));

        await using (var ctx = _db.NewContext())
        {
            ctx.Exercises.RemoveRange(ctx.Exercises);
            await ctx.SaveChangesAsync();
        }

        await using var check = _db.NewContext();
        Assert.Empty(check.Errors);
        var card = await check.Cards.SingleAsync();
        Assert.Equal("spent the whole afternoon", card.Answer);
        Assert.Equal(Language.Portuguese, card.SourceLanguage);
        Assert.Equal(Language.English, card.TargetLanguage);
    }

    /// <summary>
    /// Errar o MESMO item de novo, escrevendo de verdade, é a evidência mais forte
    /// de que ele não entrou. Vira reprogramação do card existente — não um segundo
    /// card, que dividiria o histórico em dois e escondaria a reincidência.
    /// </summary>
    [Fact]
    public async Task A_recurring_error_reschedules_the_existing_card_instead_of_duplicating()
    {
        await SeedAndMintAsync(
            "I have a coffee at the bar every morning before work.",
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        // Gradua o card, pra a reincidência ter de onde cair.
        await using (var ctx = _db.NewContext())
        {
            var card = await ctx.Cards.SingleAsync();
            card.State = CardState.Review;
            card.IntervalDays = 40;
            card.Reps = 4;
            await ctx.SaveChangesAsync();
        }

        await SeedAndMintAsync(
            "She decided to have a coffee while she waited for the train.",
            (ErrorCategory.Collocation, "take a coffee", "have a coffee", "tomar um café"));

        await using var check = _db.NewContext();
        var single = await check.Cards.SingleAsync();

        Assert.Equal(CardScheduler.FirstStepDays, single.IntervalDays);
        Assert.Equal(CardState.Learning, single.State);
        Assert.Equal(1, single.Lapses);
        // Conteúdo atualizado pro fracasso mais recente: mesmo item, contexto fresco.
        Assert.Equal("She decided to ___ while she waited for the train.", single.Prompt);
        Assert.Equal("take a coffee", single.YourAttempt);
    }

    /// <summary>
    /// A reincidência NÃO gera linha no log. O log responde "voltando depois de N
    /// dias, qual a taxa de acerto?" — um fracasso na escrita, sem o card ter sido
    /// mostrado, não tem intervalo pra atribuir e corromperia essa estatística.
    /// </summary>
    [Fact]
    public async Task A_recurring_error_does_not_write_a_review_row()
    {
        await SeedAndMintAsync(
            "I have a coffee at the bar every morning before work.",
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));
        await SeedAndMintAsync(
            "She decided to have a coffee while she waited for the train.",
            (ErrorCategory.Collocation, "take a coffee", "have a coffee", "tomar um café"));

        await using var ctx = _db.NewContext();
        Assert.Empty(ctx.CardReviews);
    }

    /// <summary>
    /// Card descartado não ressuscita. Se errar de novo o trouxesse de volta, o
    /// descarte não significaria nada — e cards ruins (erro de digitação
    /// classificado como vocabulário) voltariam pra sempre.
    /// </summary>
    [Fact]
    public async Task A_discarded_card_is_not_resurrected_by_a_new_error()
    {
        await SeedAndMintAsync(
            "I have a coffee at the bar every morning before work.",
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        var service = Service(out var ctx);
        await using (ctx)
        {
            var id = (await ctx.Cards.SingleAsync()).Id;
            Assert.Equal(CardOutcome.Ok, await service.DiscardAsync(id));
        }

        await SeedAndMintAsync(
            "She decided to have a coffee while she waited for the train.",
            (ErrorCategory.Collocation, "take a coffee", "have a coffee", "tomar um café"));

        await using var check = _db.NewContext();
        var card = await check.Cards.SingleAsync();
        Assert.Equal(CardState.Discarded, card.State);
        Assert.Equal(0, card.Lapses);
    }

    [Fact]
    public async Task Checking_an_answer_reveals_it_without_scheduling_anything()
    {
        await SeedAndMintAsync(
            "I have a coffee at the bar every morning before work.",
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        var service = Service(out var ctx);
        await using (ctx)
        {
            var id = (await ctx.Cards.SingleAsync()).Id;
            var (outcome, result) = await service.CheckAsync(id, "drink a coffee");

            Assert.Equal(CardOutcome.Ok, outcome);
            Assert.Equal(CardVerdict.Wrong, result!.Verdict);
            Assert.Equal("have a coffee", result.Answer);
            Assert.Equal("drink a coffee", result.YourAttempt);
        }

        await using var check = _db.NewContext();
        var card = await check.Cards.SingleAsync();
        Assert.Equal(CardState.New, card.State);
        Assert.Equal(0, card.Reps);
        Assert.Empty(check.CardReviews);
    }

    [Fact]
    public async Task Reviewing_schedules_the_card_and_logs_the_interval_on_both_sides()
    {
        await SeedAndMintAsync(
            "I have a coffee at the bar every morning before work.",
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        var service = Service(out var ctx);
        await using (ctx)
        {
            var id = (await ctx.Cards.SingleAsync()).Id;
            var (outcome, result) = await service.ReviewAsync(
                id, new CardReviewRequest("have a coffee", WasCorrect: true, CardRating.Easy));

            Assert.Equal(CardOutcome.Ok, outcome);
            Assert.Equal(CardScheduler.FirstStepDays, result!.IntervalDays);
        }

        await using var check = _db.NewContext();
        var log = await check.CardReviews.SingleAsync();
        Assert.Equal(0, log.IntervalBefore);
        Assert.Equal(CardScheduler.FirstStepDays, log.IntervalAfter);
        Assert.True(log.WasCorrect);
        Assert.Equal(CardRating.Easy, log.Rating);
    }

    /// <summary>
    /// Fácil/difícil só existe sobre acerto. Aceitar "Easy" num erro deixaria o
    /// ease subir errado — e o ease é o que governa o crescimento do intervalo.
    /// </summary>
    [Fact]
    public async Task A_wrong_answer_is_always_logged_as_Again_whatever_the_client_sent()
    {
        await SeedAndMintAsync(
            "I have a coffee at the bar every morning before work.",
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        var service = Service(out var ctx);
        await using (ctx)
        {
            var id = (await ctx.Cards.SingleAsync()).Id;
            await service.ReviewAsync(id, new CardReviewRequest("nope", WasCorrect: false, CardRating.Easy));
        }

        await using var check = _db.NewContext();
        Assert.Equal(CardRating.Again, (await check.CardReviews.SingleAsync()).Rating);
        Assert.Equal(CardScheduler.StartingEase - CardScheduler.LapsePenalty,
            (await check.Cards.SingleAsync()).Ease, 6);
    }

    [Fact]
    public async Task The_session_queue_skips_cards_that_are_not_due_retired_or_discarded()
    {
        await SeedAndMintAsync(
            "I have a coffee at the bar every morning before work. She spent the whole afternoon reading a book. "
            + "The old buildings have colorful façades and clay roofs. He is mastering grammar with real effort.",
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"),
            (ErrorCategory.WordChoice, "stayed the whole afternoon", "spent the whole afternoon", "passou a tarde toda"),
            (ErrorCategory.WordChoice, "color façades", "colorful façades", "fachadas coloridas"),
            (ErrorCategory.WordChoice, "dominating", "mastering", "dominando"));

        await using (var ctx = _db.NewContext())
        {
            var cards = await ctx.Cards.OrderBy(c => c.Id).ToListAsync();
            Assert.Equal(4, cards.Count);

            cards[1].DueAt = DateTimeOffset.UtcNow.AddDays(3); // ainda não venceu
            cards[2].State = CardState.Retired;
            cards[3].State = CardState.Discarded;
            await ctx.SaveChangesAsync();
        }

        var service = Service(out var ctx2);
        await using (ctx2)
        {
            var due = await service.GetDueAsync();
            Assert.Equal("have a coffee", (await ctx2.Cards.FindAsync(due.Single().Id))!.Answer);
        }
    }

    /// <summary>
    /// Caso real do backfill: uma frase que acumulou três erros virou três cards, e
    /// a frente de cada um contém a resposta dos outros dois. Mostrados juntos, dois
    /// terços da sessão sairiam de graça.
    /// </summary>
    [Fact]
    public async Task Only_one_card_per_source_sentence_enters_a_session()
    {
        await SeedAndMintAsync(
            "Marina arrived at the train station earlier than expected, so she decided to have a coffee at the bar.",
            (ErrorCategory.LiteralTranslation, "early of expected", "earlier than expected", "mais cedo do que o previsto"),
            (ErrorCategory.WordChoice, "then she decided", "so she decided", "então decidiu"),
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        var service = Service(out var ctx);
        await using (ctx)
        {
            await using var check = _db.NewContext();
            Assert.Equal(3, await check.Cards.CountAsync());

            var due = await service.GetDueAsync();
            Assert.Single(due);
        }
    }

    /// <summary>
    /// Revisar um card TIRA a frase inteira do dia. Sem isto o irmão voltaria na
    /// consulta seguinte — trinta segundos depois de você ter lido a resposta dele
    /// no enunciado do card que acabou de responder.
    /// </summary>
    [Fact]
    public async Task Reviewing_a_card_takes_its_whole_sentence_out_of_the_day()
    {
        await SeedAndMintAsync(
            "Marina arrived at the train station earlier than expected, so she decided to have a coffee at the bar.",
            (ErrorCategory.LiteralTranslation, "early of expected", "earlier than expected", "mais cedo do que o previsto"),
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        var service = Service(out var ctx);
        await using (ctx)
        {
            var first = (await service.GetDueAsync()).Single();
            await service.ReviewAsync(
                first.Id, new CardReviewRequest("x", WasCorrect: true, CardRating.Easy));

            Assert.Empty(await service.GetDueAsync());
        }
    }

    /// <summary>
    /// Um card pode se APOSENTAR na revisão de hoje. Se o aposentado saísse da conta,
    /// o irmão dele deixaria de estar bloqueado e apareceria na mesma sessão — logo
    /// depois de a resposta dele ter sido lida no enunciado que acabou de ser
    /// respondido. Aposentado sai da fila, não do bloqueio do dia.
    /// </summary>
    [Fact]
    public async Task A_sibling_that_retired_today_still_blocks_the_sentence()
    {
        await SeedAndMintAsync(
            "Marina arrived at the train station earlier than expected, so she decided to have a coffee at the bar.",
            (ErrorCategory.LiteralTranslation, "early of expected", "earlier than expected", "mais cedo do que o previsto"),
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        // Os dois com as MESMAS revisões — senão o desempate "menos revisado
        // primeiro" escolheria o irmão novo, e o que aposenta nunca apareceria.
        // Com o empate, quem sai é o de Id menor: o que está a uma revisão de
        // aposentar.
        await using (var seed = _db.NewContext())
        {
            var cards = await seed.Cards.OrderBy(c => c.Id).ToListAsync();
            foreach (var c in cards)
            {
                c.State = CardState.Review;
                c.Reps = CardScheduler.RetirementReps;
                c.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            }
            cards[0].IntervalDays = CardScheduler.RetirementIntervalDays; // este aposenta
            cards[1].IntervalDays = 20;                                   // este não
            await seed.SaveChangesAsync();
        }

        var service = Service(out var ctx);
        await using (ctx)
        {
            var escolhido = Assert.Single(await service.GetDueAsync());
            await service.ReviewAsync(
                escolhido.Id, new CardReviewRequest("x", WasCorrect: true, CardRating.Easy));

            await using (var check = _db.NewContext())
                Assert.Equal(CardState.Retired, (await check.Cards.FindAsync(escolhido.Id))!.State);

            // Aposentou, mas a frase continua fora do dia — o irmão não sobe.
            Assert.Empty(await service.GetDueAsync());
        }
    }

    /// <summary>
    /// E no dia seguinte o irmão assume — senão "um por frase por dia" viraria
    /// "um por frase, pra sempre", e metade do deck nunca seria estudada.
    /// </summary>
    [Fact]
    public async Task The_sibling_takes_over_on_the_next_day()
    {
        await SeedAndMintAsync(
            "Marina arrived at the train station earlier than expected, so she decided to have a coffee at the bar.",
            (ErrorCategory.LiteralTranslation, "early of expected", "earlier than expected", "mais cedo do que o previsto"),
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        int revisado;
        var service = Service(out var ctx);
        await using (ctx)
        {
            revisado = (await service.GetDueAsync()).Single().Id;
            await service.ReviewAsync(revisado, new CardReviewRequest("x", WasCorrect: true, CardRating.Easy));
        }

        // Empurra a revisão (e o vencimento) pra ontem: é o amanhã chegando.
        await using (var seed = _db.NewContext())
        {
            var card = await seed.Cards.FindAsync(revisado);
            card!.LastReviewedAt = DateTimeOffset.UtcNow.AddDays(-1);
            card.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await seed.SaveChangesAsync();
        }

        var amanha = Service(out var ctx2);
        await using (ctx2)
        {
            // Os dois estão vencidos, mas só um sai — e é o que ainda não foi visto.
            var proximo = Assert.Single(await amanha.GetDueAsync());
            Assert.NotEqual(revisado, proximo.Id);
        }
    }

    /// <summary>
    /// O contador do deck e a sessão contam a MESMA coisa. Divergir faria o menu
    /// prometer um número que a sessão não entrega.
    /// </summary>
    [Fact]
    public async Task The_deck_counter_matches_the_session_queue()
    {
        await SeedAndMintAsync(
            "Marina arrived at the train station earlier than expected, so she decided to have a coffee at the bar.",
            (ErrorCategory.LiteralTranslation, "early of expected", "earlier than expected", "mais cedo do que o previsto"),
            (ErrorCategory.WordChoice, "then she decided", "so she decided", "então decidiu"),
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        var service = Service(out var ctx);
        await using (ctx)
        {
            var deck = await service.GetDeckAsync();
            Assert.Equal(3, deck.Summary.Total);
            Assert.Equal((await service.GetDueAsync()).Count, deck.Summary.Due);
        }
    }

    /// <summary>
    /// O cenário de banco zerado: UMA prática, 3 cards. A regra de enterrar é por
    /// FRASE, não por prática — dois erros na mesma frase são irmãos, um erro numa
    /// outra frase do mesmo texto não é. Então a sessão 1 entrega 2 cards (um de
    /// cada frase) e a sessão 2 entrega o irmão que ficou.
    /// </summary>
    [Fact]
    public async Task One_practice_with_two_sentences_splits_across_sessions()
    {
        await SeedAndMintAsync(
            "Yesterday I spent the whole afternoon looking for my keys. "
            + "Later we went back home and prepared a wonderful meal.",
            // frase 1 — dois erros, viram irmãos
            (ErrorCategory.WordChoice, "stayed the whole afternoon", "spent the whole afternoon", "passei a tarde toda"),
            (ErrorCategory.WordChoice, "my keys", "looking for my keys", "procurando minhas chaves"),
            // frase 2 — erro isolado, não é irmão de ninguém
            (ErrorCategory.LiteralTranslation, "turned back to home", "went back home", "voltamos para casa"));

        var service = Service(out var ctx);
        await using (ctx)
        {
            await using (var check = _db.NewContext())
                Assert.Equal(3, await check.Cards.CountAsync()); // 3 cards nascem

            // Hoje: um card por frase → 2 dos 3.
            var hoje = await service.GetDueAsync();
            Assert.Equal(2, hoje.Count);

            // E são de frases diferentes: a da tarde e a do jantar.
            Assert.Single(hoje, c => c.Prompt.Contains("Yesterday"));
            Assert.Single(hoje, c => c.Prompt.Contains("Later we"));

            foreach (var card in hoje)
                await service.ReviewAsync(card.Id, new CardReviewRequest("x", WasCorrect: true, CardRating.Easy));

            // O terceiro NÃO aparece hoje: seu irmão acabou de ser respondido, e o
            // enunciado dele já mostrou a resposta.
            Assert.Empty(await service.GetDueAsync());
        }
    }

    /// <summary>Frases diferentes não são irmãs — duas práticas, dois cards na mesma sessão.</summary>
    [Fact]
    public async Task Cards_from_different_sentences_all_enter_the_session()
    {
        await SeedAndMintAsync(
            "I have a coffee at the bar every morning before work.",
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));
        await SeedAndMintAsync(
            "Yesterday I spent the whole afternoon looking for my keys.",
            (ErrorCategory.WordChoice, "stayed the whole afternoon", "spent the whole afternoon", "passei a tarde toda"));

        var service = Service(out var ctx);
        await using (ctx) Assert.Equal(2, (await service.GetDueAsync()).Count);
    }

    /// <summary>
    /// A ordem é sorteada. Sem isso o desempate seria o Id — e como a cunhagem
    /// carimba DueAt no mesmo instante, a sessão começaria sempre pela mesma frase,
    /// ensinando a resposta pela posição em vez de pelo inglês.
    /// </summary>
    [Fact]
    public async Task The_session_order_is_shuffled()
    {
        // Oito frases independentes: nenhuma é irmã de outra, então todas entram.
        for (var i = 0; i < 8; i++)
        {
            await SeedAndMintAsync(
                $"The visitor decided to word{i}number at the old museum entrance.",
                (ErrorCategory.WordChoice, $"wrong{i}", $"word{i}number", $"dica {i}"));
        }

        var service = Service(out var ctx);
        await using (ctx)
        {
            var ordens = new List<string>();
            for (var tentativa = 0; tentativa < 10; tentativa++)
                ordens.Add(string.Join(",", (await service.GetDueAsync()).Select(c => c.Id)));

            Assert.Equal(8, ordens[0].Split(',').Length);      // ninguém sumiu
            Assert.True(ordens.Distinct().Count() > 1,          // e a ordem varia
                "a fila saiu na mesma ordem 10 vezes seguidas");
        }
    }

    /// <summary>
    /// A conferência devolve os três intervalos possíveis, pra os botões dizerem o
    /// que fazem antes de serem clicados. Vêm do servidor porque é o MESMO agendador
    /// que vai agendar — recalcular no cliente abriria espaço pra os dois discordarem.
    /// </summary>
    [Fact]
    public async Task Checking_previews_the_three_possible_intervals()
    {
        await SeedAndMintAsync(
            "I have a coffee at the bar every morning before work.",
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        var service = Service(out var ctx);
        await using (ctx)
        {
            var id = (await ctx.Cards.SingleAsync()).Id;
            var (_, result) = await service.CheckAsync(id, "have a coffee");

            // Card novo: qualquer caminho cai no primeiro passo.
            Assert.Equal(CardScheduler.FirstStepDays, result!.AgainDays);
            Assert.Equal(CardScheduler.FirstStepDays, result.HardDays);
            Assert.Equal(CardScheduler.FirstStepDays, result.EasyDays);
        }

        // Já graduado, os três caminhos divergem — que é quando o preview importa.
        await using (var seed = _db.NewContext())
        {
            var card = await seed.Cards.SingleAsync();
            card.State = CardState.Review;
            card.IntervalDays = 10;
            card.Reps = 3;
            await seed.SaveChangesAsync();
        }

        var depois = Service(out var ctx2);
        await using (ctx2)
        {
            var id = (await ctx2.Cards.SingleAsync()).Id;
            var (_, result) = await depois.CheckAsync(id, "have a coffee");

            Assert.Equal(CardScheduler.FirstStepDays, result!.AgainDays);
            Assert.Equal(10 * CardScheduler.HardMultiplier, result.HardDays, 6);
            Assert.Equal(10 * (CardScheduler.StartingEase + CardScheduler.EasyBonus), result.EasyDays, 6);
            Assert.True(result.EasyDays > result.HardDays);
        }
    }

    /// <summary>Conferir continua sendo leitura: os previews não agendam nada.</summary>
    [Fact]
    public async Task Previewing_intervals_does_not_schedule_anything()
    {
        await SeedAndMintAsync(
            "I have a coffee at the bar every morning before work.",
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        var service = Service(out var ctx);
        await using (ctx)
        {
            var id = (await ctx.Cards.SingleAsync()).Id;
            await service.CheckAsync(id, "qualquer coisa");
        }

        await using var check = _db.NewContext();
        var card = await check.Cards.SingleAsync();
        Assert.Equal(0, card.IntervalDays);
        Assert.Equal(CardState.New, card.State);
        Assert.Null(card.LastReviewedAt);
    }

    /// <summary>A resposta NÃO viaja na fila da sessão — só no /check, depois de digitar.</summary>
    [Fact]
    public async Task The_session_queue_never_carries_the_answer()
    {
        await SeedAndMintAsync(
            "I have a coffee at the bar every morning before work.",
            (ErrorCategory.Collocation, "drink a coffee", "have a coffee", "tomar um café"));

        var service = Service(out var ctx);
        await using (ctx)
        {
            var item = (await service.GetDueAsync()).Single();

            Assert.DoesNotContain("have a coffee", System.Text.Json.JsonSerializer.Serialize(item));
            Assert.Equal("tomar um café", item.Hint); // a dica, essa sim, vai
        }
    }

    public void Dispose() => _db.Dispose();
}
