using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That `project list` never merges "it answered" with "anything can address it" (T075, FR-023).
/// </summary>
/// <remarks>
/// <para>
/// <b>The spec quotes the old listing as the defect, in as many words:</b> "Today's listing prints
/// <c>reachable</c> beside <c>http://127.0.0.1:51707</c>, meaning only that this process connected
/// to it. Somebody planning a firewall rule reads that as reachable from the network — and no rule
/// they write can make it so, because nothing off the machine can address a loopback port."
/// </para>
/// <para>
/// Two questions, and they change on different timescales. Whether the broker answered is about
/// this second and is different a minute later. Whether anything off the machine could address the
/// endpoint at all is a property of what is written in <c>project.json</c>, and changes only when
/// somebody edits it. One word could not carry both and the one it carried was the wrong one.
/// </para>
/// </remarks>
public sealed class ReachabilityIsTwoFactsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-two-facts-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<string> ListAsync(string endpoint)
    {
        var name = ProjectName.Create("orders");

        ProjectManifest.Write(
            _root,
            [new ProjectSettings(name, ProjectFolder.StoreFor(_root, name), endpoint, null)]);

        var output = new StringWriter();
        var error = new StringWriter();

        _ = await ProjectCommand.RunAsync(
            ["list"], _root, new ProjectFields(null), output, error,
            cancellationToken: TestContext.Current.CancellationToken);

        return output.ToString();
    }

    [Fact]
    public async Task A_loopback_endpoint_is_never_called_reachable()
    {
        // The word itself, refused. This is narrow on purpose: it is the exact spelling the spec
        // names, and a listing that reintroduced it would be the same defect however it was
        // phrased around it.
        var said = await ListAsync("http://127.0.0.1:19100");

        Assert.DoesNotContain("reachable", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task It_says_the_broker_did_not_answer_and_that_only_this_machine_could_reach_it()
    {
        // Both facts, separately, for an endpoint nothing is listening on. The first is about the
        // broker and the second about the address, and a reader must be able to tell which is
        // which — the old single word left somebody unable to act on either.
        var said = await ListAsync("http://127.0.0.1:19100");

        Assert.Contains("not answering", said, StringComparison.Ordinal);
        Assert.Contains("only this machine can address it", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_endpoint_that_is_not_loopback_is_named_as_such()
    {
        // **The case FR-022 exists for.** No command writes this — a project is handed a loopback
        // port when it is named — so the only way to arrive here is somebody editing
        // `project.json`, which SPEC.md §16 permits on a network the operator controls. The
        // listing has to say so, because all three gRPC services share the one port: reaching it
        // grants every operator operation, with no credentials on the wire.
        var said = await ListAsync("http://10.0.0.5:19100");

        Assert.Contains("ANY MACHINE", said, StringComparison.Ordinal);
        Assert.DoesNotContain("only this machine can address it", said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_listing_and_the_broker_ask_one_rule()
    {
        // The warning `serve` prints on every start and the line this listing writes are the same
        // question. Two implementations would agree until one was edited, and what they would
        // disagree about is whether a deployment is exposed.
        Assert.True(BrokerEndpoint.OnlyThisMachine("http://127.0.0.1:19100"));
        Assert.True(BrokerEndpoint.OnlyThisMachine("http://localhost:19100"));
        Assert.True(BrokerEndpoint.OnlyThisMachine("http://[::1]:19100"));
        Assert.False(BrokerEndpoint.OnlyThisMachine("http://10.0.0.5:19100"));
        Assert.False(BrokerEndpoint.OnlyThisMachine("http://0.0.0.0:19100"));

        // An endpoint nothing can parse reads as loopback, so the exposure warning does not fire
        // over what is actually a malformed value — the start fails with its own message instead.
        Assert.True(BrokerEndpoint.OnlyThisMachine("127.0.0.1:19100"));
    }
}
