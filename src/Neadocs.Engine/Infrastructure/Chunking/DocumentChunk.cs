namespace Neadocs.Engine.Infrastructure.Chunking;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

public sealed class DocumentChunk
{
    public DocumentChunk(
        int ordinal,
        IReadOnlyList<string> headingPath,
        string body,
        string overlap,
        int tokenCount)
    {
        Ordinal = ordinal;
        HeadingPath = headingPath;
        Body = body;
        Overlap = overlap;
        TokenCount = tokenCount;
        ContentHash = ChunkHash.Of(headingPath, body);
    }

    public int Ordinal { get; }

    public IReadOnlyList<string> HeadingPath { get; }

    public string Body { get; }

    public string Overlap { get; }

    public string Content => Overlap.Length == 0 ? Body : Overlap + "\n\n" + Body;

    public string ContentHash { get; }

    public int TokenCount { get; }

    public string TsvSource =>
        HeadingPath.Count == 0
            ? Content
            : string.Join(' ', HeadingPath) + ' ' + Content;
}

public static class ChunkHash
{
    private const char PathSeparator = (char)0x1F;

    public static string Of(IReadOnlyList<string> headingPath, string body)
    {
        StringBuilder builder = new();

        for (int i = 0; i < headingPath.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(PathSeparator);
            }

            builder.Append(headingPath[i]);
        }

        builder.Append('\n').Append(body);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()), hash);

        return Convert.ToHexStringLower(hash);
    }

    public static string OfDocument(string title, string content)
    {
        string payload = title.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + PathSeparator + title + content;

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(payload), hash);

        return Convert.ToHexStringLower(hash);
    }
}
