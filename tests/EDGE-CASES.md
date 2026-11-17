# Paranoid Edge Cases & Hardening Registry — Neadocs

All scenarios below are covered in `Neadocs.Engine.Tests.Unit` and `Neadocs.Engine.Tests.Integration` (against real `pgvector/pgvector:pg17`, with external embedding providers mocked/stubbed — 0 external calls) and verified against Native AOT publishing.

## 1. Authentication & Tenant/Scope Isolation
- Duplicate `X-Project-Key` header smuggling (`[admin, smuggled]`, `[smuggled, admin]`, `[admin, admin]`, `[tenantA, tenantB]`) -> fails closed with `401 Unauthorized` (`application/problem+json`).
- HS256 JWT boundary checks: scoped read-only JWTs (`403` on writes), expired JWTs, wrong-signature JWTs, and valid-signature tokens missing the `tenant` claim (`401`).
- Constant-time `ApiKeyValidator` UTF-8 matching (prefix/suffix rejection).
- Strict scope hierarchy (`read` / `write` / `admin`) and cross-tenant isolation across collections, documents, search, deletes, reindex, stats, and provider health.

## 2. PostgreSQL Encoding & JSON Safety (`\0` / `\u0000` / Malformed JSON)
- NUL (`\0`) in collection `key` or `name`, document `externalKey`/`title`/`content`/`sourceUri`/`sourceContentHash`, search `query`, query parameters (`locale`, `staleAgainst`, `cursor`), and eval inputs -> rejected cleanly with `400 Bad Request` (`application/problem+json`) before reaching Postgres.
- Escaped `\u0000` inside JSONB payloads (`UpsertCollectionRequest.Config`, `UpsertDocumentRequest.Metadata`, `BulkUpsertItem.Metadata`, `SearchRequest.Filter`) detected via recursive AOT-safe `JsonElement` traversal -> `400 Bad Request` (in bulk mode, item-level `400` rather than `500`), while preserving valid escaped `"\\u0000"` text without false positives.
- Malformed JSON request bodies (`BadHttpRequestException`) -> `400 application/problem+json`.

## 3. Search & Lexical/Vector Retrieval Boundaries
- Hostile `tsquery` metacharacters (`& | ! ( ) : * ' \ <->`), unmatched quotes, punctuation-only strings, and Turkish `İ`/`ı` folding -> never crash `websearch_to_tsquery` or trigram queries.
- `RrfFuser` rejects `k <= 0` and breaks ties deterministically by `Ordinal` then `ChunkId`.
- `EmbeddingStore.Literal` rejects `NaN`, `+/-Infinity`, and empty vectors before building pgvector literals.

## 4. Embedding Provider & Concurrency Resilience (0 External Calls)
- `HttpEmbeddingProvider` (`OpenAiEmbeddingProvider` / `GeminiEmbeddingProvider`) tested via stub `HttpMessageHandler`: HTTP `200` malformed JSON, missing `data`/`embeddings`, wrong JSON types, or non-finite floats raise `EmbeddingRequestFailedException`.
- `EmbeddingChain.EmbedAsync` validates vector counts, vector dimensions (`Length == Dimensions`), and finite float elements on every batch, throwing `EmbeddingRequestFailedException` before corrupting storage.
- Concurrent upserts against the same `(collection, externalKey, locale)` complete cleanly under Postgres concurrency.
