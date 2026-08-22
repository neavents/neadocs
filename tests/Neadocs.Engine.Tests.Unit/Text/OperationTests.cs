namespace Neadocs.Engine.Tests.Unit.Text;

using System.Collections.Generic;
using System.Globalization;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Text;

public sealed class MapCharsOperationTests
{
    private static MapCharsOperation Op(params (char From, string To)[] pairs)
    {
        Dictionary<char, string> map = [];

        foreach ((char from, string to) in pairs)
        {
            map[from] = to;
        }

        return new MapCharsOperation(map);
    }

    [Fact]
    public void ReplacesASingleCharacter() =>
        Op(('a', 'b'.ToString())).Apply("banana").Should().Be("bbnbnb");

    [Fact]
    public void ExpandsOneCharacterIntoSeveral() =>
        Op(('ß', "ss")).Apply("Straße").Should().Be("Strasse");

    [Fact]
    public void RemovesACharacterWhenTheTargetIsEmpty() =>
        Op(('-', "")).Apply("a-b-c").Should().Be("abc");

    [Fact]
    public void ReturnsTheSameInstanceWhenNothingMatches()
    {
        const string input = "untouched";

        Op(('z', "y")).Apply(input).Should().BeSameAs(input);
    }

    [Fact]
    public void HandlesAsciiAndNonAsciiInOneMap() =>
        Op(('a', "1"), ('ş', "2")).Apply("aşa").Should().Be("121");

    [Fact]
    public void AppliesEachCharacterOnceAndDoesNotCascade() =>
        Op(('a', "b"), ('b', "c")).Apply("ab").Should().Be("bc");

    [Fact]
    public void HandlesAnEmptyInput() =>
        Op(('a', "b")).Apply("").Should().BeEmpty();

    [Fact]
    public void ReplacesAtTheVeryStartAndVeryEnd() =>
        Op(('x', "-")).Apply("xmiddlex").Should().Be("-middle-");

    [Fact]
    public void IsNamedForItsOperation() =>
        Op(('a', "b")).Name.Should().Be(RuleOperations.MapChars);
}

public sealed class MapSequencesOperationTests
{
    private static MapSequencesOperation Op(params (string From, string To)[] pairs)
    {
        Dictionary<string, string> map = [];

        foreach ((string from, string to) in pairs)
        {
            map[from] = to;
        }

        return new MapSequencesOperation(map);
    }

    [Fact]
    public void ReplacesAMultiCharacterSequence() =>
        Op(("ch", "č")).Apply("chata").Should().Be("čata");

    [Fact]
    public void PrefersTheLongestMatchAtAGivenPosition() =>
        Op(("a", "1"), ("abc", "3"), ("ab", "2")).Apply("abc").Should().Be("3");

    [Fact]
    public void MatchesLeftToRightWithoutOverlapping() =>
        Op(("aa", "X")).Apply("aaaa").Should().Be("XX");

    [Fact]
    public void LeavesAnUnmatchedTailAlone() =>
        Op(("aa", "X")).Apply("aaa").Should().Be("Xa");

    [Fact]
    public void ReturnsTheSameInstanceWhenNothingMatches()
    {
        const string input = "untouched";

        Op(("zz", "y")).Apply(input).Should().BeSameAs(input);
    }

    [Fact]
    public void HandlesAnEmptyMap() =>
        new MapSequencesOperation(new Dictionary<string, string>()).Apply("abc").Should().Be("abc");

    [Fact]
    public void RemovesASequenceWhenTheTargetIsEmpty() =>
        Op(("--", "")).Apply("a--b").Should().Be("ab");

    [Fact]
    public void DoesNotRescanItsOwnOutput() =>
        Op(("ab", "b")).Apply("aab").Should().Be("ab");
}

public sealed class LowercaseOperationTests
{
    [Theory]
    [InlineData("ABC", "abc")]
    [InlineData("MiXeD", "mixed")]
    [InlineData("abc", "abc")]
    [InlineData("123", "123")]
    public void AsciiModeLowercasesLatinLetters(string input, string expected) =>
        new LowercaseOperation(asciiOnly: true).Apply(input).Should().Be(expected);

    [Theory]
    [InlineData("ÉCOLE", "École")]
    [InlineData("ŞİFRE", "Şİfre")]
    [InlineData("ПРИВЕТ", "ПРИВЕТ")]
    public void AsciiModeLowercasesOnlyTheAsciiLettersAndLeavesTheRest(string input, string expected) =>
        new LowercaseOperation(asciiOnly: true).Apply(input).Should().Be(expected);

    [Theory]
    [InlineData("ÉCOLE", "école")]
    [InlineData("ПРИВЕТ", "привет")]
    [InlineData("ABC", "abc")]
    public void InvariantModeLowercasesBeyondAscii(string input, string expected) =>
        new LowercaseOperation(asciiOnly: false).Apply(input).Should().Be(expected);

    [Fact]
    public void AsciiModeReturnsTheSameInstanceWhenNothingChanges()
    {
        const string input = "already lower";

        new LowercaseOperation(asciiOnly: true).Apply(input).Should().BeSameAs(input);
    }

    [Fact]
    public void AsciiModeDoesNotTouchTurkishDottedCapital() =>
        new LowercaseOperation(asciiOnly: true).Apply("İ").Should().Be("İ");

    [Fact]
    public void HandlesAnEmptyInput() =>
        new LowercaseOperation(asciiOnly: true).Apply("").Should().BeEmpty();
}

public sealed class StripUnicodeCategoryOperationTests
{
    [Fact]
    public void RemovesCombiningMarks()
    {
        StripUnicodeCategoryOperation op = new([UnicodeCategory.NonSpacingMark]);
        string decomposed = "Cafe" + '\u0301';

        op.Apply(decomposed).Should().Be("Cafe");
    }

    [Fact]
    public void LeavesPrecomposedCharactersAloneBecauseTheyCarryNoSeparateMark()
    {
        StripUnicodeCategoryOperation op = new([UnicodeCategory.NonSpacingMark]);
        string precomposed = "Caf" + '\u00e9';

        op.Apply(precomposed).Should().Be(precomposed,
            "this is why the '*' set folds explicitly: with no working decomposition step there "
            + "is no mark to strip");
    }

    [Fact]
    public void RemovesArabicHarakat()
    {
        StripUnicodeCategoryOperation op = new([UnicodeCategory.NonSpacingMark]);

        op.Apply("مَدْرَسَة").Should().Be("مدرسة");
    }

    [Fact]
    public void DoesNotRemoveTatweelWhichIsAModifierLetter()
    {
        StripUnicodeCategoryOperation op = new([UnicodeCategory.NonSpacingMark]);

        op.Apply("الــقائمة").Should().Contain("ـ");
    }

    [Fact]
    public void ReturnsTheSameInstanceWhenNothingMatches()
    {
        const string input = "plain";
        StripUnicodeCategoryOperation op = new([UnicodeCategory.NonSpacingMark]);

        op.Apply(input).Should().BeSameAs(input);
    }

    [Fact]
    public void AnEmptyCategoryListIsAPassThrough()
    {
        const string input = "Café";

        new StripUnicodeCategoryOperation([]).Apply(input).Should().BeSameAs(input);
    }

    [Fact]
    public void CanStripSeveralCategoriesAtOnce()
    {
        StripUnicodeCategoryOperation op =
            new([UnicodeCategory.DecimalDigitNumber, UnicodeCategory.SpaceSeparator]);

        op.Apply("a1 b2").Should().Be("ab");
    }
}

public sealed class CollapseWhitespaceOperationTests
{
    private static readonly CollapseWhitespaceOperation Op = new();

    [Theory]
    [InlineData("a  b", "a b")]
    [InlineData("a\t\tb", "a b")]
    [InlineData("a\n\nb", "a b")]
    [InlineData("a \t\n b", "a b")]
    [InlineData("  leading", "leading")]
    [InlineData("trailing  ", "trailing")]
    [InlineData("  both  ", "both")]
    [InlineData("a b c", "a b c")]
    public void CollapsesRunsAndTrimsEnds(string input, string expected) =>
        Op.Apply(input).Should().Be(expected);

    [Fact]
    public void ReturnsTheSameInstanceWhenNothingChanges()
    {
        const string input = "already clean";

        Op.Apply(input).Should().BeSameAs(input);
    }

    [Fact]
    public void CollapsesNonBreakingSpace() =>
        Op.Apply("a  b").Should().Be("a b");

    [Fact]
    public void ReducesWhitespaceOnlyInputToEmpty() =>
        Op.Apply("   \t\n  ").Should().BeEmpty();

    [Fact]
    public void HandlesAnEmptyInput() =>
        Op.Apply("").Should().BeEmpty();
}
