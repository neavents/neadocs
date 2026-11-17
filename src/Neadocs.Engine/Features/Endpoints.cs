namespace Neadocs.Engine.Features;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Diagnostics;
using Neadocs.Engine.Infrastructure.Evaluation;
using Neadocs.Engine.Infrastructure.Providers;
using Neadocs.Engine.Infrastructure.Http;
using Neadocs.Engine.Infrastructure.Retrieval;
using Neadocs.Engine.Infrastructure.Serialization;
using Neadocs.Engine.Infrastructure.Security;
using Neadocs.Engine.Infrastructure.Storage;
using Neadocs.Engine.Infrastructure.Text;

public static class Endpoints
{
    private const string Prefix = "/api/v1";

    public static void MapNeadocs(this WebApplication app)
    {
        app.MapPut($"{Prefix}/collections/{{key}}", UpsertCollection);
        app.MapGet($"{Prefix}/collections", ListCollections);
        app.MapDelete($"{Prefix}/collections/{{key}}", DeleteCollection);

        app.MapPut($"{Prefix}/collections/{{key}}/documents/{{externalKey}}", UpsertDocument);
        app.MapPost($"{Prefix}/collections/{{key}}/documents:bulk", BulkUpsert);
        app.MapGet($"{Prefix}/collections/{{key}}/documents", ListDocuments);
        app.MapGet($"{Prefix}/collections/{{key}}/documents/{{externalKey}}", GetDocument);
        app.MapDelete($"{Prefix}/collections/{{key}}/documents/{{externalKey}}", DeleteDocument);
        app.MapGet($"{Prefix}/collections/{{key}}/documents/{{externalKey}}/revisions", ListRevisions);

        app.MapPost($"{Prefix}/collections/{{key}}/search", Search);

        app.MapGet($"{Prefix}/stats", GetStats);
        app.MapPost($"{Prefix}/eval/run", RunEval);
        app.MapGet($"{Prefix}/health/providers", GetProviderHealth);
        app.MapGet($"{Prefix}/text/normalizers", GetNormalizers);
        app.MapPost($"{Prefix}/collections/{{key}}/reindex", Reindex);
        app.MapGet($"{Prefix}/jobs/{{id:guid}}", GetJob);
    }

    private static IResult? Guard(HttpContext context, DocumentScope required, out RequestPrincipal principal)
    {
        principal = RequestPrincipal.Require(context);

        return principal.Grants(required)
            ? null
            : Problem.Result(
                context,
                StatusCodes.Status403Forbidden,
                "Forbidden",
                $"This credential holds [{DocumentScopeNames.Format(principal.Scopes)}] and the "
                + $"route requires {DocumentScopeNames.Format(required)}.");
    }

    private static bool ContainsNullChar(string? value) =>
        value is not null && value.IndexOf('\0') >= 0;

    private static bool ContainsNullChar(JsonElement? element) =>
        element is { } value && ContainsNullCharInJson(value);

    private static bool ContainsNullCharInJson(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return ContainsNullChar(value.GetString());
            case JsonValueKind.Array:
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (ContainsNullCharInJson(item))
                    {
                        return true;
                    }
                }
                return false;
            case JsonValueKind.Object:
                foreach (JsonProperty prop in value.EnumerateObject())
                {
                    if (ContainsNullChar(prop.Name) || ContainsNullCharInJson(prop.Value))
                    {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    private static IResult? GuardNullChars(HttpContext context, params string?[] values)
    {
        foreach (string? value in values)
        {
            if (ContainsNullChar(value))
            {
                return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                    "Input values must not contain NUL bytes.");
            }
        }

        return null;
    }

    private static IResult? ValidateKey(HttpContext context, string key, string name)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || ContainsNullChar(key))
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                $"{name} must be between 1 and 128 characters and contain no NUL bytes.");
        }

        return null;
    }

    private static bool TryLocale(string? raw, out string locale)
    {
        locale = LocaleTag.Normalize(raw);

        return locale.Length > 0 && LocaleTag.IsWellFormed(locale);
    }

    private static string JsonOrDefault(JsonElement? element, string fallback) =>
        element is null || element.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? fallback
            : element.Value.GetRawText();

    private static async Task<IResult> UpsertCollection(
        HttpContext context, string key, UpsertCollectionRequest request, DocumentStore store, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Admin, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        if (ValidateKey(context, key, "Collection key") is IResult invalid)
        {
            return invalid;
        }

        if (GuardNullChars(context, request.Name) is IResult badText
            || ContainsNullChar(request.Config))
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                "Collection fields must not contain NUL bytes.");
        }

        string name = string.IsNullOrWhiteSpace(request.Name) ? key : request.Name;

        (CollectionRow row, bool created) = await store.UpsertCollectionAsync(
            principal.Tenant, key, name, JsonOrDefault(request.Config, "{}"), ct);

        CollectionResponse payload = new()
        {
            Id = row.Id,
            Key = row.Key,
            Name = row.Name,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };

        return Results.Json(payload, NeadocsJsonContext.Default.CollectionResponse,
            statusCode: created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static async Task<IResult> ListCollections(
        HttpContext context, DocumentStore store, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Read, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        CollectionListResponse payload = new()
        {
            Items = await store.ListCollectionsAsync(principal.Tenant, ct),
        };

        return Results.Json(payload, NeadocsJsonContext.Default.CollectionListResponse);
    }

    private static async Task<IResult> DeleteCollection(
        HttpContext context, string key, DocumentStore store, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Admin, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        if (GuardNullChars(context, key) is IResult badKey)
        {
            return badKey;
        }

        bool removed = await store.DeleteCollectionAsync(principal.Tenant, key, ct);

        return removed
            ? Results.NoContent()
            : Problem.Result(context, StatusCodes.Status404NotFound, "Not Found",
                $"No collection '{key}' exists for this credential.");
    }

    private static async Task<IResult> UpsertDocument(
        HttpContext context, string key, string externalKey, UpsertDocumentRequest request,
        DocumentStore store, IOptions<DocumentEngineOptions> options, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Write, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        if (ValidateKey(context, externalKey, "Document key") is IResult invalid)
        {
            return invalid;
        }

        if (!TryLocale(request.Locale, out string locale))
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                $"'{request.Locale}' is not a well-formed BCP-47 locale tag.");
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                "'content' must not be empty.");
        }

        if (GuardNullChars(context, key, request.Title, request.Content, request.SourceUri,
                request.SourceLocale, request.SourceContentHash) is not null
            || ContainsNullChar(request.Metadata))
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                "Document fields must not contain NUL bytes.");
        }

        bool force = context.Request.Query.TryGetValue("force", out Microsoft.Extensions.Primitives.StringValues f)
            && string.Equals(f.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        UpsertDocumentResponse? result = await store.UpsertDocumentAsync(
            principal.Tenant, key, externalKey, locale,
            string.IsNullOrWhiteSpace(request.Title) ? externalKey : request.Title,
            request.Content, request.SourceUri, JsonOrDefault(request.Metadata, "{}"),
            request.SourceLocale is null ? null : LocaleTag.Normalize(request.SourceLocale),
            request.SourceContentHash, force, ct);

        return result is null
            ? Problem.Result(context, StatusCodes.Status404NotFound, "Not Found",
                $"No collection '{key}' exists for this credential.")
            : Results.Json(result, NeadocsJsonContext.Default.UpsertDocumentResponse);
    }

    private static async Task<IResult> BulkUpsert(
        HttpContext context, string key, BulkUpsertRequest request, DocumentStore store,
        IOptions<DocumentEngineOptions> options, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Write, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        if (GuardNullChars(context, key) is IResult badKey)
        {
            return badKey;
        }

        int max = options.Value.MaxBulkDocuments;

        if (request.Documents.Count > max)
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                $"A bulk request carries at most {max} documents; got {request.Documents.Count}.");
        }

        BulkUpsertResponse response = new() { Total = request.Documents.Count };

        foreach (BulkUpsertItem item in request.Documents)
        {
            BulkUpsertResult result = new()
            {
                ExternalKey = item.ExternalKey,
                Locale = item.Locale,
            };

            if (!TryLocale(item.Locale, out string locale))
            {
                result.Status = StatusCodes.Status400BadRequest;
                result.Error = $"'{item.Locale}' is not a well-formed BCP-47 locale tag.";
                response.Results.Add(result);
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.ExternalKey) || string.IsNullOrWhiteSpace(item.Content))
            {
                result.Status = StatusCodes.Status400BadRequest;
                result.Error = "'externalKey' and 'content' are required.";
                response.Results.Add(result);
                continue;
            }

            if (ContainsNullChar(item.ExternalKey) || ContainsNullChar(item.Title)
                || ContainsNullChar(item.Content) || ContainsNullChar(item.SourceUri)
                || ContainsNullChar(item.SourceLocale) || ContainsNullChar(item.SourceContentHash)
                || ContainsNullChar(item.Metadata))
            {
                result.Status = StatusCodes.Status400BadRequest;
                result.Error = "Document fields must not contain NUL bytes.";
                response.Results.Add(result);
                continue;
            }

            try
            {
                UpsertDocumentResponse? single = await store.UpsertDocumentAsync(
                    principal.Tenant, key, item.ExternalKey, locale,
                    string.IsNullOrWhiteSpace(item.Title) ? item.ExternalKey : item.Title,
                    item.Content, item.SourceUri, JsonOrDefault(item.Metadata, "{}"),
                    item.SourceLocale is null ? null : LocaleTag.Normalize(item.SourceLocale),
                    item.SourceContentHash, force: false, ct);

                if (single is null)
                {
                    return Problem.Result(context, StatusCodes.Status404NotFound, "Not Found",
                        $"No collection '{key}' exists for this credential.");
                }

                result.Locale = locale;
                result.Status = StatusCodes.Status200OK;
                result.Changed = single.Changed;
                result.Revision = single.Revision;

                if (single.Changed)
                {
                    response.Changed++;
                }
            }
            catch (Npgsql.PostgresException ex)
            {
                result.Status = StatusCodes.Status500InternalServerError;
                result.Error = ex.MessageText;
            }

            response.Results.Add(result);
        }

        return Results.Json(response, NeadocsJsonContext.Default.BulkUpsertResponse);
    }

    private static async Task<IResult> ListDocuments(
        HttpContext context, string key, DocumentReader reader, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Read, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        string? locale = context.Request.Query["locale"].ToString();
        string? staleAgainst = context.Request.Query["staleAgainst"].ToString();
        string? cursor = context.Request.Query["cursor"].ToString();

        if (GuardNullChars(context, key, locale, staleAgainst, cursor) is IResult badParams)
        {
            return badParams;
        }

        int limit = int.TryParse(context.Request.Query["limit"], out int parsed) ? Math.Clamp(parsed, 1, 200) : 50;

        DocumentListResponse? payload = await reader.ListAsync(
            principal.Tenant, key,
            string.IsNullOrWhiteSpace(locale) ? null : LocaleTag.Normalize(locale),
            string.IsNullOrWhiteSpace(staleAgainst) ? null : LocaleTag.Normalize(staleAgainst),
            string.IsNullOrWhiteSpace(cursor) ? null : cursor,
            limit, ct);

        return payload is null
            ? Problem.Result(context, StatusCodes.Status404NotFound, "Not Found",
                $"No collection '{key}' exists for this credential.")
            : Results.Json(payload, NeadocsJsonContext.Default.DocumentListResponse);
    }

    private static async Task<IResult> GetDocument(
        HttpContext context, string key, string externalKey, DocumentReader reader, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Read, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        string localeRaw = context.Request.Query["locale"].ToString();
        if (GuardNullChars(context, key, externalKey, localeRaw) is IResult badParams)
        {
            return badParams;
        }

        string? locale = string.IsNullOrWhiteSpace(localeRaw) ? null : LocaleTag.Normalize(localeRaw);

        (DocumentResponse? document, int matches) =
            await reader.GetAsync(principal.Tenant, key, externalKey, locale, ct);

        if (matches > 1)
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Ambiguous",
                $"'{externalKey}' exists in {matches} locales; specify ?locale=.");
        }

        return document is null
            ? Problem.Result(context, StatusCodes.Status404NotFound, "Not Found",
                $"No document '{externalKey}' in collection '{key}'.")
            : Results.Json(document, NeadocsJsonContext.Default.DocumentResponse);
    }

    private static async Task<IResult> DeleteDocument(
        HttpContext context, string key, string externalKey, DocumentReader reader, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Write, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        string localeRaw = context.Request.Query["locale"].ToString();
        if (GuardNullChars(context, key, externalKey, localeRaw) is IResult badParams)
        {
            return badParams;
        }

        string? locale = string.IsNullOrWhiteSpace(localeRaw) ? null : LocaleTag.Normalize(localeRaw);

        int removed = await reader.SoftDeleteAsync(principal.Tenant, key, externalKey, locale, ct);

        return removed > 0
            ? Results.NoContent()
            : Problem.Result(context, StatusCodes.Status404NotFound, "Not Found",
                $"No document '{externalKey}' in collection '{key}'.");
    }

    private static async Task<IResult> ListRevisions(
        HttpContext context, string key, string externalKey, DocumentReader reader, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Read, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        string localeRaw = context.Request.Query["locale"].ToString();
        if (GuardNullChars(context, key, externalKey, localeRaw) is IResult badParams)
        {
            return badParams;
        }

        string? locale = string.IsNullOrWhiteSpace(localeRaw) ? null : LocaleTag.Normalize(localeRaw);

        RevisionListResponse? payload =
            await reader.ListRevisionsAsync(principal.Tenant, key, externalKey, locale, ct);

        return payload is null
            ? Problem.Result(context, StatusCodes.Status404NotFound, "Not Found",
                $"No document '{externalKey}' in collection '{key}'.")
            : Results.Json(payload, NeadocsJsonContext.Default.RevisionListResponse);
    }

    private static async Task<IResult> Search(
        HttpContext context, string key, SearchRequest request, SearchService search,
        IOptions<DocumentEngineOptions> options, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Read, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        DocumentEngineOptions engine = options.Value;

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                "'query' must not be empty.");
        }

        if (GuardNullChars(context, key, request.Query, request.Mode) is not null
            || ContainsNullChar(request.Filter))
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                "Search parameters must not contain NUL bytes.");
        }

        if (request.Query.Length > engine.MaxQueryLength)
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                $"'query' is limited to {engine.MaxQueryLength} characters; got {request.Query.Length}.");
        }

        if (request.Locale is not null && !TryLocale(request.Locale, out _))
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                $"'{request.Locale}' is not a well-formed BCP-47 locale tag.");
        }

        string mode = string.IsNullOrWhiteSpace(request.Mode) ? engine.DefaultSearchMode : request.Mode;

        if (string.Equals(mode, "vector", StringComparison.OrdinalIgnoreCase)
            && !search.HasEmbeddingModel)
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                "no embedding model is configured");
        }

        long started = Stopwatch.GetTimestamp();

        SearchResponse? response = await search.SearchAsync(
            principal.Tenant, key, request, mode, ct);

        if (response is null)
        {
            return Problem.Result(context, StatusCodes.Status404NotFound, "Not Found",
                $"No collection '{key}' exists for this credential.");
        }

        response.TookMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        NeadocsMeters.SearchDuration.Record(response.TookMs,
            new KeyValuePair<string, object?>(NeadocsTags.Mode, response.Mode),
            new KeyValuePair<string, object?>(NeadocsTags.Collection, key),
            new KeyValuePair<string, object?>(NeadocsTags.Degraded, response.Degraded));
        NeadocsMeters.SearchHits.Record(response.Hits.Count,
            new KeyValuePair<string, object?>(NeadocsTags.Mode, response.Mode));

        return Results.Json(response, NeadocsJsonContext.Default.SearchResponse);
    }

    /// <summary>
    /// The compiled normalisation rules per locale — what `staleChunks` is measured against.
    /// </summary>
    private static IResult GetNormalizers(
        HttpContext context, NormalizerRegistry normalizers)
    {
        if (Guard(context, DocumentScope.Read, out RequestPrincipal _) is IResult denied)
        {
            return denied;
        }

        NormalizerListResponse payload = new();

        foreach (LoadedRuleSet loaded in normalizers.All)
        {
            CompiledPipeline pipeline = loaded.Pipeline;
            List<string> operations = [];
            int dropped = 0;

            foreach (ICompiledOperation operation in pipeline.Operations)
            {
                operations.Add(operation.Name);

                if (operation is DropTokensOperation drop)
                {
                    dropped += drop.Count;
                }
            }

            payload.Items.Add(new NormalizerResponse
            {
                Tag = pipeline.Tag,
                Hash = pipeline.Hash,
                SearchConfig = pipeline.SearchConfig,
                StemPrefixLength = pipeline.StemPrefixLength,
                Operations = operations,
                DroppedTokenCount = dropped,
                SelfTestCount = pipeline.SelfTests.Count,
                FromFile = loaded.FromFile,
                Origin = loaded.Origin,
            });
        }

        payload.Items.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));

        return Results.Json(payload, NeadocsJsonContext.Default.NormalizerListResponse);
    }

    private static async Task<IResult> GetStats(
        HttpContext context, DocumentReader reader, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Read, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        StatsResponse payload = await reader.StatsAsync(principal.Tenant, ct);

        return Results.Json(payload, NeadocsJsonContext.Default.StatsResponse);
    }

    private static async Task<IResult> RunEval(
        HttpContext context, EvalSet request, EvalRunner runner, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Admin, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(request.Collection) || request.Cases.Count == 0)
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                "'collection' and at least one case are required.");
        }

        if (GuardNullChars(context, request.Collection, request.Locale, request.Mode) is not null
            || request.Cases.Exists(c => ContainsNullChar(c.Query)))
        {
            return Problem.Result(context, StatusCodes.Status400BadRequest, "Invalid request",
                "Evaluation inputs must not contain NUL bytes.");
        }

        EvalReport? report = await runner.RunAsync(principal.Tenant, request, ct);

        if (report is null)
        {
            return Problem.Result(context, StatusCodes.Status404NotFound, "Not Found",
                $"No collection '{request.Collection}' exists for this credential.");
        }

        return Results.Json(report, NeadocsJsonContext.Default.EvalReport,
            statusCode: report.Meets ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetProviderHealth(
        HttpContext context, EmbeddingChain chain, EmbeddingModelRegistry models,
        EmbeddingStore store, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Admin, out RequestPrincipal _) is IResult denied)
        {
            return denied;
        }

        ProviderHealthResponse response = new() { Configured = models.HasActiveModel };
        IReadOnlyList<ProviderHealth> health = await chain.HealthAsync(ct);

        foreach (EmbeddingModelDescriptor model in models.All)
        {
            ProviderHealth? probe = null;

            foreach (ProviderHealth candidate in health)
            {
                if (candidate.Model == model.Model)
                {
                    probe = candidate;
                    break;
                }
            }

            response.Providers.Add(new ProviderHealthItem
            {
                Provider = model.Provider,
                Model = model.Model,
                Slug = model.Slug,
                Dimensions = model.Dimensions,
                Retired = model.Retired,
                Healthy = probe?.Healthy ?? false,
                LastError = probe?.LastError,
                BacklogDepth = store.Enabled ? await store.BacklogDepthAsync(model.Slug, ct) : 0,
            });
        }

        return Results.Json(response, NeadocsJsonContext.Default.ProviderHealthResponse);
    }

    private static async Task<IResult> Reindex(
        HttpContext context, string key, ReindexService reindex, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Admin, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        string localeRaw = context.Request.Query["locale"].ToString();
        if (GuardNullChars(context, key, localeRaw) is IResult badParams)
        {
            return badParams;
        }

        string? locale = string.IsNullOrWhiteSpace(localeRaw) ? null : LocaleTag.Normalize(localeRaw);

        Guid? jobId = await reindex.QueueAsync(principal.Tenant, key, locale, ct);

        if (jobId is null)
        {
            return Problem.Result(context, StatusCodes.Status404NotFound, "Not Found",
                $"No collection '{key}' exists for this credential.");
        }

        return Results.Json(
            new JobAcceptedResponse { JobId = jobId.Value, State = JobStates.Queued },
            NeadocsJsonContext.Default.JobAcceptedResponse,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> GetJob(
        HttpContext context, Guid id, JobStore jobs, CancellationToken ct)
    {
        if (Guard(context, DocumentScope.Admin, out RequestPrincipal principal) is IResult denied)
        {
            return denied;
        }

        JobResponse? job = await jobs.GetAsync(principal.Tenant, id, ct);

        return job is null
            ? Problem.Result(context, StatusCodes.Status404NotFound, "Not Found",
                $"No job '{id}' exists for this credential.")
            : Results.Json(job, NeadocsJsonContext.Default.JobResponse);
    }
}
