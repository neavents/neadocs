# neadocs

A generic document storage and retrieval engine. Documents in, ranked passages out.

PostgreSQL full-text search and vector search over the same corpus, fused with Reciprocal Rank
Fusion. One Native-AOT binary, one config file, one Postgres.

```
git clone https://github.com/neavents/neadocs && cd neadocs
docker compose up -d
curl localhost:5700/ready
```

That gives you a working lexical search engine. **No API keys, no accounts, no model downloads.**

## What it is not

A help centre, a chatbot, a CMS, or anything that knows what your domain is. The engine has no
vocabulary of its own — collections, locales and metadata are yours to define.

Deliberately **not** in v1, as decisions rather than backlog: chat and RAG answering, rerankers,
external connectors (Notion, Confluence, S3, Drive), an admin UI, alternative vector stores,
multimodal input, agents, plugin systems, webhooks, multi-database support.

## Why Turkish is handled explicitly

Most search engines fold text with the runtime's culture rules. This one cannot: it publishes with
`InvariantGlobalization=true`, so there is no `tr-TR` culture at runtime. `"IST".ToLower()` yields
`ist`, not `ıst`.

That constraint turned out to be a good thing, because it forced every casing and folding rule to
become **data**:

```jsonc
{
  "tag": "tr",
  "pipeline": [
    { "op": "mapChars",  "map": { "I": "ı", "İ": "i" } },
    { "op": "lowercase", "mode": "ascii" },
    { "op": "mapChars",  "map": { "ş":"s", "ğ":"g", "ı":"i", "ü":"u", "ö":"o", "ç":"c" } },
    { "op": "collapseWhitespace" }
  ],
  "selfTest": [
    { "in": "IST",      "out": "ist" },
    { "in": "İSTANBUL", "out": "istanbul" },
    { "in": "ŞİFRE",    "out": "sifre" }
  ]
}
```

Drop that file into `./normalizers`, list `tr` in `Locales`, restart. **No rebuild.** The engine
runs every `selfTest` at boot and refuses to start if one fails, naming the tag, the input, what it
expected and what it got.

That last part is the point. A rule set without a proof of what "working" means for it is rejected,
so a language cannot be added by someone who has not said what correct looks like.

Turkish matters here because people type `sifre` when they mean `şifre`, constantly, and a search
engine that misses that is useless to them. The same machinery folds French accents, German
ß, Arabic harakat and Hebrew niqqud with no code and no new operations.

### Right-to-left

Arabic, Persian and Hebrew work with **zero new operations** — that was the test of whether the rule
format was the right size. Bidi control characters are stripped by explicit list rather than by
Unicode category, because the `Format` category also contains ZERO WIDTH NON-JOINER, which is
meaningful in Persian: `می‌رود` is two morphemes joined by one. Stripping the category wholesale
corrupts Persian while appearing to work, so the engine refuses that rule outright.

Text is stored and returned in logical order, and `headingPath` is a JSON array rather than a
pre-joined string, so no reading direction is ever baked into stored data.

## The operation set

Exactly six operations, and **no regular expressions**, ever.

| `op` | Fields | Behaviour |
|---|---|---|
| `mapChars` | `map`: char → string | One character to zero or more. `ß → ss` is why the target is a string. |
| `mapSequences` | `map`: string → string | Longest match first, left to right, non-overlapping. |
| `lowercase` | `mode`: `ascii` \| `invariant` | `ascii` touches only `A–Z`. |
| `stripUnicodeCategory` | `categories`: string[] | Unicode table data, not culture data. `Format` is refused. |
| `collapseWhitespace` | — | Runs become one space; ends trimmed. |
| `normalizeForm` | `form`: `FormC` \| `FormD` \| … | **Refused under invariant globalization** — see below. |

`normalizeForm` is declared but the engine rejects any rule set using it when the runtime cannot
perform normalization. Under `InvariantGlobalization=true`, `string.Normalize` returns its input
unchanged *and* `IsNormalized` returns `true`, so the no-op is undetectable at runtime. Rather than
execute a rule that provably does nothing, the boot fails and tells you to fold explicitly.

The set is closed by design. If a language needs something it cannot express, add a seventh named
operation with its own semantics and self-tests — never an escape hatch.

## Configuration

Every setting lives under `DocumentEngine` and is overridable as
`DocumentEngine__Section__Key`.

| Setting | Default | Notes |
|---|---|---|
| `PostgresConnectionString` | localhost | |
| `Schema` | `neadocs` | Every table lives here. Set it to share one database across services. |
| `EmbeddingModels` | `[]` | Empty is fully supported: lexical-only, no API key anywhere. Providers: `gemini`, `openai`, `deterministic`. |
| `DefaultSearchMode` | `hybrid` | `lexical` \| `vector` \| `hybrid`. Degrades to lexical and says so. |
| `RrfK` | `60` | Reciprocal Rank Fusion constant. |
| `CandidateMultiplier` / `MinCandidates` | `5` / `50` | Candidates per strategy = `max(min, limit × mult)`. |
| `Chunking:TargetTokens` | `400` | Soft ceiling; an indivisible block is emitted oversized. |
| `Chunking:OverlapPercent` | `15` | Snapped to a sentence boundary, excluded from the chunk hash. |
| `Chunking:SplitAtHeadingLevel` | `2` | |
| `Chunking:CharsPerToken` | `3.5` | Sizing only. No tokenizer dependency. |
| `Text:Locales` | `["en"]` | Adding one is config, not code. |
| `Text:NormalizersDirectory` | `./normalizers` | A file always wins over an embedded default. |
| `Text:LocaleFallback` | `{}` | Must be acyclic; checked at boot. |
| `Text:Synonyms` | `{}` | Keyed **by locale**. Expanded at query time, so editing never needs a reindex. |
| `JwtSymmetricKey` | — | ≥32 bytes. |
| `AllowedProjectKeys` | — | `tenant:key` or `tenant:key:read+write`. Omit scopes to grant admin. |
| `MaxRequestBodyBytes` | 4 MiB | |
| `MaxQueryLength` / `MaxSearchLimit` / `MaxBulkDocuments` | 512 / 100 / 500 | |

At least one credential mechanism must be configured or the boot is refused — every route except
`/health`, `/ready` and `/metrics` requires one.

Invalid configuration reports **every** problem at once, each naming its setting:

```
4 configuration errors:
  - DocumentEngine:Schema must be a bare lowercase SQL identifier matching [a-z_][a-z0-9_]{0,62}; got 'Not A Schema'.
  - DocumentEngine:RrfK must be greater than 0; got 0.
  - DocumentEngine:Text:DefaultLocale is 'de', which is not listed in DocumentEngine:Text:Locales.
  - No credential mechanism is configured: set DocumentEngine:JwtSymmetricKey, ...
```

## API

All routes under `/api/v1`. Errors are RFC 7807 `application/problem+json` and carry the
correlation id.

| Method | Route | Scope |
|---|---|---|
| `PUT` | `/collections/{key}` | `docs:admin` |
| `GET` | `/collections` | `docs:read` |
| `DELETE` | `/collections/{key}` | `docs:admin` |
| `PUT` | `/collections/{key}/documents/{externalKey}` | `docs:write` |
| `POST` | `/collections/{key}/documents:bulk` | `docs:write` |
| `GET` | `/collections/{key}/documents` | `docs:read` |
| `GET` | `/collections/{key}/documents/{externalKey}` | `docs:read` |
| `DELETE` | `/collections/{key}/documents/{externalKey}` | `docs:write` |
| `GET` | `/collections/{key}/documents/{externalKey}/revisions` | `docs:read` |
| `POST` | `/collections/{key}/search` | `docs:read` |
| `GET` | `/stats` | `docs:read` |
| `GET` | `/health` `/ready` `/metrics` | anonymous |

`docs:admin` ⊃ `docs:write` ⊃ `docs:read`.

**Tenancy.** The tenant is resolved once, in middleware, from the credential — never from a route
parameter, query string, header or body field. No endpoint accepts a tenant id as input, which is
what makes cross-tenant leakage structurally impossible rather than a thing to remember.

### Search

```jsonc
POST /api/v1/collections/acme-help/search
{ "query": "sifremi unuttum", "locale": "tr", "mode": "hybrid", "limit": 10 }
```

```jsonc
{
  "mode": "lexical",
  "degraded": true,
  "tookMs": 4,
  "hits": [{
    "externalKey": "password-reset",
    "locale": "tr",
    "headingPath": ["Şifremi unuttum", "Sıfırlama"],
    "score": 0.099,
    "snippet": "…<em>şifrenizi</em> sıfırlamak için…",
    "highlights": [{ "start": 41, "length": 11 }]
  }]
}
```

`degraded: true` means hybrid ran lexical-only because no embedding model was available. The client
can always tell.

## Ingestion

Re-uploading sixty documents where one paragraph changed re-embeds **one chunk**, not sixty
documents. Each chunk carries a content hash; unchanged chunks are reused, absent ones deleted. The
same upsert with unchanged content is a no-op that reports `changed: false`.

If an embedding provider is down, the document commits and stays lexically searchable, with the
missing vectors queued in a backlog that a worker drains. A vendor outage never makes a document
unfindable.

## Observability

Traces, metrics and logs are configured **separately** — "telemetry works" is never one answer.

- **Traces**: `Neadocs.Ingest`, `Neadocs.Search`, `Neadocs.Provider`, `Neadocs.Migration`.
- **Metrics**: Prometheus at `/metrics`, plus OTLP. `neadocs_build_info` is always present, so a
  scrape of a fresh instance proves the meter is wired rather than looking identical to silence.
- **Logs**: Serilog compact JSON to console and OTLP. Never document content, never full queries.

Every request carries `X-Correlation-Id` — echoed if you send one, generated from the trace id if
you don't, and rejected if it contains anything that could forge a log line.

## Development

```
dotnet test                                     # unit + integration
dotnet publish -c Release -r linux-x64          # native binary, zero trim warnings
./scripts/smoke.sh http://localhost:5700
```

The integration suite needs a Postgres. It creates a throwaway `neadocs_test_<id>` schema and drops
it on teardown, so it can share a server with anything else without reaching it. Point it elsewhere
with `NEADOCS_TEST_POSTGRES`.

The unit suite runs with `InvariantGlobalization=true` deliberately — the text layer's behaviour
depends on ICU being absent, and a suite that runs with ICU present verifies rules the service never
executes.

## Licence

MIT.
