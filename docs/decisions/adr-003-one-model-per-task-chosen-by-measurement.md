# ADR 003: One model per task, chosen by measurement

**Status:** Accepted · **Date:** 2026-07-22 · **Updated:** 2026-09-24 (correction effort), 2026-10-08 (card model)

## Context

The app makes four kinds of AI call, and they differ in what a mistake costs:

| Task | What a bad answer costs |
|---|---|
| Generate a text to translate | Almost nothing — a dull text, discarded after one practice |
| Correct the translation | A wrong lesson, and a wrong row in the history |
| Analyse the error history | Generic advice, which is the failure the analysis is designed against |
| Design a vocabulary card | A wrong card trained for months, and the credibility of every other card |

It's paid per token from pay-as-you-go credit, so cost is a design input, not an afterthought. And reasoning effort turned out to be a lever as strong as the model itself.

## Decision

Each task has its own configurable model (`Llm:GenerationModel`, `CorrectionModel`, `AnalysisModel`, `CardModel`), and effort is set **explicitly in code** for every call that accepts it:

| Task | Model | Effort |
|---|---|---|
| Generation | Claude Haiku 4.5 | none (the model doesn't take the parameter) |
| Correction | Claude Sonnet 5 | Medium |
| Analysis | Claude Sonnet 5 | Medium |
| Card design | Claude Opus 5.5 | High |

Effort is explicit even where it matches the default: a default is the provider's decision, and relying on it would let a change on Anthropic's side alter the app's cost and behaviour without a commit.

**Correction at Medium (2026-09-24).** Measured on 14 real corrections against High: about 37% cheaper and half the latency (14 s against 25 s), with the same coverage — 6.8 errors found per practice against 6.6. High spent the difference deliberating: on an 8-word text it used 3,432 output tokens to find 2 errors.

**Cards on Opus 5.5 at High (2026-10-08).** Measured on 24 practices against Sonnet 5 at High, both answering the same question ([ADR 005](adr-005-cards-designed-by-their-own-call.md)): Sonnet let 37% of the bad cards through, Opus 17%. Against my estimate the bigger model was also **cheaper** — it thought for about 900 output tokens per practice against about 3,000, for US$ 0.025 against 0.033 — and roughly six times faster (~10 s). High pays off here because the core question, "is there another equally correct answer?", is a judgement against itself.

## Alternatives considered

- **One model for everything.** Sonnet everywhere overpays for generation and underperforms on cards; Opus everywhere multiplies the cost of the most frequent calls for no visible gain.
- **Provider-agnostic, bring-your-own-key.** An abstraction for users and providers that don't exist. The model sits behind an `ILlmProvider` interface — enough to stub it in tests — and the model names are configuration, which is all the flexibility one user needs.
- **Leaving effort at the provider's default.** Cheapest to write, and the one lever on cost that could change without anyone noticing.

## Consequences

### Positive
- Spend goes where a mistake is expensive. The card step adds about US$ 0.025 per practice; a practice without errors makes no card call at all.
- Every call records its model, tokens and duration, so a change of model or effort shows up in `/api/usage` as a number, not a feeling.
- Swapping a model is a configuration change; the startup validator refuses to run if the new model has no price configured.

### Negative
- Four models to keep priced and to re-evaluate as new snapshots ship.
- The evidence behind each choice is small: one run per arm, on my own data. Good enough to choose, not to publish.
- Medium effort files more grammar under vocabulary categories ([ADR 002](adr-002-closed-taxonomy-and-structured-output.md)). That stopped mattering for the deck once the category no longer decided cards; it remains as noise in the profile.
- Calls are not streamed, so output is capped at 16,000 tokens to stay inside the HTTP timeout. A timeout is the worst outcome — the call is billed and the answer lost — so needing more is a reason to switch to streaming, not to raise the cap.

## Revisit when

A new model snapshot or price change lands, or the usage report shows a task's cost or latency moving. The rule for revisiting is the same as for deciding: measure on real practices before switching.
