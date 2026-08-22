namespace Neadocs.Engine.Infrastructure.Diagnostics;

using System.Diagnostics;

public static class NeadocsActivitySources
{
    public const string IngestName = "Neadocs.Ingest";
    public const string SearchName = "Neadocs.Search";
    public const string ProviderName = "Neadocs.Provider";
    public const string MigrationName = "Neadocs.Migration";

    public static readonly ActivitySource Ingest = new(IngestName);
    public static readonly ActivitySource Search = new(SearchName);
    public static readonly ActivitySource Provider = new(ProviderName);
    public static readonly ActivitySource Migration = new(MigrationName);

    public static readonly string[] All = [IngestName, SearchName, ProviderName, MigrationName];
}
