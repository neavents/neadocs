namespace Neadocs.Engine.Tests.Unit.Text;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Text;

public sealed class PipelineOrderTests
{
    private static RuleSet CaseFoldingSet(bool lowercaseFirst)
    {
        RuleOperation lowercase = new() { Op = RuleOperations.Lowercase, Mode = "invariant" };
        RuleOperation fold = new()
        {
            Op = RuleOperations.MapChars,
            Map = new Dictionary<string, string> { ["é"] = "e", ["ö"] = "o" },
        };

        return new RuleSet
        {
            Tag = "zz",
            Pipeline = lowercaseFirst ? [lowercase, fold] : [fold, lowercase],
            SelfTest =
            [
                new SelfTestCase { Input = "ÉCOLE", Expected = "ecole" },
                new SelfTestCase { Input = "école", Expected = "ecole" },
                new SelfTestCase { Input = "GÖZ", Expected = "goz" },
            ],
        };
    }

    [Fact]
    public void TheCorrectOrderCompilesAndPassesItsOwnProof()
    {
        CompiledPipeline pipeline = PipelineCompiler.Compile(CaseFoldingSet(lowercaseFirst: true), "test");

        pipeline.Normalize("ÉCOLE").Should().Be("ecole");
        pipeline.Normalize("GÖZ").Should().Be("goz");
    }

    [Fact]
    public void ReversingTheOrderIsRejectedByTheSelfTest()
    {
        Action act = () => PipelineCompiler.Compile(CaseFoldingSet(lowercaseFirst: false), "test");

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should()
                .Contain("self-test")
                .And.Contain("ÉCOLE")
                .And.Contain("école")
                .And.Contain("ecole");
    }

    [Fact]
    public void FoldingBeforeCasingSilentlyMissesEveryUppercaseAccent()
    {
        RuleSet wrong = CaseFoldingSet(lowercaseFirst: false);
        wrong.SelfTest = [];

        Action act = () => PipelineCompiler.Compile(wrong, "test");

        act.Should().Throw<RuleSetException>().Which.Message.Should().Contain("selfTest");
    }

    [Fact]
    public void AnAsciiFoldingPipelineIsGenuinelyOrderInsensitiveForDottedI()
    {
        RuleOperation mapI = new()
        {
            Op = RuleOperations.MapChars,
            Map = new Dictionary<string, string> { ["I"] = "ı", ["İ"] = "i" },
        };
        RuleOperation lower = new() { Op = RuleOperations.Lowercase, Mode = "ascii" };
        RuleOperation foldToAscii = new()
        {
            Op = RuleOperations.MapChars,
            Map = new Dictionary<string, string> { ["ı"] = "i" },
        };

        SelfTestCase[] tests =
        [
            new() { Input = "IST", Expected = "ist" },
            new() { Input = "İSTANBUL", Expected = "istanbul" },
            new() { Input = "ILIK", Expected = "ilik" },
        ];

        CompiledPipeline mapFirst = PipelineCompiler.Compile(
            new RuleSet { Tag = "zz", Pipeline = [mapI, lower, foldToAscii], SelfTest = [.. tests] },
            "map-first");

        CompiledPipeline lowerFirst = PipelineCompiler.Compile(
            new RuleSet { Tag = "zz", Pipeline = [lower, mapI, foldToAscii], SelfTest = [.. tests] },
            "lower-first");

        foreach (SelfTestCase test in tests)
        {
            mapFirst.Normalize(test.Input).Should().Be(lowerFirst.Normalize(test.Input),
                "a pipeline that ends by folding ı to i erases the distinction the ordering "
                + "was protecting, so both orders converge — the ordering only matters while "
                + "the distinction still exists downstream");
        }
    }

    [Fact]
    public void OrderIsObservableTheMomentTheAsciiFoldIsRemoved()
    {
        RuleOperation mapI = new()
        {
            Op = RuleOperations.MapChars,
            Map = new Dictionary<string, string> { ["I"] = "ı", ["İ"] = "i" },
        };
        RuleOperation lower = new() { Op = RuleOperations.Lowercase, Mode = "ascii" };

        SelfTestCase[] tests =
        [
            new() { Input = "IST", Expected = "ıst" },
            new() { Input = "İSTANBUL", Expected = "istanbul" },
            new() { Input = "KIZ", Expected = "kız" },
        ];

        CompiledPipeline correct = PipelineCompiler.Compile(
            new RuleSet { Tag = "zz", Pipeline = [mapI, lower], SelfTest = [.. tests] },
            "correct");

        correct.Normalize("IST").Should().Be("ıst");

        Action reversed = () => PipelineCompiler.Compile(
            new RuleSet { Tag = "zz", Pipeline = [lower, mapI], SelfTest = [.. tests] },
            "reversed");

        reversed.Should().Throw<RuleSetException>()
            .Which.Message.Should().Contain("'IST' produced 'ist', expected 'ıst'");
    }

    [Fact]
    public void LigatureExpansionMustPrecedeAnyPerCharacterFold()
    {
        RuleOperation ligature = new()
        {
            Op = RuleOperations.MapChars,
            Map = new Dictionary<string, string> { ["ß"] = "ss" },
        };
        RuleOperation strip = new()
        {
            Op = RuleOperations.StripUnicodeCategory,
            Categories = ["LowercaseLetter"],
        };

        CompiledPipeline expandFirst = PipelineCompiler.Compile(
            new RuleSet
            {
                Tag = "zz",
                Pipeline = [ligature, strip],
                SelfTest =
                [
                    new() { Input = "ß", Expected = "" },
                    new() { Input = "AB", Expected = "AB" },
                    new() { Input = "aß", Expected = "" },
                ],
            },
            "expand-first");

        expandFirst.Normalize("ß").Should().BeEmpty();
    }
}
