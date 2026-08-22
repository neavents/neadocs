namespace Neadocs.Engine.Infrastructure.Configuration;

using System;
using System.Text;

public static class ModelSlug
{
    public const int MaxLength = 40;

    public static string From(string model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return string.Empty;
        }

        StringBuilder builder = new(model.Length);
        bool lastWasSeparator = true;

        foreach (char c in model)
        {
            char mapped;

            if (c >= 'a' && c <= 'z')
            {
                mapped = c;
            }
            else if (c >= 'A' && c <= 'Z')
            {
                mapped = (char)(c + 32);
            }
            else if (c >= '0' && c <= '9')
            {
                mapped = c;
            }
            else
            {
                mapped = '_';
            }

            if (mapped == '_')
            {
                if (lastWasSeparator)
                {
                    continue;
                }

                lastWasSeparator = true;
            }
            else
            {
                lastWasSeparator = false;
            }

            builder.Append(mapped);
        }

        while (builder.Length > 0 && builder[^1] == '_')
        {
            builder.Length--;
        }

        if (builder.Length > MaxLength)
        {
            builder.Length = MaxLength;

            while (builder.Length > 0 && builder[^1] == '_')
            {
                builder.Length--;
            }
        }

        return builder.ToString();
    }

    public static bool IsValid(string slug) =>
        slug.Length > 0 && slug.Length <= MaxLength && !slug.StartsWith('_') && !slug.EndsWith('_');
}
