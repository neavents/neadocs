namespace Neadocs.Engine.Tests.Unit.Text;

using FluentAssertions;
using Neadocs.Engine.Infrastructure.Text;

/// <summary>
/// The truncated lexemes, and the settings that produce them.
/// </summary>
/// <remarks>
/// A stemmer answers "what is the root of this word". For a language that stacks suffixes without
/// limit, no stemmer answers it consistently — snowball reduces some surface forms of a word to the
/// same lexeme and others to something shorter or unrelated. What <i>is</i> consistent in a
/// suffixing language is that the root is a prefix, so a fixed-length truncation gives two inflected
/// forms of the same word somewhere to meet.
/// <para>
/// These tests use a bare pipeline rather than the shipped rule sets, so they describe the
/// mechanism rather than one language's configuration of it.
/// </para>
/// </remarks>
public sealed class StemPrefixTests
{
    private static CompiledPipeline Pipeline(int prefixLength, string searchConfig = "simple") =>
        new(
            "test",
            "hash",
            [],
            [],
            searchConfig,
            prefixLength);

    [Fact]
    public void TwoInflectionsOfTheSameStemProduceTheSamePrefix()
    {
        CompiledPipeline pipeline = Pipeline(4);

        // The pair that started this: neither snowball nor an exact match connects them, because
        // one has been stemmed to something shorter than the other.
        pipeline.Prefixes("menumu").Should().Be("menu");
        pipeline.Prefixes("menuyu").Should().Be("menu");
        pipeline.Prefixes("menulerimiz").Should().Be("menu");
    }

    [Fact]
    public void ItSkipsTokensNoLongerThanThePrefix()
    {
        // Emitting them would repeat, at weight D, a lexeme the full-weight vector already carries.
        // That only dilutes ranking — it cannot add a match.
        Pipeline(4).Prefixes("bir iki menu menular").Should().Be("menu");
    }

    [Fact]
    public void ItEmitsEachDistinctPrefixOnce()
    {
        // Five words sharing a stem should not weight that stem five times over.
        Pipeline(4).Prefixes("yayinlama yayinlarim yayinladi").Should().Be("yayi");
    }

    [Fact]
    public void ItIsOffByDefault()
    {
        CompiledPipeline pipeline = Pipeline(0);

        pipeline.EmitsPrefixes.Should().BeFalse();
        pipeline.Prefixes("menumu yayinlarim").Should().BeEmpty();
    }

    [Fact]
    public void ItHandlesEmptyAndWhitespaceInput()
    {
        Pipeline(4).Prefixes(null).Should().BeEmpty();
        Pipeline(4).Prefixes("   ").Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(-1)]
    public void AnOutOfRangePrefixLengthIsRefusedAtLoad(int length)
    {
        RuleSet ruleSet = MinimalRuleSet();
        ruleSet.StemPrefixLength = length;

        Action compile = () => PipelineCompiler.Compile(ruleSet, "test");

        compile.Should().Throw<RuleSetException>().WithMessage("*stemPrefixLength*");
    }

    [Fact]
    public void AnUnknownSearchConfigShapeIsRefusedAtLoad()
    {
        // Not the injection boundary — the value is always a parameter cast to regconfig. This is
        // so a typo fails where the file is read, rather than on every search afterwards with an
        // error naming a type cast.
        RuleSet ruleSet = MinimalRuleSet();
        ruleSet.SearchConfig = "turkish; DROP TABLE chunks";

        Action compile = () => PipelineCompiler.Compile(ruleSet, "test");

        compile.Should().Throw<RuleSetException>().WithMessage("*searchConfig*");
    }

    [Fact]
    public void AMissingSearchConfigMeansSimple()
    {
        CompiledPipeline compiled = PipelineCompiler.Compile(MinimalRuleSet(), "test");

        compiled.SearchConfig.Should().Be(RuleOperations.DefaultSearchConfig);
        compiled.StemPrefixLength.Should().Be(0);
    }

    [Fact]
    public void ChangingEitherSettingChangesTheHash()
    {
        // `normalizer_hash` is the only thing that reports a chunk as stale. A setting that changes
        // the indexed vector without changing the hash leaves the column holding two incompatible
        // schemes with nothing anywhere saying so.
        RuleSet plain = MinimalRuleSet();
        RuleSet stemmed = MinimalRuleSet();
        stemmed.SearchConfig = "turkish";
        RuleSet truncated = MinimalRuleSet();
        truncated.StemPrefixLength = 4;

        string plainHash = PipelineCompiler.Compile(plain, "test").Hash;

        PipelineCompiler.Compile(stemmed, "test").Hash.Should().NotBe(plainHash);
        PipelineCompiler.Compile(truncated, "test").Hash.Should().NotBe(plainHash);
        PipelineCompiler.Compile(stemmed, "test").Hash
            .Should().NotBe(PipelineCompiler.Compile(truncated, "test").Hash);
    }

    [Fact]
    public void ARuleSetNamingNeitherKeepsTheHashItAlreadyHad()
    {
        // Adopting this change must not mark every chunk in every locale stale — only the locales
        // whose indexing actually moved.
        RuleSet ruleSet = MinimalRuleSet();

        PipelineCompiler.Compile(ruleSet, "test").Hash
            .Should().Be(PipelineHash.Of(ruleSet.Pipeline));
    }

    private static RuleSet MinimalRuleSet() => new()
    {
        Tag = "zz",
        Pipeline = [new RuleOperation { Op = RuleOperations.CollapseWhitespace }],
        SelfTest =
        [
            new SelfTestCase { Input = "a  b", Expected = "a b" },
            new SelfTestCase { Input = " c ", Expected = "c" },
            new SelfTestCase { Input = "d", Expected = "d" },
        ],
    };
}
