using System.Text;

namespace TeaChest.Models;

public static class CommandLine
{
    /// <summary>Splits a command line using the same quoting rules as CommandLineToArgvW.</summary>
    public static List<string> Split(string? commandLine)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return args;

        string s = commandLine;
        var current = new StringBuilder();
        bool inQuotes = false;
        bool hasToken = false;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                i++;
                continue;
            }

            hasToken = true;
            if (c == '\\')
            {
                int count = 0;
                while (i < s.Length && s[i] == '\\')
                {
                    count++;
                    i++;
                }
                if (i < s.Length && s[i] == '"')
                {
                    // 2n backslashes + quote -> n backslashes, quote toggles quoting.
                    // 2n+1 backslashes + quote -> n backslashes and a literal quote.
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                }
                else
                {
                    current.Append('\\', count);
                }
                continue;
            }

            if (c == '"')
            {
                if (inQuotes && i + 1 < s.Length && s[i + 1] == '"')
                {
                    current.Append('"');
                    i += 2;
                    continue;
                }
                inQuotes = !inQuotes;
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        if (hasToken) args.Add(current.ToString());
        return args;
    }

    /// <summary>Joins arguments so that <see cref="Split"/> returns them unchanged.</summary>
    public static string Join(IEnumerable<string> args)
        => string.Join(' ', args.Select(Quote));

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;

        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
