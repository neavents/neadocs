namespace Neadocs.Engine.Infrastructure.Text;

using System;
using System.Text;

public static class LocaleTag
{
    public const int MaxLength = 35;

    public static string Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> trimmed = tag.AsSpan().Trim();
        StringBuilder builder = new(trimmed.Length);

        foreach (char c in trimmed)
        {
            if (c >= 'A' && c <= 'Z')
            {
                builder.Append((char)(c + 32));
            }
            else if (c == '_')
            {
                builder.Append('-');
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    public static bool IsWellFormed(string normalized)
    {
        if (normalized.Length is 0 or > MaxLength)
        {
            return false;
        }

        int segmentStart = 0;
        int segmentIndex = 0;

        for (int i = 0; i <= normalized.Length; i++)
        {
            bool atEnd = i == normalized.Length;

            if (!atEnd && normalized[i] != '-')
            {
                continue;
            }

            int length = i - segmentStart;

            if (!IsValidSegment(normalized.AsSpan(segmentStart, length), segmentIndex))
            {
                return false;
            }

            segmentStart = i + 1;
            segmentIndex++;
        }

        return true;
    }

    private static bool IsValidSegment(ReadOnlySpan<char> segment, int index)
    {
        if (index == 0)
        {
            if (segment.Length is < 2 or > 3)
            {
                return false;
            }

            foreach (char c in segment)
            {
                if (c is < 'a' or > 'z')
                {
                    return false;
                }
            }

            return true;
        }

        if (segment.Length is < 2 or > 8)
        {
            return false;
        }

        foreach (char c in segment)
        {
            bool isLower = c is >= 'a' and <= 'z';
            bool isDigit = c is >= '0' and <= '9';

            if (!isLower && !isDigit)
            {
                return false;
            }
        }

        return true;
    }
}
