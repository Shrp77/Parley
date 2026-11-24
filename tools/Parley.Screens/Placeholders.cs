using System.Globalization;
using System.Text.RegularExpressions;

namespace Parley.Screens;

/// <summary>What a shot's path may name in the scenario's words: <c>{project}</c>, <c>{agent:role}</c>, <c>{exchange:label}</c>.</summary>
/// <param name="Scenario">The scenario that was played.</param>
/// <param name="Exchanges">Each labelled request's exchange number.</param>
/// <remarks>
/// A shot names what it shows by the scenario's labels rather than by numbers, so adding a request to the scenario
/// does not send a shot to the wrong exchange. <see cref="Problems"/> judges a path against the scenario alone, so a
/// path that names nothing fails at <c>check</c>, before a broker or a browser starts.
/// </remarks>
internal sealed partial record Placeholders(Scenario Scenario, IReadOnlyDictionary<string, long> Exchanges)
{
    /// <summary>Says what a path names that the scenario does not make.</summary>
    /// <param name="path">A shot's path.</param>
    /// <param name="scenario">The scenario.</param>
    /// <returns>One line per placeholder that names nothing.</returns>
    public static IEnumerable<string> Problems(string path, Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        foreach (Match match in Pattern().Matches(path))
        {
            var name = match.Groups["name"].Value;
            var known = match.Groups["kind"].Value switch
            {
                "project" => name.Length == 0,
                "agent" => name.Length > 0 && scenario.Knows(name),
                "exchange" => scenario.ExchangeLabels.Contains(name),
                _ => false,
            };

            if (!known)
            {
                yield return $"'{match.Value}' names nothing the scenario makes";
            }
        }
    }

    /// <summary>Replaces every placeholder in <paramref name="path"/>, URL-encoding what it puts in.</summary>
    /// <param name="path">A shot's path, already judged by <see cref="Problems"/>.</param>
    /// <returns>The path the browser opens.</returns>
    /// <exception cref="InvalidDataException">A placeholder names nothing the scenario made.</exception>
    public string Expand(string path) =>
        Pattern().Replace(path, match =>
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["kind"].Value switch
            {
                "project" when name.Length == 0 => Scenario.Project,
                "agent" when Scenario.Knows(name) => Scenario.NameOf(name),
                "exchange" when Exchanges.TryGetValue(name, out var id) => id.ToString(CultureInfo.InvariantCulture),
                _ => throw new InvalidDataException($"{path}: '{match.Value}' names nothing the scenario made"),
            };

            return Uri.EscapeDataString(value);
        });

    [GeneratedRegex(@"\{(?<kind>[a-z]+)(:(?<name>[^}]+))?\}")]
    private static partial Regex Pattern();
}
