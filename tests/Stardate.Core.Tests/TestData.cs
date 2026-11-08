namespace Stardate.Core.Tests;

internal static class TestData
{
    /// <summary>
    /// The folder at that path, its case aside, in STARDATE_TESTDATA, or in a _testdata folder
    /// above the test binary; null when there is none. Tests that need it pass without checking then.
    /// </summary>
    public static string? Find(string relativePath) => Search("STARDATE_TESTDATA", ["_testdata"], relativePath);

    /// <summary>
    /// A game's folder, from the repository's own <c>games</c>: <c>stfc</c>, <c>stka</c> or
    /// <c>stpp</c>. STARDATE_GAMES can point elsewhere. Null when there is none.
    /// </summary>
    public static string? Game(string folder) => Search("STARDATE_GAMES", ["games"], folder);

    private static string? Search(string variable, string[] rootNames, string relativePath)
    {
        var fromEnv = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(fromEnv) && In(fromEnv, relativePath) is { } found)
        {
            return found;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var rootName in rootNames)
            {
                if (In(Path.Combine(dir.FullName, rootName), relativePath) is { } candidate)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? In(string root, string relativePath)
    {
        string current = root;
        foreach (var part in relativePath.Split('/'))
        {
            if (!Directory.Exists(current))
            {
                return null;
            }

            current = Directory.EnumerateDirectories(current).FirstOrDefault(d => string.Equals(Path.GetFileName(d), part, StringComparison.OrdinalIgnoreCase))!;
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }
}
