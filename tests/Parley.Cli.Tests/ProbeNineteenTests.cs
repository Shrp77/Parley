using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 8: every sentence built out of a file's contents.
/// </summary>
/// <remarks>
/// <para>
/// 2.2 neutralised the values <c>Catalogues.Refusal</c> quotes. It is not the only sentence built from
/// a file: the enrolment plan and the withdrawal both quote keys, agent names, project names and
/// broker addresses read out of somebody's <c>.mcp.json</c>, into notes printed on a terminal.
/// </para>
/// <para>
/// This is lens 2's version of the pattern lens 1 found four times — a fix is a claim about every
/// caller, and it is only ever true of the ones the probe that found it happened to run. So this
/// enumerates them rather than sampling: every message either half can produce from a hostile file.
/// </para>
/// </remarks>
public sealed class ProbeNineteenTests : IDisposable
{
    private const string Escape = "";
    private const string Reverse = "‮";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe19-" + Path.GetRandomFileName());

    public ProbeNineteenTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
    }

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

    private string Workspace => Path.Combine(_root, "orders");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private static EnrollmentEntry Entry() =>
        new("/src/orders", "orders", "Carts and totals.", Unattended: false)
        {
            Enroll = true,
            OwnsApproved = true,
        };

    /// <summary>Every note a plan produces, joined.</summary>
    private static string Notes(string launch, string? project = "alpha")
    {
        try
        {
            return string.Join(" | ", EnrollmentPlan.Describe(Entry(), launch, null, false, project).Notes);
        }
        catch (InvalidDataException refusal)
        {
            return refusal.Message;
        }
    }

    private static void AssertFlat(string said)
    {
        foreach (var character in said)
        {
            Assert.False(
                char.IsControl(character) && character is not '\n',
                $"a message carried U+{(int)character:X4}: {said}");
        }

        Assert.DoesNotContain(Reverse, said, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_name_from_the_file_reaches_no_message_raw()
    {
        AssertFlat(Notes($$"""
            { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "orders{{Escape}}[2J{{Reverse}}", "PARLEY_PROJECT": "alpha" } } } }
            """));
    }

    [Fact]
    public void A_project_name_from_the_file_reaches_no_message_raw()
    {
        // The mismatch refusal quotes what the environment says the project is, and that is a string
        // in somebody's file rather than a name anything validated.
        AssertFlat(Notes($$"""
            { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "beta{{Escape}}[2J{{Reverse}}" } } } }
            """));
    }

    [Fact]
    public void A_broker_address_from_the_file_reaches_no_message_raw()
    {
        AssertFlat(Notes($$"""
            { "mcpServers": { "parley": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "orders", "PARLEY_BROKER": "127.0.0.1{{Escape}}[2J{{Reverse}}" } } } }
            """));
    }

    [Fact]
    public async Task Every_note_a_withdrawal_makes_from_the_file_is_flat()
    {
        // The withdrawal quotes the key it found and the agent that entry names, both read out of the
        // file, into notes a person reads before deciding whether an agent is gone.
        await File.WriteAllTextAsync(
            LaunchFile,
            $$"""
              { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": {
                  "PARLEY_AGENT": "somebody{{Escape}}[2J{{Reverse}}", "PARLEY_PROJECT": "alpha" } } } }
              """,
            TestContext.Current.CancellationToken);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", "alpha", null, TestContext.Current.CancellationToken);

        AssertFlat(string.Join(" | ", outcome.Notes));
    }

    [Fact]
    public async Task A_withdrawal_that_refuses_an_ambiguous_file_is_flat_too()
    {
        // The refusal that lists every project a repository answers in, built from the file's own
        // project names.
        await File.WriteAllTextAsync(
            LaunchFile,
            $$"""
              { "mcpServers": {
                  "parley-alpha": { "command": "parley-mcp", "env": {
                      "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha{{Escape}}[2J" } },
                  "parley-beta": { "command": "parley-mcp", "env": {
                      "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "beta{{Reverse}}" } } } }
              """,
            TestContext.Current.CancellationToken);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", null, null, TestContext.Current.CancellationToken);

        AssertFlat(string.Join(" | ", outcome.Notes));
    }
}
