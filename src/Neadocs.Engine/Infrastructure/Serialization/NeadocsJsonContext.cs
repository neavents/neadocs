namespace Neadocs.Engine.Infrastructure.Serialization;

using System.Text.Json.Serialization;
using Neadocs.Engine.Infrastructure.Http;
using Neadocs.Engine.Features;
using Neadocs.Engine.Infrastructure.Text;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProblemResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(RuleSet))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(UpsertCollectionRequest))]
[JsonSerializable(typeof(CollectionResponse))]
[JsonSerializable(typeof(CollectionListResponse))]
[JsonSerializable(typeof(UpsertDocumentRequest))]
[JsonSerializable(typeof(BulkUpsertRequest))]
[JsonSerializable(typeof(BulkUpsertResponse))]
[JsonSerializable(typeof(UpsertDocumentResponse))]
[JsonSerializable(typeof(DocumentResponse))]
[JsonSerializable(typeof(DocumentListResponse))]
[JsonSerializable(typeof(RevisionListResponse))]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(StatsResponse))]
[JsonSerializable(typeof(NormalizerListResponse))]
[JsonSerializable(typeof(ProviderHealthResponse))]
[JsonSerializable(typeof(JobResponse))]
[JsonSerializable(typeof(JobAcceptedResponse))]
[JsonSerializable(typeof(Neadocs.Engine.Infrastructure.Evaluation.EvalSet))]
[JsonSerializable(typeof(Neadocs.Engine.Infrastructure.Evaluation.EvalReport))]
public sealed partial class NeadocsJsonContext : JsonSerializerContext
{
}
