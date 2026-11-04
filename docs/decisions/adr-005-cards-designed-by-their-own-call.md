# ADR 005: Vocabulary cards decided by their own model call and checked by code

**Status:** Accepted · **Date:** 2026-10-08

## Context

The deck exists because categories can't help with vocabulary. Of 188 errors across 36 practices, 82 (44%) were vocabulary, with 82 distinct corrections — no item repeated on its own. A rule generalises; a word doesn't. So vocabulary mistakes come back as flashcards: the corrected sentence with the answer blanked out, the source-language phrase as a hint, and what you actually wrote on the back.

The first version (2026-08-18) let the **correction's category decide**: any error filed under a vocabulary category became a card. Two months of use showed the damage is asymmetric — one wrong card doesn't cost 1/N of the deck's credibility, it costs nearly all of it, because from then on every card is suspect. Measured on the real deck (24 practices, 76 cards, labelled before anything was run), **32% of the cards were bad**:

- grammar filed as vocabulary ("the schedule → my schedule");
- a valid alternative punished ("unique aspects" for "unique characteristics");
- a sentence fragment instead of an item ("brings the student closer to").

And the reverse: "wait *for*" was filed as `Preposition` and never became a card. A label built for the profile was answering a question it was never asked.

## Decision

**What becomes a card is its own decision**, made by a separate call after the correction, on the errors already saved.

- The model looks at each error and returns a verdict first, then the card: answer, hint, and a list of **accepted alternatives** (lawyer / attorney). "No card" is a valid and expected answer. The schema puts the verdict before the answer, so the model decides *whether* before it designs *what*.
- It runs on Opus 5.5 at high effort ([ADR 003](adr-003-one-model-per-task-chosen-by-measurement.md)).

**The model proposes; code checks** (`CardGate`). Any mechanical doubt and the card isn't minted:

- the answer must appear verbatim, as whole words, in the corrected text, because the blank is cut by string matching;
- there is no card without a hint: the blank alone rarely has a unique answer ("at the ___ center" accepts anything), so a hintless card would be wrong forever and corrupt the review statistics. It's enforced three times — the gate rejects it, the field is non-nullable, and the column is `NOT NULL` with a `CHECK` against the empty string;
- the hint must appear verbatim, as whole words, in the source text — otherwise it was invented and may not point at this blank;
- at most 3 words in the answer (beyond that it's a fragment, memorised as a string) and at most 4 alternatives (a long list means the blank has no defined answer);
- neither the answer nor any alternative may already be in what you wrote — if one is, you knew the word and the mistake was somewhere around it;
- once the blank is cut, the sentence can't still show the answer elsewhere.

**It runs off the critical path.** The correction marks the practice *cards pending* in its own transaction and returns; a background worker drains pending practices on startup and whenever a correction lands. The database is the queue, so a restart loses nothing; after three failed attempts the practice is marked failed, the deck page says so, and a retry is a deliberate action.

## Evaluation

Same 24 practices, labels fixed before any run, two criteria fixed in advance: catch ≥ 80% of the bad cards and keep ≥ 90% of the good ones.

| Variant | Bad cards caught | Good cards kept |
|---|---|---|
| *Baseline: the category decides (the deck as it was)* | *0%* | *100%* |
| Sonnet 5, high effort | 63% | 93% |
| Opus 5.5, high effort, no alternatives | 100% | 70% |
| Opus 5.5, high effort, with alternatives | 83% | 93% |
| **The same, plus the code gates (what shipped)** | **92%** | **90%** |

Without alternatives the stricter model refused anything that has a synonym. The two numeric gates were validated for free, by replaying the output already paid for instead of calling the model again: each dropped one bad card the model let through, at the cost of one good one — the last row.

Caveats, stated because they're real: one run per variant; the winning prompt was tuned on the same set it was measured on; and the labels were assigned by Claude at my request rather than by me.

## Alternatives considered

- **Keep the category as the gate and filter its output** — e.g. a guard discounting function words before minting. Written, tested and discarded: it patches a decision made by the wrong component.
- **Single-word cards only.** Simple to check, but three of the five vocabulary categories are multi-word by definition, and collocations ("excited *about*") are among the most common errors of a Portuguese speaker writing in English.
- **Ask for the card inside the correction call.** One call instead of two, but the correction's job is to correct; mixing the decisions brings back the coupling this ADR removes, and makes the correction slower for something the user doesn't wait for.

## Consequences

### Positive
- The deck can be trusted again; a wrong card is now the exception the discard button is for, not the norm.
- Items filed under grammar can become cards ("wait for"), and grammar filed under vocabulary no longer does.
- The gate is deterministic and unit-tested, each rule named after a real bad card.

### Negative
- An extra call per practice with errors (about US$ 0.025).
- Cards arrive a few seconds after the correction, which needed a pending state in the UI and a badge that follows it.
- The gate trades recall for precision on purpose: some good cards are never minted. "Better to skip one than to mint a bad one."
- The old deck was deleted by the migration that introduced this step, and practices from before it don't get cards.

## Revisit when

The share of discarded cards rises, or a new model changes the cost/accuracy balance — measured against a labelled set, the same way.
