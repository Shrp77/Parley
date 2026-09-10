using Parley.Setup;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the contract document's example is the file this actually writes.
/// </summary>
/// <remarks>
/// <para>
/// Review gate, lens 3, run 4. <c>specs/009-multi-project-enrolment/contracts/launch-configuration.md</c>
/// shows a launch configuration with two entries and names every variable in each, and nothing checked
/// it. <c>Parley.Spec.Tests</c> exists because the same was true of SPEC.md — every schema fragment in
/// that document executes, and the argument for it applies here word for word: a contract nobody
/// verifies is prose, and prose drifts.
/// </para>
/// <para>
/// <b>The drift runs both ways and both are worth catching.</b> A variable added to the writer and not
/// to the document leaves the document lying to whoever reads it before hand-editing a file; a
/// variable shown and never written sends somebody looking for something that was never there.
/// </para>
/// </remarks>
public sealed class LaunchContractTests
{
    private static readonly string Contract = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "specs", "009-multi-project-enrolment", "contracts", "launch-configuration.md"));

    /// <summary>The first fenced JSON block in the contract, as the document shows it.</summary>
    /// <remarks>
    /// Comments are skipped rather than stripped by hand: the block is annotated, which is what makes
    /// it worth reading, and a parser that admits them is one line where a stripper is a second
    /// implementation of JSON.
    /// </remarks>
    private static JsonObject Illustrated()
    {
        var text = File.ReadAllText(Contract).ReplaceLineEndings("\n");

        var open = text.IndexOf("```json", StringComparison.Ordinal);

        Assert.True(open >= 0, $"no fenced JSON in {Contract}");

        var from = text.IndexOf('\n', open) + 1;
        var to = text.IndexOf("```", from, StringComparison.Ordinal);

        return (JsonObject)JsonNode.Parse(
            text[from..to],
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            })!;
    }

    [Fact]
    public void The_illustrated_keys_this_owns_are_ones_it_derives()
    {
        // **Only the keys this owns, because the illustration deliberately holds one it does not.**
        // The first version of this required every key to be ours and reported `some-other-server`,
        // which is the entry the document is *making a point about*: a repository's launch
        // configuration is shared, and what this writes goes beside what was already there. A check
        // that failed on it would push somebody to delete the one line that matters most.
        var servers = (JsonObject)Illustrated()["mcpServers"]!;

        foreach (var (key, _) in servers.Where(pair => LaunchKey.IsOurs(pair.Key)))
        {
            Assert.True(
                LaunchKey.ProjectOf(key) is not null || key == LaunchKey.Unnamed,
                $"the contract shows '{key}', which this would never write");
        }
    }

    [Fact]
    public void The_illustration_shows_a_server_this_does_not_own()
    {
        // The other half, and the reason the filter above is not a hole: the document has to keep
        // showing a stranger, because "leaving N other server(s)" is a promise about somebody else's
        // configuration and an illustration with only our own entries never shows it being kept.
        var servers = (JsonObject)Illustrated()["mcpServers"]!;

        Assert.Contains(servers, pair => !LaunchKey.IsOurs(pair.Key));
    }

    [Fact]
    public void The_illustrated_variables_are_exactly_the_ones_this_writes()
    {
        // The strongest of the three and the least brittle: not the values, which are examples, but
        // the set of names — which is the thing a person reads this document to learn.
        var entry = new EnrollmentEntry("/src/inventory", "inventory", "Stock levels. Not pricing.", false)
        {
            Enroll = true,
            OwnsApproved = true,
        };

        var plan = EnrollmentPlan.Describe(entry, null, null, false, "alpha");

        var written = ((JsonObject)JsonNode.Parse(plan.LaunchConfiguration!)!
            ["mcpServers"]!["parley-alpha"]!["env"]!)
            .Select(pair => pair.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var shown = ((JsonObject)Illustrated()["mcpServers"]!["parley-alpha"]!["env"]!)
            .Select(pair => pair.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(shown, written);
    }

    [Fact]
    public void The_illustrated_command_is_the_one_this_writes()
    {
        var servers = (JsonObject)Illustrated()["mcpServers"]!;

        foreach (var (_, entry) in servers.Where(pair => LaunchKey.IsOurs(pair.Key)))
        {
            Assert.Equal(EnrollmentPlan.AdapterCommand, entry!["command"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Every_illustrated_entry_names_the_project_its_key_does()
    {
        // The document says this duplication is deliberate — the key is what the client uses and the
        // variable is what the adapter reads, and a reader who sees them disagree cannot tell which
        // one decides. So the illustration must not be the thing that shows them disagreeing.
        var servers = (JsonObject)Illustrated()["mcpServers"]!;

        foreach (var (key, entry) in servers)
        {
            var project = LaunchKey.ProjectOf(key);

            if (project is null)
            {
                continue;
            }

            Assert.Equal(
                project,
                entry!["env"]![EnrollmentPlan.ProjectVariable]!.GetValue<string>());
        }
    }
}
