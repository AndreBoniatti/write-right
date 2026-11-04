# ADR 007: Remove generation aimed at the profile

**Status:** Accepted · **Date:** 2026-10-22

## Context

From the first commit, the core of WriteRight was an adaptive loop: *error → category → profile → targeted generation*. With "Focus on my weaknesses" ticked — the default — the next text to translate was written so that translating it would force the user's three heaviest categories to come up again. It moved server-side on 2026-07-23 (the client no longer needed to know the profile) and switched to severity-weighted categories on 2026-07-27. It was the feature the project was pitched on.

Three months of daily use said otherwise:

- **I kept switching it off.** The input token counts of the last 40 generation calls show the focus block present at most once.
- **Its targets were mostly unreachable.** After 57 practices my heaviest category was `WordChoice`: 67 errors with 66 different corrections. There is no rule to drill — each error is its own word. The second heaviest, `Spelling`, has the same problem. Two of the three slots went to categories a generated text can't aim at.
- **It aimed with too little.** The generator only ever received each category's one-line description, never the words I'd actually got wrong.

## Decision

Remove profile-targeted generation completely: the checkbox, the request field, the server code that resolved the top categories, and the prompt block. The new-practice screen generates by level, length, direction and theme only. The profile tile that offered "practise now" shows the heaviest category instead.

What a category couldn't reach is covered by the parts that work with real items: the vocabulary deck turns the actual words back into flashcards ([ADR 005](adr-005-cards-designed-by-their-own-call.md)), and the analysis explains the patterns behind the categories ([ADR 004](adr-004-analysis-grounded-in-cited-evidence.md)).

## Alternatives considered

- **Keep it, off by default.** Code, tests and a prompt branch maintained for a feature nobody used — and a checkbox implying an adaptivity the app doesn't deliver.
- **Send the real errors to the generator instead of the category description.** The better idea, but a different feature: "practise this pattern" starting from a grounded analysis, with its items as input. Not built; not a reason to keep the old path alive.
- **Aim only at grammar categories**, where a rule generalises. Still unused in practice, and it leaves the heaviest categories untouched.

## Consequences

### Positive
- The pitch matches the product: an error memory read by three views, rather than an adaptive loop that wasn't adapting anything.
- Less code on the hottest path (every practice starts with a generation), and a simpler new-practice screen.
- The decision is on record, so a future "let's make generation adaptive" starts from the data instead of from the original idea.

### Negative
- The most visible "AI that adapts to you" feature is gone — a weaker one-line pitch for a stronger product.
- Generation now ignores the history entirely; if targeting comes back, it comes back as new work.

## Revisit when

There is a way to aim at **items** rather than categories — most likely from the analysis, sending a pattern's real errors to the generator — and a measurement that the user would actually use it.
