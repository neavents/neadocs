namespace Neadocs.Engine.Tests.Unit.Guards;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Neadocs.Tests.Shared;

public sealed class SourceGuardTests
{
    private const string TextNamespaceDirectory = "Infrastructure/Text/";

    private static readonly string[] CultureSensitiveCasingCalls = ["ToLower(", "ToUpper("];

    private static readonly string[] ConsumerDomainWords =
    [
        "venue",
        "venues",
        "menu",
        "menus",
        "org",
        "orgs",
        "smartmenu",
        "restaurant",
        "restaurants",
        "diner",
        "waiter",
        "qrmenu",
    ];

    private static readonly string[] LanguageNames =
    [
        "turkish",
        "english",
        "arabic",
        "persian",
        "farsi",
        "hebrew",
        "german",
        "french",
        "spanish",
        "italian",
        "portuguese",
        "russian",
        "chinese",
        "japanese",
        "korean",
        "dutch",
        "polish",
        "greek",
        "azerbaijani",
        "kurdish",
        "ukrainian",
        "hindi",
        "urdu",
    ];

    [Fact]
    public void TheEngineSourceTreeIsFound()
    {
        Directory.Exists(SourceTree.EngineSource).Should().BeTrue();
        SourceTree.EngineFiles().Should().NotBeEmpty();
    }

    [Fact]
    public void CultureSensitiveCasingAppearsOnlyInTheTextLayer()
    {
        List<string> offenders = [];

        foreach (string path in SourceTree.EngineFiles())
        {
            string relative = SourceTree.RelativeToEngine(path);

            if (relative.StartsWith(TextNamespaceDirectory, StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(path);

            foreach (string call in CultureSensitiveCasingCalls)
            {
                if (source.Contains(call, StringComparison.Ordinal))
                {
                    offenders.Add($"{relative} contains {call}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "InvariantGlobalization removes tr-TR, so casing must be performed from declared rules "
            + "in Infrastructure/Text rather than delegated to the runtime");
    }

    [Fact]
    public void NoSourceFileIsNamedAfterALanguage()
    {
        List<string> offenders = [];

        foreach (string path in SourceTree.EngineFiles())
        {
            string fileName = Path.GetFileNameWithoutExtension(path);

            foreach (string language in LanguageNames)
            {
                if (ContainsWord(SplitIdentifier(fileName), language))
                {
                    offenders.Add($"{SourceTree.RelativeToEngine(path)} is named after {language}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a language is a rule file, not a type — a class named after one is the same mistake "
            + "as a column named after a consumer's domain");
    }

    [Fact]
    public void NoDeclaredTypeIsNamedAfterALanguage()
    {
        List<string> offenders = [];

        foreach (string path in SourceTree.EngineFiles())
        {
            foreach (string identifier in DeclaredTypeNames(File.ReadAllLines(path)))
            {
                foreach (string language in LanguageNames)
                {
                    if (ContainsWord(SplitIdentifier(identifier), language))
                    {
                        offenders.Add($"{SourceTree.RelativeToEngine(path)}: {identifier}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void NoConsumerDomainWordAppearsOutsideAComment()
    {
        List<string> offenders = [];

        foreach (string path in SourceTree.EngineFiles())
        {
            string relative = SourceTree.RelativeToEngine(path);
            string[] lines = File.ReadAllLines(path);

            for (int i = 0; i < lines.Length; i++)
            {
                string code = StripComment(lines[i]);

                if (code.Length == 0)
                {
                    continue;
                }

                IReadOnlyList<string> words = SplitIdentifier(code);

                foreach (string domainWord in ConsumerDomainWords)
                {
                    if (ContainsWord(words, domainWord))
                    {
                        offenders.Add($"{relative}:{i + 1} uses '{domainWord}': {lines[i].Trim()}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "the engine must not contain any one consumer's vocabulary — that single rule is what "
            + "keeps it reusable");
    }

    [Fact]
    public void NoLogTemplateUsesDestructuring()
    {
        List<string> offenders = [];

        foreach (string path in SourceTree.EngineFiles())
        {
            string relative = SourceTree.RelativeToEngine(path);
            string[] lines = File.ReadAllLines(path);

            for (int i = 0; i < lines.Length; i++)
            {
                string code = StripComment(lines[i]);

                if (code.Contains("{@", StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "Serilog's destructuring operator reflects over public properties, which is the one "
            + "path in Serilog that produces trim warnings; the build suppresses IL2104 on the "
            + "strength of this guard, so the suppression stops being true the moment {@} appears");
    }

    [Fact]
    public void NoAnonymousTypeIsConstructedAnywhereInTheEngine()
    {
        List<string> offenders = [];

        foreach (string path in SourceTree.EngineFiles())
        {
            string relative = SourceTree.RelativeToEngine(path);
            string[] lines = File.ReadAllLines(path);

            for (int i = 0; i < lines.Length; i++)
            {
                if (ConstructsAnonymousType(StripComment(lines[i])))
                {
                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "an anonymous type has no source-generated JsonTypeInfo, so serialising one throws at "
            + "runtime under AOT — which is how /health once returned 500");
    }

    [Fact]
    public void TheAnonymousTypeGuardCatchesTheShapeThatBrokeHealth()
    {
        ConstructsAnonymousType("return Results.Ok(new { status = \"ok\" });").Should().BeTrue();
        ConstructsAnonymousType("var x = new {a = 1};").Should().BeTrue();
        ConstructsAnonymousType("var x = new").Should().BeFalse();
    }

    [Fact]
    public void TheAnonymousTypeGuardDoesNotFireOnOrdinaryConstruction()
    {
        ConstructsAnonymousType("var x = new StatusResponse(\"ok\");").Should().BeFalse();
        ConstructsAnonymousType("SchemaTables tables = new(options);").Should().BeFalse();
        ConstructsAnonymousType("var d = new Dictionary<string, object>").Should().BeFalse();
        ConstructsAnonymousType("List<string> errors = [];").Should().BeFalse();
        ConstructsAnonymousType("var o = new TokenValidationParameters").Should().BeFalse();
        ConstructsAnonymousType("string s = \"new { not code }\";").Should().BeFalse();
    }

    private static bool ConstructsAnonymousType(string code)
    {
        int index = 0;

        while (true)
        {
            index = code.IndexOf("new", index, StringComparison.Ordinal);

            if (index < 0)
            {
                return false;
            }

            int before = index - 1;
            int after = index + 3;

            bool standalone =
                (before < 0 || !IsIdentifierChar(code[before]))
                && after < code.Length
                && !IsIdentifierChar(code[after]);

            if (standalone && !IsInsideStringLiteral(code, index))
            {
                int cursor = after;

                while (cursor < code.Length && char.IsWhiteSpace(code[cursor]))
                {
                    cursor++;
                }

                if (cursor < code.Length && code[cursor] == '{')
                {
                    return true;
                }
            }

            index = after;
        }
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';

    private static bool IsInsideStringLiteral(string code, int index)
    {
        bool inString = false;

        for (int i = 0; i < index; i++)
        {
            if (code[i] == '"' && (i == 0 || code[i - 1] != '\\'))
            {
                inString = !inString;
            }
        }

        return inString;
    }

    [Fact]
    public void TheGuardWouldActuallyCatchAnOffendingLine()
    {
        StripComment("    string venueId = GetVenue();").Should().NotBeEmpty();

        ContainsWord(SplitIdentifier("string venueId = GetVenue();"), "venue").Should().BeTrue();
        ContainsWord(SplitIdentifier("var menuItems = new List<int>();"), "menu").Should().BeTrue();
        ContainsWord(SplitIdentifier("string orgId;"), "org").Should().BeTrue();
    }

    [Fact]
    public void TheGuardDoesNotFireOnWordsThatMerelyContainADomainWord()
    {
        ContainsWord(SplitIdentifier("Organize()"), "org").Should().BeFalse();
        ContainsWord(SplitIdentifier("original"), "org").Should().BeFalse();
        ContainsWord(SplitIdentifier("var organization = 1;"), "org").Should().BeFalse();
        ContainsWord(SplitIdentifier("MenuscriptParser"), "menu").Should().BeFalse();
    }

    [Fact]
    public void TheGuardIgnoresCommentsButNotStringsThatLookLikeThem()
    {
        StripComment("// venue example").Should().BeEmpty();
        StripComment("    /// <summary>a venue</summary>").Should().BeEmpty();
        StripComment("code(); // venue example").Should().Be("code(); ");
        StripComment("var url = \"http://x\";").Should().Contain("http");
    }

    private static IReadOnlyList<string> SplitIdentifier(string text)
    {
        List<string> words = [];
        System.Text.StringBuilder current = new();

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (char c in text)
        {
            bool isLetter = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

            if (!isLetter)
            {
                Flush();
                continue;
            }

            if (c is >= 'A' and <= 'Z' && current.Length > 0)
            {
                Flush();
            }

            current.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
        }

        Flush();

        return words;
    }

    private static bool ContainsWord(IReadOnlyList<string> words, string target) =>
        words.Any(w => string.Equals(w, target, StringComparison.Ordinal));

    private static string StripComment(string line)
    {
        string trimmed = line.TrimStart();

        if (trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        int marker = line.IndexOf("//", StringComparison.Ordinal);

        return marker < 0 ? line : line[..marker];
    }

    private static IEnumerable<string> DeclaredTypeNames(IEnumerable<string> lines)
    {
        string[] keywords = ["class ", "record ", "struct ", "interface ", "enum "];

        foreach (string line in lines)
        {
            string code = StripComment(line).Trim();

            foreach (string keyword in keywords)
            {
                int index = code.IndexOf(keyword, StringComparison.Ordinal);

                if (index < 0)
                {
                    continue;
                }

                string rest = code[(index + keyword.Length)..].Trim();
                int end = rest.IndexOfAny([' ', '<', '(', ':', '{', ';']);
                string name = end < 0 ? rest : rest[..end];

                if (name.Length > 0)
                {
                    yield return name;
                }
            }
        }
    }
}
