# API reference

The routes the client uses, and what each one is for. The source of truth is the OpenAPI document the API generates from the code: in development it's served at `/openapi/v1.json` and browsable at **http://localhost:5056/scalar**. This page adds what a schema can't say — why a route exists and what it deliberately doesn't do.

## Errors

The API's own errors come back as ProblemDetails (RFC 9457):

- `400` for a request refused before any work — same source and target language, a blank translation, a body or query that doesn't bind;
- `404` / `409` for a missing or read-only resource;
- `502` when the AI answered — and charged — but its response couldn't be used.

Anything unexpected is still a plain `500` (in development, with the exception page).

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/practices` | Create a practice (generates the text, stores it as in-progress) |
| `GET` | `/api/practices?skip=` | List practices for the home screen, a page at a time (in-progress first, then newest) |
| `GET` | `/api/practices/completions` | When each completed practice was finished — the day streak is computed from it on the client, which knows your time zone |
| `GET` | `/api/practices/{id}` | Full practice detail (resume or read) |
| `PUT` | `/api/practices/{id}/translation` | Save a draft translation without correcting |
| `POST` | `/api/practices/{id}/correct` | Correct the translation, store the errors, complete the practice |
| `DELETE` | `/api/practices/{id}` | Soft-delete a practice: it leaves the list, the profile and future analyses (a saved analysis keeps its evidence), but still counts for the day streak and the cost report |
| `GET`  | `/api/profile` | The aggregated weakness profile, plus one point per completed practice (words and errors by severity) for the evolution chart |
| `GET`  | `/api/profile/errors?category=` | Your real errors in one category (review panel — no AI call) |
| `GET`  | `/api/analysis` | The latest weakness analysis + whether a new one is worth generating, and how far the history has moved since it |
| `POST` | `/api/analysis` | Queue a new analysis — `202` right away (`409` if the history is too small); it runs in the background and is followed on `GET /api/analysis` |
| `GET`  | `/api/cards/due` | The review queue — **without** the answers |
| `POST` | `/api/cards/{id}/check` | Grade the typed answer and reveal (schedules nothing) |
| `POST` | `/api/cards/{id}/review` | Close the review: reschedule the card and log it |
| `GET`  | `/api/cards` | The whole deck (counters + cards) |
| `GET`  | `/api/cards/summary` | Just the counters — what the menu badge polls while cards are being made |
| `DELETE` | `/api/cards/{id}` | Discard a bad card (marks, never deletes) |
| `POST` | `/api/cards/retry` | Put practices whose cards failed back in the queue |
| `GET`  | `/api/usage` | Token/cost report per operation, and the average cost of a practice (no AI call) |
