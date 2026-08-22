namespace Neadocs.Engine.Infrastructure.Storage;

public sealed class VectorTypeInfo
{
    private string? _vectorSchema;
    private string _trigramSchema = "public";

    public bool Available => _vectorSchema is not null;

    public string Schema => _vectorSchema
        ?? throw new System.InvalidOperationException(
            "The pgvector extension schema has not been resolved. The migrator sets it, so a "
            + "vector query reaching here ran before migration or in lexical-only mode.");

    public string TypeName => $"{Schema}.vector";

    public string TrigramSchema => _trigramSchema;

    public void Resolve(string schema) => _vectorSchema = schema;

    public void ResolveTrigram(string schema) => _trigramSchema = schema;
}
