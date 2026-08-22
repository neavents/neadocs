# Building a UI on neadocs

Everything a front-end needs: what to call, what comes back, what it costs, what breaks, and what
the interface should do about it.

Two audiences, and they are genuinely different products:

- **The reader surface** — a search box and an article. Used by people who have a problem right now
  and will give you about four seconds. Read-only, one endpoint.
- **The admin surface** — collections, documents, locales, reindexing, retrieval quality. Used
  rarely, by someone deliberate, who needs to understand consequences before acting.

Design them separately. The reader surface should feel like nothing; the admin surface should feel
like a control room.

---

## Table of contents

1. [The five concepts](#1-the-five-concepts)
2. [Auth, tenancy and what the UI must never send](#2-auth-tenancy-and-what-the-ui-must-never-send)
3. [The error model](#3-the-error-model)
4. [Reader surface — search](#4-reader-surface--search)
5. [Reader surface — the article](#5-reader-surface--the-article)
6. [Admin — collections](#6-admin--collections)
7. [Admin — documents](#7-admin--documents)
8. [Admin — bulk upload](#8-admin--bulk-upload)
9. [Admin — translations and staleness](#9-admin--translations-and-staleness)
10. [Admin — revisions](#10-admin--revisions)
11. [Admin — reindex and jobs](#11-admin--reindex-and-jobs)
12. [Admin — retrieval quality](#12-admin--retrieval-quality)
13. [Admin — stats and provider health](#13-admin--stats-and-provider-health)
14. [Right-to-left and Turkish](#14-right-to-left-and-turkish)
15. [Performance and caching](#15-performance-and-caching)
16. [Rate limits](#16-rate-limits)
17. [Things the UI must never do](#17-things-the-ui-must-never-do)

---

## 1. The five concepts

| Concept | What it is | UI implication |
|---|---|---|
| **Collection** | A namespace. `neavents-help` is one. | Almost always exactly one. Do not build a collection switcher unless you genuinely have several. |
| **Document** | One article, in one locale. Identified by `externalKey` + `locale`. | `publishing-a-menu` in `tr` and in `en` are two documents sharing one key. The UI must always carry the locale. |
| **Revision** | An immutable snapshot of a document's content. | Every save that changes content creates one. Nothing is ever overwritten. |
| **Chunk** | A slice of a document, roughly a section. **Search returns chunks, not documents.** | A hit points at a *place inside* an article. Deep-link to it. |
| **Locale** | A BCP-47 tag: `tr`, `en`, `en-gb`. | Normalised server-side (`tr_TR` → `tr-tr`). Send whatever the user's UI language is. |

The single most important consequence: **a search result is a passage, not a page.** Its
`headingPath` tells you where in the article it came from. A UI that shows only article titles is
throwing away the most useful thing the engine produces.

---

## 2. Auth, tenancy and what the UI must never send

Base URL through the estate gateway: `/docs/v1/…` (maps to the engine's `/api/v1/…`).

Two credential mechanisms:

| Mechanism | Header | Use |
|---|---|---|
| Project key | `X-Project-Key: <key>` | Service-to-service. **Never ship this to a browser.** |
| Bearer JWT | `Authorization: Bearer <token>` | Carries a `tenant` claim and `scope` claims. |

Scopes are hierarchical: `docs:admin` ⊃ `docs:write` ⊃ `docs:read`.

**The tenant is resolved from the credential and from nothing else.** There is no endpoint that
accepts a tenant id — not as a route parameter, query string, header or body field. If your UI
finds itself wanting to "pass the org id", the design has gone wrong somewhere upstream.

**A browser must never hold a project key.** For the reader surface, put a thin proxy in your own
backend that holds the key and forwards the search call. The proxy is also where you enforce that
end users can only search, never write.

### Correlation

Send `X-Correlation-Id` on every request — any string of `[A-Za-z0-9-_.:]`, up to 128 characters.
It comes back on the response and appears in every log line and trace for that request. When a user
reports "search is broken", the correlation id is what turns that into a five-second lookup.

If you don't send one, the engine generates it from the trace id and returns it anyway. **Surface it
in your error UI** — a small monospace string under the message is enough.

---

## 3. The error model

Every failure is RFC 7807 `application/problem+json`:

```jsonc
{
  "type": "about:blank",
  "title": "Forbidden",
  "status": 403,
  "detail": "This credential holds [docs:read] and the route requires docs:write.",
  "correlationId": "4bf92f3577b34da6a3ce929d0e0e4736"
}
```

`detail` is written for a human and is safe to show verbatim in an admin UI. Do **not** show it to
end users on the reader surface — it can name internal settings.

| Status | Meaning | What the UI should do |
|---|---|---|
| `400` | Malformed request — bad locale, empty query, over-long query, bulk too large | Fix it client-side; these are all preventable with validation |
| `401` | No or invalid credential | Re-authenticate. Never retry blindly |
| `403` | Valid credential, insufficient scope | Hide the control rather than letting them hit this |
| `404` | Collection, document or job doesn't exist **for this tenant** | Note the qualifier — 404 also means "not yours" |
| `422` | Eval run completed but did not meet the quality floor | This is a *result*, not an error. Render the report |
| `429` | Rate limited | Back off; the detail names the limit and window |
| `503` | `/ready` only — migration incomplete or Postgres unreachable | Show a maintenance state, retry |

**`404` deliberately does not distinguish "absent" from "not yours".** Don't try to infer which.

---

## 4. Reader surface — search

```
POST /docs/v1/collections/{collection}/search      scope: docs:read
```

```jsonc
{
  "query": "menümü nasıl yayınlarım",
  "locale": "tr",
  "mode": "hybrid",
  "limit": 10,
  "minScore": 0,
  "filter": { "section": "menus" },
  "explain": false
}
```

| Field | Required | Notes |
|---|---|---|
| `query` | yes | 1–512 chars. Longer is `400` — truncate or block in the input |
| `locale` | no | Omit and you search everything. Almost always send the UI language |
| `mode` | no | `lexical` \| `vector` \| `hybrid`. Omit it and take the server default |
| `limit` | no | 1–100, default 10 |
| `minScore` | no | Leave at 0. RRF scores are small (~0.01–0.03) and not comparable across queries |
| `filter` | no | Exact-match on document `metadata`. `{"section":"menus"}` matches documents whose metadata contains that pair |
| `explain` | no | Adds per-strategy ranks. For an admin debug view, never the reader UI |

### Response

```jsonc
{
  "mode": "hybrid",
  "degraded": false,
  "tookMs": 12,
  "hits": [
    {
      "chunkId": "…", "documentId": "…",
      "externalKey": "publishing-a-menu",
      "locale": "tr",
      "title": "Menüyü yayınlama",
      "headingPath": ["Menüler", "Yayınlama"],
      "score": 0.0312,
      "ordinal": 2,
      "snippet": "…menüyü <em>yayınlamak</em> için…",
      "highlights": [{ "start": 41, "length": 11 }],
      "metadata": { "section": "menus" },
      "explain": null
    }
  ]
}
```

### How to render a hit

- **`title`** is the article. **`headingPath`** is the section within it. Render as
  `title` with the path as a breadcrumb beneath — **join it yourself**, and see
  [§14](#14-right-to-left-and-turkish) before you pick a separator.
- **`snippet`** already contains `<em>` around matches. If you render it as HTML, sanitise to allow
  *only* `<em>`. If you'd rather not, ignore `snippet` entirely and use `highlights`.
- **`highlights`** are `{start, length}` offsets into the chunk's raw text, in logical order. This
  is the safe path: slice the text yourself, no HTML parsing.
- **`ordinal`** is the chunk's position in the document. Use it to deep-link:
  `/help/{externalKey}?locale={locale}#chunk-{ordinal}`.
- **`score`** is for ordering only. **Never show it to a reader.** It is a Reciprocal Rank Fusion
  value with no meaning outside the result set it came from.

### `degraded` — the one flag you must not ignore

`degraded: true` means you asked for hybrid or vector and got **lexical only** — no embedding model
is configured, or every provider was unavailable.

Results are still good. Do not show an error. But an admin surface must show this plainly, because
"search feels worse today" and "the embedding vendor is down" are the same event, and nobody will
connect them without this flag.

### States the search UI must handle

| State | Trigger | Suggested treatment |
|---|---|---|
| **Idle** | No query yet | Show 4–6 popular articles. Never an empty box |
| **Typing** | < 3 chars | Do nothing. Do not call the API |
| **Loading** | Request in flight | Skeleton rows, not a spinner. Typical `tookMs` is 3–30 |
| **Results** | `hits.length > 0` | See above |
| **Empty** | `hits.length === 0` | **This is the highest-value state in the product.** See below |
| **Error** | 5xx, network | "Search is having trouble" + correlation id + a contact route |
| **Rate limited** | 429 | Silently back off; don't surface it — it means the user typed fast |

### The empty state is where you win or lose

A zero-result search is a user who has a problem your documentation does not answer, and who is
about to phone or email. Treat it as the most important screen in the feature:

1. Show the query back to them so they can see a typo.
2. Offer 3–5 popular articles as a fallback.
3. Give a contact route — and **pre-fill it with the failed query and the correlation id.**
4. Log the query. A list of searches that returned nothing is the single best backlog for whoever
   writes the documentation.

Point 4 costs almost nothing and is worth more than most features you could build instead.

### Debouncing

Debounce 250–300 ms and cancel in-flight requests. Do not search on every keystroke. Do search on
paste and on Enter immediately.

---

## 5. Reader surface — the article

```
GET /docs/v1/collections/{collection}/documents/{externalKey}?locale=tr    scope: docs:read
```

Returns the document with its **current content** as markdown in `content`:

```jsonc
{
  "id": "…", "externalKey": "publishing-a-menu", "locale": "tr",
  "title": "Menüyü yayınlama",
  "revision": 4,
  "contentHash": "…",
  "metadata": { "section": "menus" },
  "content": "# Menüyü yayınlama\n\n## Adımlar\n\n…",
  "chunkCount": 7,
  "createdAt": "…", "updatedAt": "…"
}
```

**`?locale` is effectively required.** If the key exists in several locales and you omit it, you get
`400` with a detail naming how many locales matched — not a guess. Always send it.

Render `content` as markdown. Build your own in-page table of contents from the headings; the engine
does not provide one. To honour a `#chunk-{ordinal}` deep link, you need the same chunk boundaries
the engine used — the pragmatic approach is to scroll to the heading matching the hit's
`headingPath`, which is stable and needs no chunking logic client-side.

---

## 6. Admin — collections

| Method | Route | Scope | Returns |
|---|---|---|---|
| `PUT` | `/collections/{key}` | `docs:admin` | `201` created, `200` updated |
| `GET` | `/collections` | `docs:read` | `{ items: [...] }` |
| `DELETE` | `/collections/{key}` | `docs:admin` | `204`, or `404` |

`GET /collections` returns `documentCount` per collection — enough for a list view without a second
call.

### `DELETE` is a hard delete and it cascades

This removes the collection, **every document in it, every revision, every chunk and every
embedding**, permanently. There is no soft delete at the collection level and no undo.

The UI must:

- Require typing the collection key to confirm — not an "Are you sure?"
- Show the live `documentCount` inside the confirmation
- Be reachable only from a settings screen, never from a list row's overflow menu

---

## 7. Admin — documents

```
PUT /docs/v1/collections/{key}/documents/{externalKey}?force=false   scope: docs:write
```

```jsonc
{
  "locale": "tr",
  "title": "Menüyü yayınlama",
  "content": "# Menüyü yayınlama\n\n…markdown…",
  "sourceUri": "docs/tr/publishing-a-menu.md",
  "metadata": { "section": "menus", "audience": "owner" },
  "sourceLocale": "tr",
  "sourceContentHash": "…"
}
```

### The response tells you what actually happened

```jsonc
{
  "documentId": "…", "externalKey": "publishing-a-menu", "locale": "tr",
  "revision": 4,
  "changed": true,
  "chunks": { "total": 7, "created": 2, "reused": 5, "deleted": 1 }
}
```

**`changed: false` means the content was byte-identical and nothing happened** — no new revision, no
re-chunking, no re-embedding. Saving twice is free and safe.

The UI should say so rather than lying with a green "Saved!":

- `changed: true` → *"Saved as revision 4. 2 sections reindexed, 5 unchanged."*
- `changed: false` → *"No changes to save."*

That `chunks` breakdown is genuinely useful to a content editor: it shows that editing one paragraph
reindexed one section, not the whole article. It's also how you'd notice a formatting change that
accidentally rewrote everything.

### `?force=true`

Re-ingests identical content: new revision, re-chunk, re-embed. Only reason to use it is after
changing chunking settings or normalisation rules. Put it behind an "Advanced" affordance, not a
checkbox next to Save.

### Validation, so the UI can prevent the round trip

| Rule | Failure |
|---|---|
| `locale` well-formed BCP-47 | `400` |
| `content` non-empty | `400` |
| `externalKey` 1–128 chars | `400` |
| Body ≤ 4 MiB | `413` |
| Collection exists | `404` |

`title` defaults to `externalKey` if omitted — always send a real one.

### Delete is soft

```
DELETE /docs/v1/collections/{key}/documents/{externalKey}?locale=tr    scope: docs:write
```

`204`. The document disappears from search and from listings; **rows are retained**. Re-upserting
the same key and locale revives it, keeping its revision history.

Say that in the confirmation — "can be restored by re-uploading" is materially different from
"gone", and it changes how carefully someone clicks.

**Omitting `?locale` deletes every locale of that key.** Make the locale explicit in the UI, and
show which translations are about to disappear.

---

## 8. Admin — bulk upload

```
POST /docs/v1/collections/{key}/documents:bulk    scope: docs:write
```

```jsonc
{ "documents": [ { "externalKey": "…", "locale": "tr", "title": "…", "content": "…" } ] }
```

Max 500 per request. **Partial success is normal** — the response is per item:

```jsonc
{
  "total": 3, "changed": 2,
  "results": [
    { "externalKey": "a", "locale": "tr", "status": 200, "changed": true,  "revision": 2 },
    { "externalKey": "b", "locale": "en", "status": 200, "changed": false, "revision": 1 },
    { "externalKey": "c", "locale": "",   "status": 400, "error": "'!!bad!!' is not a well-formed BCP-47 locale tag." }
  ]
}
```

The HTTP status is `200` even when items failed. **Never treat 2xx as "all good" here.** Render a
per-row table: changed / unchanged / failed, with the `error` string shown inline on failures.

If the whole collection is missing you get `404` for the entire request — that one is all-or-nothing.

---

## 9. Admin — translations and staleness

The engine never translates anything. It *reports* when a translation has fallen behind.

The contract: when you upload a translation, record where it came from.

1. Upload the source (`tr`), read back its `contentHash`.
2. Upload the translation (`en`) with `sourceLocale: "tr"` and `sourceContentHash: "<that hash>"`.

Then:

```
GET /docs/v1/collections/{key}/documents?staleAgainst=tr    scope: docs:read
```

returns only documents whose recorded source hash no longer matches the current `tr` content — i.e.
the Turkish moved on and the English didn't.

This is the backbone of a **Translations** screen: a matrix of `externalKey` × locale, with each
cell showing up to date / stale / missing. It's the difference between a documentation set you can
trust and one where nobody knows which languages are current.

A translation uploaded without `sourceContentHash` can never be reported stale. If your UI has a
translation workflow, always send it.

---

## 10. Admin — revisions

```
GET /docs/v1/collections/{key}/documents/{externalKey}/revisions?locale=tr    scope: docs:read
```

```jsonc
{ "items": [ { "revision": 4, "title": "…", "contentHash": "…", "length": 2841, "createdAt": "…" } ] }
```

Newest first. **Metadata only — no bodies**, so this is cheap and safe to load on an article screen.

There is no endpoint to fetch or restore an old revision's content. If you need rollback, keep the
markdown in your docs git repository and re-upload; the engine is an index, not a CMS. Say that in
the UI rather than implying a restore button exists.

`length` lets you show a size delta between revisions, which is the cheapest possible "what changed"
signal without diffing.

---

## 11. Admin — reindex and jobs

```
POST /docs/v1/collections/{key}/reindex?locale=tr    scope: docs:admin
```

Returns immediately:

```jsonc
{ "jobId": "…", "state": "queued" }      // 202 Accepted
```

Then poll:

```
GET /docs/v1/jobs/{jobId}    scope: docs:admin
```

```jsonc
{
  "id": "…", "kind": "reindex", "state": "running",
  "processed": 25, "total": 60,
  "errors": [],
  "createdAt": "…", "updatedAt": "…"
}
```

`state` is `queued` → `running` → `succeeded` | `failed`. Poll every 1–2 seconds; progress updates
every 25 documents, so faster polling shows you nothing new.

**`failed` still means it ran.** Per-document failures are collected in `errors` and the rest of the
corpus was still rebuilt. Render the error list rather than a bare "Failed".

### When to offer reindex

Reindex rebuilds every document from its current revision: re-chunks, re-embeds. It is the right
action after:

- changing chunking settings,
- editing a normalisation rule file (`/stats` will show stale chunks),
- adding an embedding model.

It is **not** something a content editor should ever need. Search keeps working throughout — a stale
index is degraded, not broken, and taking the corpus offline to rebuild would be the worse failure.

Jobs are tenant-scoped: another tenant's job id returns `404`.

---

## 12. Admin — retrieval quality

```
POST /docs/v1/eval/run    scope: docs:admin
```

```jsonc
{
  "collection": "neavents-help",
  "locale": "tr",
  "mode": "lexical",
  "cases": [
    { "query": "menümü nasıl yayınlarım", "expect": ["publishing-a-menu"], "maxRank": 3 },
    { "query": "sifremi unuttum",         "expect": ["password-reset"],    "maxRank": 1 }
  ]
}
```

Returns a report with `recallAt1`, `recallAt3`, `recallAt10`, `mrr`, `meanLatencyMs`, per-case
results, and `meets`.

**`200` means it met the quality floor. `422` means it ran and did not.** `422` is a result, not an
error — render the same report either way, with failures highlighted. Only `404` (unknown
collection) and `400` (empty case list) are real errors.

This is the most under-appreciated admin screen you can build. Retrieval degrades silently: nobody
files a bug saying "results are 8% worse". A stored golden set that anyone can re-run after editing
documentation is the only thing that makes that visible.

If you build one screen beyond search and upload, build this.

Per-case results carry `actualRank` and the top `returned` keys — enough to show *why* a case failed
without a second call.

---

## 13. Admin — stats and provider health

```
GET /docs/v1/stats               scope: docs:read
GET /docs/v1/health/providers    scope: docs:admin
```

`/stats` gives per-collection and per-locale counts plus `backlogDepth`. Good for a dashboard
header: documents, chunks, locales.

**`backlogDepth > 0` means embeddings are queued after a provider failure.** Documents are still
searchable lexically; vectors are catching up. Show it as a warning, not an error, and only if it
stays non-zero across several polls — a transient value during a large upload is normal.

`/health/providers` returns per model: `provider`, `model`, `dimensions`, `retired`, `healthy`,
`lastError`, `backlogDepth`. `configured: false` means lexical-only, which is a **valid, supported
configuration** — present it as a mode, not a misconfiguration.

`GET /health` and `GET /ready` are anonymous and are for your monitoring, not your UI.

---

## 14. Right-to-left and Turkish

The engine is careful about this and the UI has to match, or the care is wasted.

**`headingPath` is an array, never a joined string.** That is deliberate: joining with `›` bakes a
reading direction into the data and renders backwards in Arabic and Hebrew. Join it in the view,
with a separator chosen for the document's direction — and set `dir` from the hit's `locale`, not
from the UI language. A Turkish admin browsing Arabic documents needs each result rendered in its
own direction.

**`highlights` offsets are logical**, i.e. typing order, matching how JavaScript indexes strings.
Slicing works identically for RTL and LTR — do not reverse anything.

**Turkish input must not be "helpfully" normalised client-side.** Do not lowercase, strip accents or
transliterate before sending. The engine folds `İ`, `ı`, `ş`, `ğ`, `ü`, `ö`, `ç` correctly and a
JavaScript `toLowerCase()` will get `İ` wrong. Send exactly what the user typed.

The corollary is the good news: `sifre` finds `şifre`, `menumu` finds `menüyü`, and inflected forms
(`menümü`, `menülerimiz`) find the base form. **Do not build a "did you mean" layer** — you would be
re-solving something already handled, and worse.

---

## 15. Performance and caching

| Call | Typical | Cache |
|---|---|---|
| `POST /search` | 3–30 ms | Don't cache. Do debounce and cancel |
| `GET /documents/{key}` | 5–15 ms | Cache by `contentHash`; it changes only when content does |
| `GET /collections` | < 10 ms | Session-length cache is fine |
| `GET /stats` | 20–80 ms | 30–60 s. It aggregates |
| `POST /eval/run` | seconds | Never cache; it's a measurement |
| `POST /reindex` | returns instantly | The job is the slow part |

`contentHash` is the natural ETag for an article — if it hasn't changed, neither has the content.

Search is fast enough that a spinner will flash and look broken. Prefer skeletons, or show nothing
for the first 150 ms.

---

## 16. Rate limits

Fixed window per credential — default 600 requests per 10 seconds, queueing up to 200. Exceeding it
gives `429` with a `detail` naming the limit.

For a reader surface behind your own proxy, **every end user shares the proxy's credential**. Debounce
properly or one enthusiastic typist can rate-limit everybody. If that's a real risk, apply a
per-user limit in your proxy rather than raising the engine's.

---

## 17. Things the UI must never do

- **Never put a project key in the browser.** Proxy it.
- **Never send a tenant id.** No endpoint accepts one; if you feel the need, something upstream is wrong.
- **Never show `score` to a reader.** It's an internal ordering value.
- **Never treat `degraded: true` as an error** on the reader surface — or ignore it on the admin surface.
- **Never treat a bulk `200` as full success.** Read `results[]`.
- **Never lowercase or strip accents client-side.** You will break Turkish.
- **Never pre-join `headingPath`.** It breaks RTL.
- **Never render `snippet` as unsanitised HTML.** Allow `<em>` only, or use `highlights` instead.
- **Never poll a job faster than ~1 s.** Progress advances every 25 documents.
- **Never offer reindex to content editors.** It's an operator action.
- **Never swallow the correlation id.** It is the whole debugging story.
