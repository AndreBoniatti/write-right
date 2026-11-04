# ADR 002: A closed error taxonomy, enforced through structured output

**Status:** Accepted · **Date:** 2026-07-22

## Context

The point of WriteRight is an error *memory*: corrections that accumulate into something you can study. A correction in free text can be read once but not aggregated — "wrong preposition", "preposition error" and "should be *about*" are the same mistake to a person and three different strings to a query. If the model is free to label errors, the labels drift between calls, between model versions and between prompt edits, and the history stops being comparable with itself.

## Decision

- **A closed set of 19 categories**, in four groups plus an escape hatch: structure/grammar (10), vocabulary/meaning (4), mechanics (3), style (1), and `Other` — to be used only when nothing else fits; a high rate of `Other` means a category is missing.
- **Severity is a separate axis**, not part of the category: *breaks meaning* / *understandable* / *polish*, weighted 3 / 2 / 1 when ranking what to study. A wrong preposition can break the meaning of one sentence and be polish in another.
- **One primary category per error**; when two fit, the most specific wins.
- **The model is forced into the set by structured output.** The JSON schema sent with every correction lists the allowed values, and those values are generated from the C# enums (`Enum.GetNames<ErrorCategory>()`), not typed out a second time. The system prompt is built from the same `ErrorCatalog` the UI reads its labels from.
- **Stored as strings**, never as integers: the database stays readable by eye and survives a reordering of the enum.

## Alternatives considered

- **Let the model name the category.** Richer labels, no maintenance — and no aggregation, which is the product.
- **An open taxonomy that grows when a new kind of error appears.** Every new category splits the history: errors that would have landed in it are filed elsewhere before it existed, and the profile shows a trend that is really a relabelling.
- **Severity folded into the category** ("serious grammar", "minor grammar"). Doubles the set and mixes *what* with *how bad*, which are studied differently.
- **Validating free-form JSON after the fact.** Structured output makes the constraint part of the call instead of a retry loop around it.

## Consequences

### Positive
- The profile is comparable over time, and three different views — profile, analysis and deck — read the same store.
- The schema cannot drift from the taxonomy: it is derived from it, and tests assert that the catalog and the enum stay in sync and that the schema lists every value.
- A category added or renamed in code reaches the prompt, the schema, the database and the UI in one change.

### Negative
- **A category is blind to what's inside it.** "Preposition ×12" can't say that 8 of them are one rule. That gap is what the weakness analysis exists for ([ADR 004](adr-004-analysis-grounded-in-cited-evidence.md)).
- **The model still misclassifies**, inside the set. At medium effort it files more grammar as vocabulary ("the schedule → my schedule" as `WordChoice`). In the profile that's small noise in an aggregate; when the category decided what became a flashcard it was a real defect ([ADR 005](adr-005-cards-designed-by-their-own-call.md)).
- **Severity is unstable**: it diverges in roughly 30% of errors between runs on the same text, at any effort level. It's good enough to weight a ranking, not to judge a single error.
- A category label is the right unit for grammar and the wrong one for vocabulary — two of the heaviest categories in my own data (`WordChoice`, `Spelling`) contain almost no repeated item ([ADR 007](adr-007-remove-profile-targeted-generation.md)).

## Revisit when

The share of `Other` climbs, or a category starts absorbing errors that clearly belong to a pattern of their own. Adding a category is allowed — but only knowing that the history before it is filed differently.
