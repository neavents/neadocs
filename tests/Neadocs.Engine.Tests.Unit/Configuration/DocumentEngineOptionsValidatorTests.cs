namespace Neadocs.Engine.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Configuration;

public sealed class DocumentEngineOptionsValidatorTests
{
    private static DocumentEngineOptions Valid() => new()
    {
        PostgresConnectionString = "Host=localhost;Database=neadocs;Username=neadocs;Password=x",
        Schema = "neadocs",
        JwtSymmetricKey = new string('k', 32),
        Text = new TextOptions
        {
            Locales = ["tr", "en"],
            DefaultLocale = "tr",
            LocaleFallback = new Dictionary<string, List<string>> { ["tr"] = ["en"], ["en"] = [] },
            Synonyms = [],
        },
    };

    private static IReadOnlyList<string> Errors(Action<DocumentEngineOptions> mutate)
    {
        DocumentEngineOptions options = Valid();
        mutate(options);
        return DocumentEngineOptionsValidator.Validate(options);
    }

    private static string Single(Action<DocumentEngineOptions> mutate)
    {
        IReadOnlyList<string> errors = Errors(mutate);
        errors.Should().ContainSingle();
        return errors[0];
    }

    [Fact]
    public void AcceptsAValidConfiguration() =>
        DocumentEngineOptionsValidator.Validate(Valid()).Should().BeEmpty();

    [Fact]
    public void AcceptsZeroProviderMode()
    {
        DocumentEngineOptions options = Valid();
        options.EmbeddingModels = [];
        options.Providers = [];
        options.DefaultSearchMode = "hybrid";

        DocumentEngineOptionsValidator.Validate(options).Should().BeEmpty();
    }

    [Fact]
    public void ThrowIfInvalidPassesForAValidConfiguration()
    {
        Action act = () => DocumentEngineOptionsValidator.ThrowIfInvalid(Valid());

        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowIfInvalidReportsEveryErrorAtOnce()
    {
        DocumentEngineOptions options = Valid();
        options.PostgresConnectionString = "";
        options.RrfK = 0;
        options.Schema = "Not Valid";

        Action act = () => DocumentEngineOptionsValidator.ThrowIfInvalid(options);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("3 configuration errors")
                .And.Contain("PostgresConnectionString")
                .And.Contain("RrfK")
                .And.Contain("Schema");
    }

    [Fact]
    public void RejectsAMissingConnectionString() =>
        Single(o => o.PostgresConnectionString = "  ")
            .Should().Contain("DocumentEngine:PostgresConnectionString");

    [Theory]
    [InlineData("")]
    [InlineData("Neadocs")]
    [InlineData("nea docs")]
    [InlineData("nea-docs")]
    [InlineData("1neadocs")]
    [InlineData("neadocs;DROP TABLE x")]
    [InlineData("\"neadocs\"")]
    public void RejectsASchemaThatIsNotABareIdentifier(string schema) =>
        Single(o => o.Schema = schema).Should().Contain("DocumentEngine:Schema");

    [Theory]
    [InlineData("neadocs")]
    [InlineData("neadocs_test_01j9")]
    [InlineData("_private")]
    [InlineData("a")]
    public void AcceptsABareIdentifierSchema(string schema) =>
        Errors(o => o.Schema = schema).Should().BeEmpty();

    [Fact]
    public void RejectsAnOverlongSchema() =>
        Single(o => o.Schema = new string('a', 64)).Should().Contain("DocumentEngine:Schema");

    [Fact]
    public void RejectsAnUnknownSearchMode() =>
        Single(o => o.DefaultSearchMode = "fuzzy")
            .Should().Contain("DocumentEngine:DefaultSearchMode").And.Contain("fuzzy");

    [Theory]
    [InlineData("hybrid")]
    [InlineData("HYBRID")]
    [InlineData("Lexical")]
    public void AcceptsSearchModeInAnyAsciiCasing(string mode) =>
        Errors(o => o.DefaultSearchMode = mode).Should().BeEmpty();

    [Fact]
    public void RejectsVectorModeWithNoConfiguredModel() =>
        Single(o => o.DefaultSearchMode = "vector")
            .Should().Contain("DefaultSearchMode is 'vector'").And.Contain("EmbeddingModels");

    [Fact]
    public void RejectsVectorModeWhenEveryModelIsRetired()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.DefaultSearchMode = "vector";
            o.EmbeddingModels = [new() { Model = "m", Dimensions = 8, Retired = true }];
        });

        errors.Should().ContainSingle().Which.Should().Contain("DefaultSearchMode is 'vector'");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveRrfK(int k) =>
        Single(o => o.RrfK = k).Should().Contain("DocumentEngine:RrfK");

    [Fact]
    public void RejectsANonPositiveCandidateMultiplier() =>
        Single(o => o.CandidateMultiplier = 0).Should().Contain("CandidateMultiplier");

    [Fact]
    public void RejectsANonPositiveMinCandidates() =>
        Single(o => o.MinCandidates = 0).Should().Contain("MinCandidates");

    [Fact]
    public void RejectsANonPositiveHnswEfSearch() =>
        Single(o => o.HnswEfSearch = 0).Should().Contain("HnswEfSearch");

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void RejectsAnOutOfRangeMinRecall(double value) =>
        Single(o => o.MinRecallAt3 = value).Should().Contain("MinRecallAt3");

    [Theory]
    [InlineData(49)]
    [InlineData(4001)]
    public void RejectsOutOfRangeTargetTokens(int tokens) =>
        Single(o => o.Chunking.TargetTokens = tokens).Should().Contain("Chunking:TargetTokens");

    [Theory]
    [InlineData(-1)]
    [InlineData(51)]
    public void RejectsOutOfRangeOverlapPercent(int percent) =>
        Single(o => o.Chunking.OverlapPercent = percent).Should().Contain("Chunking:OverlapPercent");

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void RejectsOutOfRangeSplitAtHeadingLevel(int level) =>
        Single(o => o.Chunking.SplitAtHeadingLevel = level).Should().Contain("SplitAtHeadingLevel");

    [Fact]
    public void RejectsANonPositiveCharsPerToken() =>
        Single(o => o.Chunking.CharsPerToken = 0).Should().Contain("CharsPerToken");

    [Fact]
    public void RejectsANonPositiveMaxChunksPerDocument() =>
        Single(o => o.Chunking.MaxChunksPerDocument = 0).Should().Contain("MaxChunksPerDocument");

    [Fact]
    public void RejectsAnEmptyLocaleList()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.Text.Locales = [];
            o.Text.LocaleFallback = [];
        });

        errors.Should().Contain(e => e.Contains("Text:Locales must declare at least one"));
    }

    [Theory]
    [InlineData("türkçe")]
    [InlineData("t")]
    [InlineData("tr-")]
    [InlineData("toolong")]
    [InlineData("*")]
    public void RejectsAMalformedLocale(string locale)
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.Text.Locales = ["tr", locale];
            o.Text.LocaleFallback = [];
        });

        errors.Should().Contain(e => e.Contains("Text:Locales contains") && e.Contains(locale));
    }

    [Fact]
    public void NormalizesLocalesBeforeComparing()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.Text.Locales = ["TR", "en_GB"];
            o.Text.DefaultLocale = "tr_TR";
            o.Text.LocaleFallback = [];
        });

        errors.Should().ContainSingle()
            .Which.Should().Contain("DefaultLocale is 'tr_TR'");
    }

    [Fact]
    public void AcceptsAnUnnormalizedButValidLocale()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.Text.Locales = ["TR", "en_GB"];
            o.Text.DefaultLocale = "tr";
            o.Text.LocaleFallback = [];
        });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void RejectsADuplicateLocale()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.Text.Locales = ["tr", "TR"];
            o.Text.LocaleFallback = [];
        });

        errors.Should().Contain(e => e.Contains("more than once"));
    }

    [Fact]
    public void RejectsAMissingDefaultLocale() =>
        Single(o => o.Text.DefaultLocale = "").Should().Contain("Text:DefaultLocale must be set");

    [Fact]
    public void RejectsADefaultLocaleOutsideTheDeclaredSet() =>
        Single(o => o.Text.DefaultLocale = "de")
            .Should().Contain("Text:DefaultLocale is 'de'");

    [Fact]
    public void RejectsAnEmptyNormalizersDirectory() =>
        Single(o => o.Text.NormalizersDirectory = " ").Should().Contain("NormalizersDirectory");

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void RejectsAnOutOfRangeTrigramThreshold(double value) =>
        Single(o => o.Text.TrigramThreshold = value).Should().Contain("TrigramThreshold");

    [Fact]
    public void RejectsAFallbackKeyOutsideTheDeclaredSet() =>
        Single(o => o.Text.LocaleFallback["de"] = [])
            .Should().Contain("LocaleFallback has key 'de'");

    [Fact]
    public void RejectsAFallbackTargetOutsideTheDeclaredSet() =>
        Single(o => o.Text.LocaleFallback["tr"] = ["de"])
            .Should().Contain("falls back to 'de'");

    [Fact]
    public void RejectsALocaleFallingBackToItself() =>
        Single(o => o.Text.LocaleFallback["tr"] = ["tr"])
            .Should().Contain("falls back to itself");

    [Fact]
    public void RejectsATwoNodeFallbackCycle() =>
        Single(o =>
        {
            o.Text.LocaleFallback["tr"] = ["en"];
            o.Text.LocaleFallback["en"] = ["tr"];
        }).Should().Contain("LocaleFallback contains a cycle").And.Contain("tr -> en -> tr");

    [Fact]
    public void RejectsALongerFallbackCycle() =>
        Single(o =>
        {
            o.Text.Locales = ["tr", "en", "de"];
            o.Text.LocaleFallback = new Dictionary<string, List<string>>
            {
                ["tr"] = ["en"],
                ["en"] = ["de"],
                ["de"] = ["tr"],
            };
        }).Should().Contain("LocaleFallback contains a cycle");

    [Fact]
    public void AcceptsADiamondFallbackGraphWithNoCycle()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.Text.Locales = ["tr", "en", "de", "fr"];
            o.Text.LocaleFallback = new Dictionary<string, List<string>>
            {
                ["tr"] = ["en", "de"],
                ["en"] = ["fr"],
                ["de"] = ["fr"],
                ["fr"] = [],
            };
        });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void RejectsASynonymKeyOutsideTheDeclaredSet() =>
        Single(o => o.Text.Synonyms["de"] = [new() { Terms = ["a", "b"] }])
            .Should().Contain("Text:Synonyms has key 'de'");

    [Fact]
    public void RejectsASynonymGroupWithFewerThanTwoTerms() =>
        Single(o => o.Text.Synonyms["tr"] = [new() { Terms = ["karekod"] }])
            .Should().Contain("Synonyms['tr'][0]").And.Contain("at least two terms");

    [Fact]
    public void AcceptsAValidSynonymGroup()
    {
        IReadOnlyList<string> errors = Errors(o =>
            o.Text.Synonyms["tr"] = [new() { Terms = ["karekod", "qr kod", "qr"] }]);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void RejectsAModelWithNoName() =>
        Single(o => o.EmbeddingModels = [new() { Provider = "gemini", Dimensions = 768 }])
            .Should().Contain("EmbeddingModels:0:Model must be set");

    [Fact]
    public void RejectsAModelNameThatYieldsNoSlug() =>
        Single(o => o.EmbeddingModels = [new() { Provider = "gemini", Model = "///", Dimensions = 768 }])
            .Should().Contain("yields no usable table slug");

    [Fact]
    public void RejectsTwoModelsSharingATableSlug()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.Providers["gemini"] = new ProviderOptions
            {
                ApiKey = "k",
                BaseUrl = "https://example.test/",
            };
            o.EmbeddingModels =
            [
                new() { Provider = "gemini", Model = "gemini-embedding-001", Dimensions = 768 },
                new() { Provider = "gemini", Model = "gemini.embedding.001", Dimensions = 768 },
            ];
        });

        errors.Should().ContainSingle()
            .Which.Should().Contain("same table slug 'gemini_embedding_001'");
    }

    [Fact]
    public void RejectsNonPositiveDimensions() =>
        Errors(o =>
        {
            o.Providers["gemini"] = new ProviderOptions { ApiKey = "k", BaseUrl = "https://example.test/" };
            o.EmbeddingModels = [new() { Provider = "gemini", Model = "m", Dimensions = 0 }];
        }).Should().Contain(e => e.Contains("EmbeddingModels:0:Dimensions"));

    [Fact]
    public void RejectsAModelWithNoProvider() =>
        Errors(o => o.EmbeddingModels = [new() { Model = "m", Dimensions = 8 }])
            .Should().Contain(e => e.Contains("EmbeddingModels:0:Provider must be set"));

    [Fact]
    public void RejectsAModelNamingAnUnconfiguredProvider() =>
        Single(o => o.EmbeddingModels = [new() { Provider = "cohere", Model = "m", Dimensions = 8 }])
            .Should().Contain("Provider is 'cohere'").And.Contain("none are configured");

    [Fact]
    public void ListsTheKnownProvidersWhenOneIsUnrecognised()
    {
        string error = Single(o =>
        {
            o.Providers["gemini"] = new ProviderOptions { ApiKey = "k", BaseUrl = "https://example.test/" };
            o.Providers["openai"] = new ProviderOptions { ApiKey = "k", BaseUrl = "https://example.test/" };
            o.EmbeddingModels = [new() { Provider = "cohere", Model = "m", Dimensions = 8 }];
        });

        error.Should().Contain("gemini, openai");
    }

    [Fact]
    public void MatchesTheProviderNameCaseInsensitively()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.Providers["Gemini"] = new ProviderOptions { ApiKey = "k", BaseUrl = "https://example.test/" };
            o.EmbeddingModels = [new() { Provider = "gemini", Model = "m", Dimensions = 8 }];
        });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void RejectsAProviderWithNoApiKey()
    {
        string error = Single(o =>
        {
            o.Providers["gemini"] = new ProviderOptions { BaseUrl = "https://example.test/" };
            o.EmbeddingModels = [new() { Provider = "gemini", Model = "m", Dimensions = 8 }];
        });

        error.Should().Contain("Providers:gemini:ApiKey must be set")
            .And.Contain("DocumentEngine__Providers__gemini__ApiKey");
    }

    [Fact]
    public void RejectsAProviderWithNoBaseUrl() =>
        Errors(o =>
        {
            o.Providers["gemini"] = new ProviderOptions { ApiKey = "k" };
            o.EmbeddingModels = [new() { Provider = "gemini", Model = "m", Dimensions = 8 }];
        }).Should().Contain(e => e.Contains("Providers:gemini:BaseUrl must be set"));

    [Theory]
    [InlineData("/v1/embed")]
    [InlineData("example.test")]
    [InlineData("ftp://example.test/")]
    [InlineData("file:///etc/passwd")]
    public void RejectsAProviderBaseUrlThatIsNotHttp(string baseUrl) =>
        Errors(o =>
        {
            o.Providers["gemini"] = new ProviderOptions { ApiKey = "k", BaseUrl = baseUrl };
            o.EmbeddingModels = [new() { Provider = "gemini", Model = "m", Dimensions = 8 }];
        }).Should().Contain(e => e.Contains("must be an absolute http or https URL"));

    [Theory]
    [InlineData("https://api.openai.com/")]
    [InlineData("http://localhost:8080/")]
    public void AcceptsAnHttpProviderBaseUrl(string baseUrl) =>
        Errors(o =>
        {
            o.Providers["gemini"] = new ProviderOptions { ApiKey = "k", BaseUrl = baseUrl };
            o.EmbeddingModels = [new() { Provider = "gemini", Model = "m", Dimensions = 8 }];
        }).Should().BeEmpty();

    [Theory]
    [InlineData("MaxBatch")]
    [InlineData("MaxConcurrentRequests")]
    [InlineData("TimeoutSeconds")]
    public void RejectsNonPositiveProviderLimits(string setting)
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            ProviderOptions provider = new() { ApiKey = "k", BaseUrl = "https://example.test/" };

            switch (setting)
            {
                case "MaxBatch": provider.MaxBatch = 0; break;
                case "MaxConcurrentRequests": provider.MaxConcurrentRequests = 0; break;
                default: provider.TimeoutSeconds = 0; break;
            }

            o.Providers["gemini"] = provider;
            o.EmbeddingModels = [new() { Provider = "gemini", Model = "m", Dimensions = 8 }];
        });

        errors.Should().ContainSingle().Which.Should().Contain($"Providers:gemini:{setting}");
    }

    [Fact]
    public void SkipsProviderChecksForARetiredModel()
    {
        IReadOnlyList<string> errors = Errors(o =>
            o.EmbeddingModels = [new() { Provider = "gone", Model = "m", Dimensions = 8, Retired = true }]);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void RejectsAConfigurationWithNoCredentialMechanism()
    {
        string error = Single(o =>
        {
            o.JwtSymmetricKey = "";
            o.AllowedProjectKeys = "";
        });

        error.Should().Contain("No credential mechanism is configured")
            .And.Contain("JwtSymmetricKey")
            .And.Contain("AllowedProjectKeys");
    }

    [Fact]
    public void AcceptsProjectKeysAsTheOnlyCredentialMechanism()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.JwtSymmetricKey = "";
            o.AllowedProjectKeys = "tenant-a:secret";
        });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void RejectsAShortJwtKey() =>
        Single(o => o.JwtSymmetricKey = new string('k', 31))
            .Should().Contain("JwtSymmetricKey must be at least 32 bytes").And.Contain("got 31");

    [Fact]
    public void CountsJwtKeyLengthInBytesNotCharacters()
    {
        Errors(o => o.JwtSymmetricKey = new string('ü', 16)).Should().BeEmpty();

        Errors(o => o.JwtSymmetricKey = new string('ü', 15))
            .Should().ContainSingle().Which.Should().Contain("got 30");
    }

    [Fact]
    public void RejectsANegativeClockSkew() =>
        Single(o => o.JwtClockSkewSeconds = -1).Should().Contain("JwtClockSkewSeconds");

    [Theory]
    [InlineData("MaxRequestBodyBytes")]
    [InlineData("MaxQueryLength")]
    [InlineData("MaxSearchLimit")]
    [InlineData("MaxBulkDocuments")]
    [InlineData("RateLimitPermitCount")]
    [InlineData("RateLimitWindowSeconds")]
    public void RejectsNonPositiveLimits(string setting)
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            switch (setting)
            {
                case "MaxRequestBodyBytes": o.MaxRequestBodyBytes = 0; break;
                case "MaxQueryLength": o.MaxQueryLength = 0; break;
                case "MaxSearchLimit": o.MaxSearchLimit = 0; break;
                case "MaxBulkDocuments": o.MaxBulkDocuments = 0; break;
                case "RateLimitPermitCount": o.RateLimitPermitCount = 0; break;
                default: o.RateLimitWindowSeconds = 0; break;
            }
        });

        errors.Should().ContainSingle().Which.Should().Contain($"DocumentEngine:{setting}");
    }

    [Fact]
    public void RejectsANegativeRateLimitQueueSize() =>
        Single(o => o.RateLimitQueueSize = -1).Should().Contain("RateLimitQueueSize");

    [Fact]
    public void RejectsANonPositiveDatabaseCommandTimeout() =>
        Single(o => o.DatabaseCommandTimeoutSeconds = 0)
            .Should().Contain("DatabaseCommandTimeoutSeconds");

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.1)]
    public void RejectsAnOutOfRangeCircuitBreakerRatio(double ratio) =>
        Single(o => o.Resilience.CircuitBreakerFailureRatio = ratio)
            .Should().Contain("CircuitBreakerFailureRatio");

    [Fact]
    public void RejectsANonPositiveSamplingWindow() =>
        Single(o => o.Resilience.CircuitBreakerSamplingSeconds = 0)
            .Should().Contain("CircuitBreakerSamplingSeconds");

    [Fact]
    public void RejectsAMinimumThroughputBelowTwo() =>
        Single(o => o.Resilience.CircuitBreakerMinimumThroughput = 1)
            .Should().Contain("CircuitBreakerMinimumThroughput");

    [Fact]
    public void RejectsANonPositiveBreakerDuration() =>
        Single(o => o.Resilience.CircuitBreakerDurationSeconds = 0)
            .Should().Contain("CircuitBreakerDurationSeconds");

    [Fact]
    public void RejectsNegativeRetries() =>
        Single(o => o.Resilience.MaxRetries = -1).Should().Contain("Resilience:MaxRetries");

    [Fact]
    public void RejectsANonPositiveBackoffCeiling() =>
        Single(o => o.Resilience.RetryBackoffCeilingMs = 0)
            .Should().Contain("RetryBackoffCeilingMs");

    [Theory]
    [InlineData("IntervalSeconds")]
    [InlineData("BatchSize")]
    [InlineData("MaxAttempts")]
    public void RejectsNonPositiveBacklogWorkerSettings(string setting)
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            switch (setting)
            {
                case "IntervalSeconds": o.BacklogWorker.IntervalSeconds = 0; break;
                case "BatchSize": o.BacklogWorker.BatchSize = 0; break;
                default: o.BacklogWorker.MaxAttempts = 0; break;
            }
        });

        errors.Should().ContainSingle().Which.Should().Contain($"BacklogWorker:{setting}");
    }

    [Fact]
    public void SkipsBacklogWorkerChecksWhenItIsDisabled()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.BacklogWorker.Enabled = false;
            o.BacklogWorker.IntervalSeconds = 0;
            o.BacklogWorker.BatchSize = -5;
            o.BacklogWorker.MaxAttempts = 0;
        });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void EveryErrorNamesItsConfigurationPath()
    {
        DocumentEngineOptions options = new();

        IReadOnlyList<string> errors = DocumentEngineOptionsValidator.Validate(options);

        errors.Should().NotBeEmpty();
        errors.Should().OnlyContain(e => e.Contains("DocumentEngine") || e.Contains("credential mechanism"));
    }

    [Fact]
    public void ADefaultConstructedConfigurationIsRejected()
    {
        Action act = () => DocumentEngineOptionsValidator.ThrowIfInvalid(new DocumentEngineOptions());

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Text:Locales").And.Contain("credential mechanism");
    }

    [Fact]
    public void ErrorsAreDistinctSoNoCheckIsRegisteredTwice()
    {
        IReadOnlyList<string> errors = DocumentEngineOptionsValidator.Validate(new DocumentEngineOptions());

        errors.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void DoubleFormattingDoesNotDependOnAmbientCulture()
    {
        string error = Single(o => o.Text.TrigramThreshold = 1.5);

        error.Should().Contain("1.5").And.NotContain("1,5");
    }
}
