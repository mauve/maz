using System.Text.RegularExpressions;

namespace Console.Cli.Commands.Copy;

/// <summary>
/// Simple glob pattern matcher supporting *, **, and ?.
/// Applied client-side against blob names or relative file paths.
/// </summary>
public sealed class GlobMatcher
{
    private readonly Regex _regex;

    public GlobMatcher(string pattern)
    {
        _regex = new Regex(GlobToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>Returns true if the path matches the glob pattern.</summary>
    public bool IsMatch(string path) => _regex.IsMatch(path);

    /// <summary>
    /// Convert a glob pattern to a regex pattern.
    /// * matches anything except /
    /// ** matches anything including /
    /// ? matches a single character except /
    /// </summary>
    private static string GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        int i = 0;
        while (i < glob.Length)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        // ** matches everything including path separators
                        sb.Append(".*");
                        i += 2;
                        // Skip trailing /
                        if (i < glob.Length && glob[i] == '/')
                            i++;
                    }
                    else
                    {
                        // * matches everything except /
                        sb.Append("[^/]*");
                        i++;
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    i++;
                    break;
                case '.':
                case '(':
                case ')':
                case '+':
                case '|':
                case '^':
                case '$':
                case '@':
                case '{':
                case '}':
                case '[':
                case ']':
                case '\\':
                    sb.Append('\\').Append(c);
                    i++;
                    break;
                default:
                    sb.Append(c);
                    i++;
                    break;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }
}
