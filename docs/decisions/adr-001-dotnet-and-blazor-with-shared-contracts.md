# ADR 001: .NET and Blazor WebAssembly with shared C# contracts

**Status:** Accepted · **Date:** 2026-07-22

## Context

WriteRight is a personal tool built by one backend developer whose main language is C#. It needs a browser UI and an API that calls Claude with structured output, and the data that crosses the wire is unusually central: the error taxonomy ([ADR 002](adr-002-closed-taxonomy-and-structured-output.md)) travels from the model's JSON into the database, out through the API, and into every screen. Any drift in that shape — a renamed category, an enum serialized as a number on one side — silently corrupts the history the whole app is built on.

The official Anthropic C# SDK supports structured output, so the backend language was not constrained by the AI integration.

## Decision

- **Backend:** ASP.NET Core minimal API on .NET 10, EF Core over SQLite.
- **Frontend:** Blazor WebAssembly, standalone — a static SPA talking to the API over HTTP, not hosted by it.
- **Contracts:** a `WriteRight.Shared` class library referenced by both. It holds the request/response records, the taxonomy enums and their labels, and the pure logic both sides need (error rate, streak, answer matching).

Both sides serialize enums as strings with the same options, so the database, the API and the client read the same values.

## Alternatives considered

- **A TypeScript SPA (React) over the same API.** The default choice and the bigger ecosystem. It means a second model of every contract in TypeScript, kept in sync by hand or regenerated from OpenAPI — a build step to maintain, and a class of bugs (a category added in C#, missing in TS) that only shows up at runtime.
- **Blazor hosted by the API.** One deployable instead of two, but it couples the UI's lifecycle to the API's for no gain at this size; standalone keeps the client a plain static site.

## Consequences

### Positive
- One contract, checked by the compiler on both sides. A taxonomy change that breaks a screen fails the build, not the user.
- Logic that belongs to both sides is written once and tested once (`ErrorRate`, `PracticeStreak`, `AnswerMatch` live in `Shared` and are covered by the same suite).
- The taxonomy's friendly labels and examples come from the same catalog the correction prompt is built from.

### Negative
- First Blazor project for the author: the component model, render cycle and JS interop were learned on the job.
- The WASM runtime is a real first download; the app needed a branded loading screen with a progress bar.
- A small UI ecosystem compared to React: the icons are inline SVG and the evolution chart is a hand-written component.
- Two origins in development (API on 5056, client on 5193), so CORS is a configuration the app depends on — and an error without the CORS header looks to the client like the API is down.
- SDK rough edges, such as a static web asset compression bug that only clears by deleting `obj`/`bin`.

## Revisit when

The UI needs something the Blazor ecosystem doesn't cover well (rich text editing, heavy charting), or the app gets a second client (mobile) that can't share C# — at which point the OpenAPI document the API already publishes becomes the contract.
