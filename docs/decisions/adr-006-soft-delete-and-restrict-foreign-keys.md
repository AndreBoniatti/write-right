# ADR 006: Soft delete for practices, and RESTRICT on every foreign key

**Status:** Accepted · **Date:** 2026-10-15

## Context

Deleting a practice used to be a physical `DELETE` that cascaded to its errors. That looked harmless until other features started reading the same rows for different reasons:

- **The day streak broke.** Deleting a practice erased the fact that you practised that day.
- **Spend disappeared.** The cost report lost money that had really been spent, which is why the usage table held weak, unenforced references instead of foreign keys — the practice could vanish under it.
- **Months of history were one cascade away.** Vocabulary cards copied their content partly as protection, because a cascade from the practice would have taken their review log with it.

A practice the user deletes still carries facts: when they practised and what it cost. Deleting the row deletes the facts, and every feature that needs them ends up working around the delete.

## Decision

- **Practices are soft-deleted**: a `DeletedAt` column and a global EF Core query filter, so a deleted practice drops out of every query without anyone remembering to filter. Its errors follow it through a filter on the parent, so they leave the profile, the category review and the next analysis's window.
- **The filter has a name** (`SoftDelete`). The few reads that must see deleted practices — the day streak and the average cost — switch off that filter only, never all filters, so a future filter (per user, for instance) can't be disabled by accident.
- **Every foreign key is `RESTRICT`**: errors → practice, card reviews → card, AI usage → practice and analysis. A stray physical delete fails in the database instead of cascading.
- Other user data follows the same rule: a discarded card is marked, never deleted; a saved analysis keeps a snapshot of its evidence.

## Alternatives considered

- **Keep physical delete and copy the facts elsewhere** (a separate table of practised days, spend detached from practices). Every new feature that needs a fact would need its own copy.
- **An archive table** that deleted rows move to. Same protection, with two schemas to keep in sync and joins across them for the streak and the cost report.
- **Soft delete with `CASCADE` left in place.** Works until the first raw `DELETE` — in a migration, a script, a manual fix — and then fails silently, which is exactly when it matters.

## Consequences

### Positive
- What happened stays true: the streak and the cost report are correct after a deletion.
- The usage table now has real foreign keys, enforced by the database.
- The guarantee lives in the schema, not in discipline: a physical delete is refused, and a test proves it.

### Negative
- Every new query depends on the global filter being right, and any read of facts must remember to switch it off by name.
- Deleted data is never really gone. For one user that's a feature; for several it's a liability — a right-to-erasure request (LGPD/GDPR) would need a deliberate hard-delete path that this decision makes harder on purpose.
- Rows accumulate forever, which is negligible at this scale.

## Revisit when

The app has users other than its author: erasure then becomes a requirement, and it should be a separate, explicit operation — not a return to cascading deletes.
