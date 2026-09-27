using System.Text.RegularExpressions;
using Parley.Core;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console writes the accepted line into the launch entry the repository actually has.
/// </summary>
/// <remarks>
/// <para>
/// <b>020's quickstart walk found this, and nothing in the suite could have.</b> The console asked
/// <c>OwnershipLine.WriteAsync</c> for the <em>unnamed</em> launch key, so every repository enrolled into a
/// named project — which is every repository since 009, because enrolment writes
/// <c>parley-&lt;project&gt;</c> — was refused with "has no entry for this agent. Nothing has been
/// written." The terminal passed its project and was unaffected, so the two surfaces disagreed about a
/// repository they were both looking at.
/// </para>
/// <para>
/// <b>It was invisible because every other test here runs against a broker that does not answer</b>, which
/// is deliberate and right for what those tests check: each asserts a guard applied before anything is
/// sent. The write is downstream of a registry read, so no test that refuses to let the registry answer
/// can reach it. <see cref="OwnershipConsole"/> exists for this one gap.
/// </para>
/// </remarks>
public sealed class TheConsoleWritesForItsProjectTests
{
    private static string Agent() => $"orders@{MachineName.Current}";

    private static string Repository(string key, string role)
    {
        var at = Path.Combine(
            Path.GetTempPath(), "parley-console-owns-" + Guid.NewGuid().ToString("N")[..8]);

        _ = Directory.CreateDirectory(at);

        File.WriteAllText(
            Path.Combine(at, ".mcp.json"),
            $$"""
            {
              "mcpServers": {
                "{{key}}": {
                  "command": "parley-mcp",
                  "env": {
                    "PARLEY_AGENT": "{{role}}"
                  }
                }
              }
            }
            """);

        return at;
    }

    [Fact]
    public async Task A_repository_enrolled_into_a_named_project_is_written_to()
    {
        // Must fail against 0.34.0, which asked for the unnamed key and reported "has no entry for this
        // agent" against a repository holding `parley-alpha` — the shape every enrolment writes.
        var workspace = Repository($"parley-{OwnershipConsole.Project}", "orders");

        try
        {
            using var console = new OwnershipConsole();
            console.Broker.Agent = Agent();
            console.Broker.Workspace = workspace;

            using var client = console.CreateClient();

            using var response = await PostAsync(
                client,
                $"/Projects/Act?project={OwnershipConsole.Project}&action=ownership&subject={Agent()}",
                new() { ["Text"] = "The ordering service and its refunds, not the ledger." });

            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.DoesNotContain("Not recorded", body, StringComparison.Ordinal);

            var written = await File.ReadAllTextAsync(
                Path.Combine(workspace, ".mcp.json"), TestContext.Current.CancellationToken);

            Assert.Contains("PARLEY_OWNS", written, StringComparison.Ordinal);
            Assert.Contains("not the ledger", written, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task A_repository_holding_only_the_unnamed_key_is_still_written_to()
    {
        // The legacy shape, which 009 says is read for ever. `LaunchEntries.Landing` falls back to the
        // unnamed key when the project's own is absent, so naming the project must not lose it — which
        // is the way a fix for the test above could quietly break the case it replaced.
        var workspace = Repository("parley", "orders");

        try
        {
            using var console = new OwnershipConsole();
            console.Broker.Agent = Agent();
            console.Broker.Workspace = workspace;

            using var client = console.CreateClient();

            using var response = await PostAsync(
                client,
                $"/Projects/Act?project={OwnershipConsole.Project}&action=ownership&subject={Agent()}",
                new() { ["Text"] = "Refunds only." });

            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.DoesNotContain("Not recorded", body, StringComparison.Ordinal);

            var written = await File.ReadAllTextAsync(
                Path.Combine(workspace, ".mcp.json"), TestContext.Current.CancellationToken);

            Assert.Contains("Refunds only.", written, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task The_file_is_written_before_the_record_and_the_record_says_it_was_composed()
    {
        // What a person types on this page is their own line, so it is recorded as theirs (§17 q10's
        // amendment turns on telling a person's words from a model's) — with no conversation and no
        // proposed length, because there was no proposal. 0.29.0's lens 2 is the reason the length field
        // must stay empty rather than carry the person's characters.
        var workspace = Repository($"parley-{OwnershipConsole.Project}", "orders");

        try
        {
            using var console = new OwnershipConsole();
            console.Broker.Agent = Agent();
            console.Broker.Workspace = workspace;

            using var client = console.CreateClient();

            using var response = await PostAsync(
                client,
                $"/Projects/Act?project={OwnershipConsole.Project}&action=ownership&subject={Agent()}",
                new() { ["Text"] = "Invoicing." });

            _ = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            var recorded = Assert.Single(console.Broker.Recorded);

            Assert.Equal("Invoicing.", recorded.Statement);
            Assert.Equal(Parley.Transport.OwnershipSource.Composed, recorded.Source);
            Assert.Equal(0, recorded.ProposedLength);
            Assert.Empty(recorded.ConversationId);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task A_repository_whose_entry_names_somebody_else_is_refused_and_nothing_is_written()
    {
        // 005's review finding from the other side: a withdrawal once deleted a *different* agent's
        // launch configuration, because the workspace comes from the registry and the registry is what
        // the agent said about itself. The name is read back out of the file first.
        var workspace = Repository($"parley-{OwnershipConsole.Project}", "somebody-else");
        var before = await File.ReadAllTextAsync(
            Path.Combine(workspace, ".mcp.json"), TestContext.Current.CancellationToken);

        try
        {
            using var console = new OwnershipConsole();
            console.Broker.Agent = Agent();
            console.Broker.Workspace = workspace;

            using var client = console.CreateClient();

            using var response = await PostAsync(
                client,
                $"/Projects/Act?project={OwnershipConsole.Project}&action=ownership&subject={Agent()}",
                new() { ["Text"] = "Everything." });

            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Contains("Not recorded", body, StringComparison.Ordinal);
            Assert.Contains("somebody-else", body, StringComparison.Ordinal);
            Assert.Empty(console.Broker.Recorded);

            Assert.Equal(
                before,
                await File.ReadAllTextAsync(
                    Path.Combine(workspace, ".mcp.json"), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string path, Dictionary<string, string> fields)
    {
        var token = TestContext.Current.CancellationToken;

        using var page = await client.GetAsync(new Uri(path, UriKind.Relative), token);

        var html = await page.Content.ReadAsStringAsync(token);

        var value = Regex.Match(
            html,
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(2)).Groups[1].Value;

        Assert.NotEmpty(value);

        fields["__RequestVerificationToken"] = value;

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields),
        };

        if (page.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            request.Headers.Add(
                "Cookie", string.Join("; ", cookies.Select(one => one.Split(';')[0])));
        }

        return await client.SendAsync(request, token);
    }
}
