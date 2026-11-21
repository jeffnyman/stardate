namespace Stardate.Driver.Tests;

/// <summary>
/// The routes in tools/routes. Each is read and checked every time. Playing one to its ending takes
/// minutes, so that's done only when STARDATE_ROUTES is set (to anything); otherwise those tests
/// pass without checking, as the core's tests do without their test data.
/// </summary>
public class RouteTests
{
    [Theory]
    [InlineData("stfc")]
    [InlineData("stka")]
    [InlineData("stpp")]
    public void A_route_reads_as_steps_with_distinct_names_and_balanced_braces(string game)
    {
        var route = Route.Read(RouteFile(game));

        Assert.NotEmpty(route.Steps);
        Assert.Equal(route.Steps.Count, route.Steps.Select(s => s.Name).Distinct().Count());
        foreach (var step in route.Steps)
        {
            // Parse throws on a brace left open
            Assert.NotEmpty(Script.Parse(step.Script));
        }
    }

    // a route's last step expects the ending's text, so a route that plays through every step, all as
    // expected, has reached it
    [Theory]
    [InlineData("stfc")]
    [InlineData("stka")]
    [InlineData("stpp")]
    public void A_route_plays_a_new_game_to_its_winning_ending(string game)
    {
        if (Environment.GetEnvironmentVariable("STARDATE_ROUTES") is null || Program.FindGames() is not { } games)
        {
            return;
        }

        string states = Path.Combine(Path.GetTempPath(), "stardate-route-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = GameProfile.Named(game)!;
            var session = new Session(profile, Path.Combine(games, game), Path.Combine(states, "saves"));
            var said = new List<string>();

            var route = Route.Read(RouteFile(game));
            bool expected = route.Play(session, states, null, null, said.Add);

            string tail = string.Join('\n', said.TakeLast(40));
            Assert.True(expected, tail);
            Assert.True(said.Contains("== " + route.Steps[^1].Name), $"The route stopped before its last step:\n{tail}");
        }
        finally
        {
            Directory.Delete(states, recursive: true);
        }
    }

    private static string RouteFile(string game)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "routes", game + ".route");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"tools/routes/{game}.route isn't found above the tests.");
    }
}
