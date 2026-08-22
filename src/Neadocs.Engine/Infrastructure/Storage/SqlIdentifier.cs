namespace Neadocs.Engine.Infrastructure.Storage;

public static class SqlIdentifier
{
    public const int MaxLength = 63;

    public static bool IsValid(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier.Length > MaxLength)
        {
            return false;
        }

        char first = identifier[0];

        if (first is not ((>= 'a' and <= 'z') or '_'))
        {
            return false;
        }

        foreach (char c in identifier)
        {
            bool isLower = c is >= 'a' and <= 'z';
            bool isDigit = c is >= '0' and <= '9';

            if (!isLower && !isDigit && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
