using System.Text.Json;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// The hook that tells an attended session it has mail.
/// </summary>
/// <remarks>
/// <para>
/// The inbox is pull, and an agent can only pull when its session takes a turn. A supervisor
/// gives an unattended agent a turn precisely because something is waiting; nothing gives one to
/// a repository a person has open. Such a session sweeps, reports live, and never looks — a
/// message sat in one for four and a half hours while every indicator said the deployment was
/// healthy, which is what this hook exists to prevent.
/// </para>
/// <para>
/// Checked from here rather than by running it, because the runner is a Python interpreter that
/// a CI image need not have and a missing one would fail this suite for a reason unrelated to
/// Parley. What is pinned is the part that would rot silently: that the plugin still declares
/// it, and that it still cannot carry a peer's words.
/// </para>
/// </remarks>
public sealed class WaitingHookTests
{
    private const string Script = "plugins/parley/hooks/parley_waiting.py";

    private static string Source => RepositoryFile.Read(Script);

    [Fact]
    public void The_plugin_declares_the_hook_on_the_events_that_mean_somebody_is_here()
    {
        // SessionStart alone would check once and then let a long session drift back into
        // deafness, which is the failure this replaces rather than a smaller version of it.
        using var manifest = JsonDocument.Parse(
            RepositoryFile.Read("plugins/parley/hooks/hooks.json"));

        var hooks = manifest.RootElement.GetProperty("hooks");

        Assert.True(hooks.TryGetProperty("SessionStart", out _));
        Assert.True(hooks.TryGetProperty("UserPromptSubmit", out _));

        var declared = hooks.GetRawText();

        Assert.Contains("parley_waiting.py", declared, StringComparison.Ordinal);
        Assert.Contains("CLAUDE_PLUGIN_ROOT", declared, StringComparison.Ordinal);

        // Swallows its own failure. A hook that exits non-zero on a machine without the
        // interpreter would block the prompt it was meant to annotate.
        Assert.Contains("|| exit 0", declared, StringComparison.Ordinal);

        // Both interpreter names. Windows installs 'python' and most Linux and macOS machines
        // have only 'python3', and picking one would leave the hook silently never running on
        // half of them -- silently because it is designed to swallow its own failure, which is
        // right and is exactly what would hide this.
        Assert.Contains("python ", declared, StringComparison.Ordinal);
        Assert.Contains("python3 ", declared, StringComparison.Ordinal);
    }

    [Fact]
    public void The_notice_cannot_carry_a_peer_words()
    {
        // The security-relevant one, and the regression somebody will introduce with the best
        // of intentions: showing the message rather than the count, to save the agent a call.
        //
        // §13 requires a peer's words to arrive wrapped in the envelope marking them as data
        // rather than instruction, and parley_inbox is what applies that wrapping. Text injected
        // by a hook arrives with no envelope at all, in a channel the model has every reason to
        // trust — which is exactly the injection route the envelope exists to close. A count
        // cannot carry an instruction; a payload can.
        Assert.DoesNotContain("payload_json", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("from_agent", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void It_reads_the_store_without_taking_a_lock_the_servers_are_waiting_on()
    {
        // Every agent's server is writing to this file continuously. A hook that opened it for
        // writing, or held a lock, would stall the deployment it was reporting on.
        Assert.Contains("mode=ro", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_nothing_when_nothing_is_waiting()
    {
        // A hook that speaks every turn is one people learn to ignore, and this has to be worth
        // reading on the rare turn it appears.
        Assert.Contains("if unread or owed:", Source, StringComparison.Ordinal);
    }
}
