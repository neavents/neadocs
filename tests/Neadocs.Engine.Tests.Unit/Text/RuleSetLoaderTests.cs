namespace Neadocs.Engine.Tests.Unit.Text;

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Text;

public sealed class RuleSetLoaderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "neadocs-rules-" + Guid.NewGuid().ToString("N")[..8]);

    public RuleSetLoaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void Write(string name, string json) =>
        File.WriteAllText(Path.Combine(_directory, name), json);

    private const string ValidTurkish = """
        {
          "tag": "tr",
          "pipeline": [ { "op": "mapChars", "map": { "X": "Y" } } ],
          "selfTest": [
            { "in": "X", "out": "Y" },
            { "in": "a", "out": "a" },
            { "in": "XX", "out": "YY" }
          ]
        }
        """;

    [Fact]
    public void LoadsTheEmbeddedDefaultsWhenNoDirectoryIsGiven()
    {
        IReadOnlyDictionary<string, LoadedRuleSet> loaded = RuleSetLoader.Load(directory: null);

        loaded.Should().ContainKeys("*", "tr", "ar", "fa", "he");
        loaded.Values.Should().OnlyContain(l => !l.FromFile);
    }

    [Fact]
    public void TolerantesAMissingDirectory()
    {
        IReadOnlyDictionary<string, LoadedRuleSet> loaded =
            RuleSetLoader.Load(Path.Combine(_directory, "does-not-exist"));

        loaded.Should().ContainKey("*");
    }

    [Fact]
    public void AFileOverridesTheEmbeddedDefaultOfTheSameTag()
    {
        Write("tr.json", ValidTurkish);

        IReadOnlyDictionary<string, LoadedRuleSet> loaded = RuleSetLoader.Load(_directory);

        loaded["tr"].FromFile.Should().BeTrue();
        loaded["tr"].Pipeline.Normalize("X").Should().Be("Y");
        loaded["tr"].Pipeline.Normalize("İSTANBUL").Should().NotBe("istanbul");
    }

    [Fact]
    public void OverridingOneTagLeavesTheOthersUntouched()
    {
        Write("tr.json", ValidTurkish);

        IReadOnlyDictionary<string, LoadedRuleSet> loaded = RuleSetLoader.Load(_directory);

        loaded["*"].FromFile.Should().BeFalse();
        loaded["*"].Pipeline.Normalize("Café").Should().Be("cafe");
    }

    [Fact]
    public void AddsAnEntirelyNewLanguageFromADroppedFile()
    {
        Write("az.json", """
            {
              "tag": "az",
              "pipeline": [ { "op": "mapChars", "map": { "Ə": "e" } } ],
              "selfTest": [
                { "in": "Ə", "out": "e" },
                { "in": "ƏƏ", "out": "ee" },
                { "in": "x", "out": "x" }
              ]
            }
            """);

        IReadOnlyDictionary<string, LoadedRuleSet> loaded = RuleSetLoader.Load(_directory);

        loaded.Should().ContainKey("az");
        loaded["az"].Pipeline.Normalize("Ə").Should().Be("e");
    }

    [Fact]
    public void TheFileNameDoesNotHaveToMatchTheTag()
    {
        Write("anything.json", ValidTurkish);

        RuleSetLoader.Load(_directory)["tr"].FromFile.Should().BeTrue();
    }

    [Fact]
    public void RefusesTwoFilesClaimingTheSameTag()
    {
        Write("one.json", ValidTurkish);
        Write("two.json", ValidTurkish);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should().Contain("already defined by");
    }

    [Fact]
    public void RefusesAFileUsingAnUndeclaredOperation()
    {
        Write("bad.json", """
            {
              "tag": "zz",
              "pipeline": [ { "op": "regexReplace", "map": { "a": "b" } } ],
              "selfTest": [
                { "in": "a", "out": "b" },
                { "in": "b", "out": "b" },
                { "in": "c", "out": "c" }
              ]
            }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should()
                .Contain("regexReplace")
                .And.Contain("not a declared operation")
                .And.Contain("bad.json");
    }

    [Fact]
    public void RefusesAFileWithNoSelfTestBlock()
    {
        Write("bad.json", """
            {
              "tag": "zz",
              "pipeline": [ { "op": "collapseWhitespace" } ],
              "selfTest": []
            }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should().Contain("at least 3 cases");
    }

    [Fact]
    public void RefusesAFileWithTooFewSelfTests()
    {
        Write("bad.json", """
            {
              "tag": "zz",
              "pipeline": [ { "op": "collapseWhitespace" } ],
              "selfTest": [ { "in": "a  b", "out": "a b" }, { "in": "c", "out": "c" } ]
            }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should().Contain("found 2");
    }

    [Fact]
    public void RefusesAFileWhoseSelfTestDoesNotHold()
    {
        Write("bad.json", """
            {
              "tag": "zz",
              "pipeline": [ { "op": "mapChars", "map": { "a": "b" } } ],
              "selfTest": [
                { "in": "a", "out": "WRONG" },
                { "in": "b", "out": "b" },
                { "in": "c", "out": "c" }
              ]
            }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should()
                .Contain("self-test failed")
                .And.Contain("'a' produced 'b', expected 'WRONG'");
    }

    [Fact]
    public void ReportsEverySelfTestFailureNotJustTheFirst()
    {
        Write("bad.json", """
            {
              "tag": "zz",
              "pipeline": [ { "op": "mapChars", "map": { "a": "b" } } ],
              "selfTest": [
                { "in": "a", "out": "X" },
                { "in": "aa", "out": "Y" },
                { "in": "aaa", "out": "Z" }
              ]
            }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should().Contain("3 self-tests failed");
    }

    [Fact]
    public void NamesTheFileAndPositionForMalformedJson()
    {
        Write("bad.json", "{ \"tag\": \"zz\", \"pipeline\": [ }");

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should().Contain("bad.json").And.Contain("invalid JSON at line");
    }

    [Fact]
    public void RefusesAFileWithNoTag()
    {
        Write("bad.json", """
            { "pipeline": [ { "op": "collapseWhitespace" } ], "selfTest": [] }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should().Contain("'tag' must be set");
    }

    [Fact]
    public void RefusesAFileWithAnEmptyPipeline()
    {
        Write("bad.json", """
            {
              "tag": "zz",
              "pipeline": [],
              "selfTest": [
                { "in": "a", "out": "a" }, { "in": "b", "out": "b" }, { "in": "c", "out": "c" }
              ]
            }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should().Contain("at least one operation");
    }

    [Fact]
    public void RefusesAMalformedTag()
    {
        Write("bad.json", """
            {
              "tag": "not a locale",
              "pipeline": [ { "op": "collapseWhitespace" } ],
              "selfTest": [
                { "in": "a", "out": "a" }, { "in": "b", "out": "b" }, { "in": "c", "out": "c" }
              ]
            }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should().Contain("well-formed BCP-47");
    }

    [Fact]
    public void RefusesStrippingTheFormatCategoryBecauseItWouldCorruptPersian()
    {
        Write("bad.json", """
            {
              "tag": "zz",
              "pipeline": [ { "op": "stripUnicodeCategory", "categories": ["Format"] } ],
              "selfTest": [
                { "in": "a", "out": "a" }, { "in": "b", "out": "b" }, { "in": "c", "out": "c" }
              ]
            }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should()
                .Contain("ZERO WIDTH NON-JOINER")
                .And.Contain("meaningful in Persian");
    }

    [Fact]
    public void RefusesNormalizeFormBecauseThisRuntimeCannotPerformIt()
    {
        Write("bad.json", """
            {
              "tag": "zz",
              "pipeline": [ { "op": "normalizeForm", "form": "FormD" } ],
              "selfTest": [
                { "in": "a", "out": "a" }, { "in": "b", "out": "b" }, { "in": "c", "out": "c" }
              ]
            }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should()
                .Contain("silently do nothing")
                .And.Contain("mapChars");
    }

    [Fact]
    public void RefusesAMapCharsKeyLongerThanOneCharacter()
    {
        Write("bad.json", """
            {
              "tag": "zz",
              "pipeline": [ { "op": "mapChars", "map": { "ch": "c" } } ],
              "selfTest": [
                { "in": "a", "out": "a" }, { "in": "b", "out": "b" }, { "in": "c", "out": "c" }
              ]
            }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should().Contain("mapSequences");
    }

    [Fact]
    public void RefusesAnUnknownUnicodeCategory()
    {
        Write("bad.json", """
            {
              "tag": "zz",
              "pipeline": [ { "op": "stripUnicodeCategory", "categories": ["Squiggle"] } ],
              "selfTest": [
                { "in": "a", "out": "a" }, { "in": "b", "out": "b" }, { "in": "c", "out": "c" }
              ]
            }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should().Contain("not a Unicode category");
    }

    [Fact]
    public void RefusesAnUnknownLowercaseMode()
    {
        Write("bad.json", """
            {
              "tag": "zz",
              "pipeline": [ { "op": "lowercase", "mode": "turkish" } ],
              "selfTest": [
                { "in": "a", "out": "a" }, { "in": "b", "out": "b" }, { "in": "c", "out": "c" }
              ]
            }
            """);

        Action act = () => RuleSetLoader.Load(_directory);

        act.Should().Throw<RuleSetException>()
            .Which.Message.Should().Contain("expected 'ascii' or 'invariant'");
    }

    [Fact]
    public void IgnoresNonJsonFilesInTheDirectory()
    {
        Write("notes.txt", "this is not a rule set");
        Write("tr.json", ValidTurkish);

        RuleSetLoader.Load(_directory)["tr"].FromFile.Should().BeTrue();
    }

    [Fact]
    public void ChangingAPipelineChangesItsHash()
    {
        Write("tr.json", ValidTurkish);
        string before = RuleSetLoader.Load(_directory)["tr"].Pipeline.Hash;

        Write("tr.json", ValidTurkish.Replace("Y", "Z"));
        string after = RuleSetLoader.Load(_directory)["tr"].Pipeline.Hash;

        after.Should().NotBe(before,
            "any edit to a pipeline must invalidate the chunks it built, with nobody having to "
            + "remember to bump a version number");
    }

    [Fact]
    public void ReorderingOperationsChangesTheHash()
    {
        RuleOperation a = new() { Op = RuleOperations.Lowercase, Mode = "ascii" };
        RuleOperation b = new() { Op = RuleOperations.CollapseWhitespace };

        PipelineHash.Of([a, b]).Should().NotBe(PipelineHash.Of([b, a]));
    }

    [Fact]
    public void MapKeyOrderDoesNotChangeTheHash()
    {
        RuleOperation forward = new()
        {
            Op = RuleOperations.MapChars,
            Map = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" },
        };
        RuleOperation reverse = new()
        {
            Op = RuleOperations.MapChars,
            Map = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" },
        };

        PipelineHash.Of([forward]).Should().Be(PipelineHash.Of([reverse]));
    }

    [Fact]
    public void EditingOnlyTheDescriptionOrSelfTestDoesNotChangeTheHash()
    {
        Write("tr.json", ValidTurkish);
        string before = RuleSetLoader.Load(_directory)["tr"].Pipeline.Hash;

        Write("tr.json", """
            {
              "tag": "tr",
              "description": "a description that did not exist before",
              "pipeline": [ { "op": "mapChars", "map": { "X": "Y" } } ],
              "selfTest": [
                { "in": "X", "out": "Y" },
                { "in": "a", "out": "a" },
                { "in": "XX", "out": "YY" },
                { "in": "XXX", "out": "YYY" }
              ]
            }
            """);

        RuleSetLoader.Load(_directory)["tr"].Pipeline.Hash.Should().Be(before,
            "improving a comment or adding a test must not invalidate a built index");
    }
}
