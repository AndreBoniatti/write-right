# Documentation

How WriteRight is built and why. The [project README](../README.md) covers what the app does and how to run it.

## Architecture haiku

[`architecture-haiku.md`](architecture-haiku.md) — the system on one page: objective, requirements, constraints, quality attributes in priority order, and the decisions that follow from them. The format is George Fairbanks' *architecture haiku*.

## Architectural decision records

[`decisions/`](decisions/) holds the decisions that shaped the system: each one with its context, what was decided, what was rejected, what it costs, and when to revisit it. Only decisions with a real alternative and a real cost are recorded here; smaller ones are explained in the project README or in the code where they apply.

| ADR | Decision | Status | Date |
|---|---|---|---|
| [001](decisions/adr-001-dotnet-and-blazor-with-shared-contracts.md) | .NET and Blazor WebAssembly with shared C# contracts | Accepted | 2026-07-22 |
| [002](decisions/adr-002-closed-taxonomy-and-structured-output.md) | A closed error taxonomy, enforced through structured output | Accepted | 2026-07-22 |
| [003](decisions/adr-003-one-model-per-task-chosen-by-measurement.md) | One model per task, chosen by measurement | Accepted | 2026-07-22 |
| [004](decisions/adr-004-analysis-grounded-in-cited-evidence.md) | Weakness analysis grounded in cited evidence | Accepted | 2026-08-01 |
| [005](decisions/adr-005-cards-designed-by-their-own-call.md) | Vocabulary cards decided by their own model call and checked by code | Accepted | 2026-10-08 |
| [006](decisions/adr-006-soft-delete-and-restrict-foreign-keys.md) | Soft delete for practices, and RESTRICT on every foreign key | Accepted | 2026-10-15 |
| [007](decisions/adr-007-remove-profile-targeted-generation.md) | Remove generation aimed at the profile | Accepted | 2026-10-22 |

**These records were written after the fact**, on 2026-11-03, from the commit history and the measurements taken when each decision was made. The date is when the decision landed on `main`. Where a decision replaced an earlier one — targeted generation, cards chosen by category — the earlier state is told inside the record that replaced it. The alternatives in each record are mostly the ones weighed at the time; a few were assessed while writing it, against the same context.

New decisions get the next number. A record that stops being true isn't edited away: its status changes to *Superseded by ADR NNN*.

## API reference

[`api.md`](api.md) — every route, what it's for, and how errors come back. The OpenAPI document generated from the code is the source of truth; this page adds the intent a schema can't carry.

## Screenshots

[`screenshots/`](screenshots/) — the images used in the project README.
