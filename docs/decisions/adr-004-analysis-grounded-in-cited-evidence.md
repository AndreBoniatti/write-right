# ADR 004: Weakness analysis grounded in cited evidence

**Status:** Accepted · **Date:** 2026-08-01

## Context

The profile says **what** you get wrong ("Preposition ×12"). It can't say that 8 of those 12 are one rule you never learned, or that three categories are really one habit — the closed taxonomy that makes the data comparable ([ADR 002](adr-002-closed-taxonomy-and-structured-output.md)) is also what makes it blind to the pattern inside a bucket.

A stronger model can find that structure. The risk is the opposite failure: asked to "analyse your writing", a model produces fluent, generic advice — "practise prepositions", "your sentences are short" — that sounds like a diagnosis and points at nothing. For a study tool that's worse than no analysis, because it's believed.

## Decision

**No claim without evidence, checked by code.**
- The model receives the user's real error rows, each with an id, and must cite ids for every pattern it reports.
- The server checks every cited id against the set it actually sent and drops the ones that don't match. A pattern left with fewer than 3 valid citations is discarded; if none survive, nothing is saved (the call is still recorded as spend).
- A pattern's categories are **derived** from its evidence, never asked of the model — one less surface for it to get wrong.
- Each pattern renders with the exact errors that produced it, then the cause, then the rule to study.

**Error rows only — never the texts.** Not to save tokens (that's marginal) but because, with the full text in context, the model starts asserting things no row can back. The diet enforces the first rule structurally: it can't claim what it can't cite. The accepted blind spot — anything visible only in whole texts — is stated on the page.

**A window sized by error volume, not by practice count.** Counting is honest with a small sample; detecting a pattern isn't — it needs several occurrences of the same sub-rule. The window walks back from the most recent practice until it has 180 errors, then keeps the 6 heaviest categories; a category with two errors supports no pattern and only spends context. Below 5 completed practices or 15 errors, the button stays off and says why.

**Persisted, not regenerated.** An analysis is saved with a snapshot of its evidence (copied text, not foreign keys — it survives the practice it cited being deleted) and a watermark of what it read — the size of its window, from which the same window can be rebuilt. It's generated in the background on request, so the diagnosis stays stable between visits instead of rewording itself on every page load.

**Staleness is measured, without AI.** A saved analysis ages silently: in my own history, one built from 19 practices was still on screen 36 practices later, while the categories it covered had fallen from 41% to 22% of my errors and what was growing appeared nowhere. The page now compares the share of errors in the analysis's categories inside its window against the practices since, straight from the database — and only once there is at least as much new material as it read, because smaller samples swing by noise.

## Alternatives considered

- **Send the whole practices.** More context, and the generic claims came back with it.
- **Ask the model for the categories of each pattern.** A second output to validate, and one that can contradict the evidence it cites.
- **A window of the last N practices.** Fixed N is either too small to find patterns early on or too large once the user improves and errors get sparse.
- **Regenerate on every visit.** Costs a call per page view, and a diagnosis that changes wording each time reads as unreliable even when it isn't.

## Consequences

### Positive
- Every sentence on the analysis page can be traced to errors the user actually made; a hallucinated pattern can't render, because it has nothing to render with.
- The grounding rules are deterministic code, so they're unit-tested: unknown ids, patterns under the floor, the empty result.
- Cost is bounded by the window, and the analysis runs only when asked.

### Negative
- Patterns only visible across whole texts (register, sentence variety, cohesion) are out of reach by design.
- The evidence floor drops real but thin patterns; a one-off can't become advice, even when it's right.
- A persisted analysis can go stale, which needed its own measurement and UI to surface.

## Revisit when

The study rules become the main thing users come for — that would justify sending richer context for the rule while keeping the citation check for the diagnosis.
