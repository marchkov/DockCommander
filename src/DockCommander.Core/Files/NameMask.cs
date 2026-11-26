namespace DockCommander.Core.Files;

public static class NameMask
{
    public static bool Matches(string pattern, string name, StringComparer comparer)
    {
        var ignoreCase = comparer.Equals("A", "a");
        var patterns = pattern.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return patterns.Any(item => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
            item == "*.*" ? "*" : item, name, ignoreCase));
    }
}
