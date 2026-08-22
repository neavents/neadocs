namespace Neadocs.Engine.Infrastructure.Chunking;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Infrastructure.Configuration;

public sealed class MarkdownChunker
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .Build();

    private static readonly char[] SentenceEnders = ['.', '!', '?', '…'];

    private readonly ChunkingOptions _options;

    public MarkdownChunker(IOptions<DocumentEngineOptions> options)
        : this(options.Value.Chunking)
    {
    }

    public MarkdownChunker(ChunkingOptions options) => _options = options;

    public IReadOnlyList<DocumentChunk> Chunk(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        List<Block> blocks = TopLevelBlocks(content);
        List<PendingChunk> pending = Group(blocks, content);

        return Materialise(pending);
    }

    private static List<Block> TopLevelBlocks(string content)
    {
        MarkdownDocument document = Markdown.Parse(content, Pipeline);

        return [.. document];
    }

    private List<PendingChunk> Group(List<Block> blocks, string source)
    {
        List<PendingChunk> chunks = [];
        List<string> headingStack = [];
        List<string> buffer = [];
        List<string> bufferHeadings = [];

        void Flush()
        {
            if (buffer.Count == 0)
            {
                return;
            }

            chunks.Add(new PendingChunk([.. bufferHeadings], string.Join("\n\n", buffer)));
            buffer.Clear();
        }

        foreach (Block block in blocks)
        {
            string text = Slice(source, block).TrimEnd();

            if (text.Length == 0)
            {
                continue;
            }

            if (block is HeadingBlock heading)
            {
                string title = HeadingText(source, heading);

                if (heading.Level <= _options.SplitAtHeadingLevel)
                {
                    Flush();
                }

                while (headingStack.Count >= heading.Level)
                {
                    headingStack.RemoveAt(headingStack.Count - 1);
                }

                while (headingStack.Count < heading.Level - 1)
                {
                    headingStack.Add(string.Empty);
                }

                headingStack.Add(title);

                if (buffer.Count == 0)
                {
                    bufferHeadings = [.. headingStack];
                }
                else
                {
                    buffer.Add(text);
                }

                continue;
            }

            if (buffer.Count == 0)
            {
                bufferHeadings = [.. headingStack];
            }
            else if (EstimateTokens(string.Join("\n\n", buffer) + "\n\n" + text) > _options.TargetTokens)
            {
                Flush();
                bufferHeadings = [.. headingStack];
            }

            buffer.Add(text);

            if (chunks.Count >= _options.MaxChunksPerDocument)
            {
                break;
            }
        }

        Flush();

        return chunks.Count > _options.MaxChunksPerDocument
            ? [.. chunks.Take(_options.MaxChunksPerDocument)]
            : chunks;
    }

    private List<DocumentChunk> Materialise(List<PendingChunk> pending)
    {
        List<DocumentChunk> result = [];

        for (int i = 0; i < pending.Count; i++)
        {
            string overlap = i == 0 || _options.OverlapPercent <= 0
                ? string.Empty
                : TrailingOverlap(pending[i - 1].Body);

            result.Add(new DocumentChunk(
                i,
                pending[i].HeadingPath,
                pending[i].Body,
                overlap,
                EstimateTokens(overlap.Length == 0 ? pending[i].Body : overlap + "\n\n" + pending[i].Body)));
        }

        return result;
    }

    internal string TrailingOverlap(string previous)
    {
        int budget = (int)Math.Round(previous.Length * (_options.OverlapPercent / 100.0));

        if (budget <= 0 || previous.Length == 0)
        {
            return string.Empty;
        }

        int start = Math.Max(0, previous.Length - budget);
        int boundary = FindSentenceStart(previous, start);

        if (boundary < 0)
        {
            return string.Empty;
        }

        string overlap = previous[boundary..].Trim();

        return overlap.Length == 0 || overlap.Length == previous.Length ? string.Empty : overlap;
    }

    private static int FindSentenceStart(string text, int from)
    {
        for (int i = from; i < text.Length - 1; i++)
        {
            if (SentenceEnders.Contains(text[i]) && char.IsWhiteSpace(text[i + 1]))
            {
                return i + 2 <= text.Length ? i + 2 : text.Length;
            }
        }

        for (int i = from; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i + 1;
            }
        }

        return -1;
    }

    internal int EstimateTokens(string text) =>
        text.Length == 0 ? 0 : (int)Math.Ceiling(text.Length / _options.CharsPerToken);

    private static string Slice(string source, Block block)
    {
        int start = block.Span.Start;
        int length = Math.Min(block.Span.Length, source.Length - start);

        return length <= 0 ? string.Empty : source.Substring(start, length);
    }

    private static string HeadingText(string source, HeadingBlock heading)
    {
        string raw = Slice(source, heading).Trim();

        return raw.TrimStart('#').Trim();
    }

    private sealed record PendingChunk(IReadOnlyList<string> HeadingPath, string Body);
}
