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
/// These read the source and the manifest, which catches a rule being deleted. What the file
/// actually does when run is <see cref="WaitingHookRunTests"/>, which is the stronger check and
/// needs an interpreter; these still run where there is none.
/// </para>
/// </remarks>
public sealed class WaitingHookTests
{
    private const string Script = "plugins/parley/hooks/parley_waiting.py";
    private const string Hooks = "plugins/parley/hooks/hooks.json";

    private static string Source => RepositoryFile.Read(Script);

    /// <summary>The script with its module docstring removed.</summary>
    /// <remarks>
    /// The rules below are about what the file does, and the docstring is where it explains
    /// what it deliberately does not do — so it names <c>subject</c> and <c>systemMessage</c> in
    /// the course of ruling them out. Grepping the whole file would make the prose that argues
    /// for a rule the thing that breaks it, and the obvious repair is to delete the explanation.
    /// </remarks>
    private static string Code
    {
        get
        {
            var source = Source;
            var opened = source.IndexOf("\"\"\"", StringComparison.Ordinal);

            // Anchored at the very first character rather than merely present. Every other
            // docstring in the file opens and closes on one line, so a module docstring that
            // had been deleted would leave this stripping one function's summary and calling
            // it the prose — and the rules below would then be grepping almost the whole file
            // again, passing for a reason that has nothing to do with what they assert.
            Assert.Equal(0, opened);

            var closed = source.IndexOf("\"\"\"", opened + 3, StringComparison.Ordinal);

            Assert.True(closed > opened, "the module docstring is unterminated");

            return source[(closed + 3)..];
        }
    }

    private static JsonDocument Manifest() => JsonDocument.Parse(RepositoryFile.Read(Hooks));

    [Fact]
    public void The_plugin_declares_the_hook_on_the_events_that_mean_somebody_is_here()
    {
        // Two pairs, because an agent can be deaf in two different ways. SessionStart and
        // UserPromptSubmit catch the turn boundary a person drives, and were the whole of this
        // once — they only fire when a human types, so an agent working through a long turn
        // learned nothing until that turn was over. PostToolBatch fires between tool batches and
        // is mid-turn delivery; Stop fires as the agent finishes and is the interlock.
        using var manifest = Manifest();

        var hooks = manifest.RootElement.GetProperty("hooks");

        foreach (var expected in new[] { "SessionStart", "UserPromptSubmit", "PostToolBatch", "Stop" })
        {
            Assert.True(hooks.TryGetProperty(expected, out _), $"{expected} is no longer declared");
        }

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
    public void Every_event_the_manifest_declares_is_one_the_script_answers()
    {
        // hooks.json names the events; the script decides the shape of the reply from
        // hook_event_name and answers an unrecognised one with silence. That is the right
        // default -- printing plain text at PostToolBatch is a parse failure rather than a
        // notice -- but it makes the two files a matched pair that nothing was checking.
        //
        // Declare an event here without teaching the script about it and the hook runs, exits
        // zero, prints nothing, and looks exactly like a repository with an empty inbox. That
        // is the same silently-dead failure as choosing one interpreter name, arriving by the
        // other door, and this file's whole design hides it: everything here fails by saying
        // nothing.
        using var manifest = Manifest();

        // Matched against the script as a whole rather than against the frozensets alone:
        // an event name the script never mentions cannot be one it answers, whichever
        // construct a later rewrite uses to hold them.
        foreach (var declared in manifest.RootElement.GetProperty("hooks").EnumerateObject())
        {
            Assert.True(
                Code.Contains($"\"{declared.Name}\"", StringComparison.Ordinal),
                $"hooks.json declares {declared.Name}, which parley_waiting.py answers with silence");
        }
    }

    [Fact]
    public void No_event_inside_a_subagent_is_declared()
    {
        // Deliberate, and the reasoning is not obvious enough to leave to the reader: collecting
        // is destructive. The broker stamps delivered_utc as it hands a message over, so a
        // subagent told it had mail would collect into a context that ends with the subagent,
        // mark the message delivered, and leave the principal none the wiser with the notice
        // permanently silent. That turns unread mail into destroyed mail.
        //
        // TeammateIdle is out for a second, independent reason: its payload carries no
        // stop_hook_active, so a hook that held a teammate could not tell a first pass from a
        // hundredth, and the only brake would be the runtime's own cap after eight blocked
        // turns.
        using var manifest = Manifest();

        var hooks = manifest.RootElement.GetProperty("hooks");

        Assert.False(hooks.TryGetProperty("SubagentStop", out _));
        Assert.False(hooks.TryGetProperty("TeammateIdle", out _));
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
        //
        // Every column that holds a peer's words or names them, not only the two that existed
        // when this was written. WaitingHookRunTests proves the same thing against real traffic.
        foreach (var column in new[] { "payload_json", "from_agent", "subject", "responder_model" })
        {
            Assert.DoesNotContain(column, Code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_model_facing_events_use_the_field_the_model_reads()
    {
        // systemMessage and additionalContext are not two spellings of the same thing:
        // systemMessage is shown to the person, additionalContext is delivered to the model.
        // Swapping them fails in whichever direction is silent — a notice the model never sees,
        // or peer-adjacent text on somebody's screen — and both look like the hook working.
        Assert.Contains("additionalContext", Code, StringComparison.Ordinal);
        Assert.DoesNotContain("systemMessage", Code, StringComparison.Ordinal);

        // Named back to the runtime, which rejects a result whose event does not match the one
        // it asked about.
        Assert.Contains("hookEventName", Code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_interlock_can_hold_a_turn_only_once()
    {
        // The failure this design invites, and the worse one of the two it sits between. A Stop
        // hook that spoke on every pass would hand the same notice to an agent that declined to
        // collect, for ever; the runtime's own backstop is eight consecutive blocks, so arriving
        // there costs eight model turns and a warning. stop_hook_active is true from the second
        // pass onward, which bounds the whole thing at one extra turn.
        Assert.Contains("stop_hook_active", Code, StringComparison.Ordinal);
    }

    [Fact]
    public void It_does_not_open_the_store_at_all()
    {
        // This asserted `mode=ro`: the hook opened the database read-only, because every agent's
        // server was writing to it continuously and a hook holding a lock would have stalled the
        // deployment it was reporting on.
        //
        // The guarantee is stronger now and the check says so rather than being deleted. One
        // process owns the store (SPEC.md §4), it may be on a machine this hook has never heard
        // of, and SQLite in WAL mode needs coherent shared memory between everything touching
        // the file — so reaching for it is not slow, it is the corrupt case. The hook asks the
        // broker through the operator's tool instead.
        Assert.DoesNotContain("sqlite3", Code, StringComparison.Ordinal);
        Assert.Contains("waiting", Code, StringComparison.Ordinal);
    }

    [Fact]
    public void It_bounds_how_long_it_will_wait_for_an_answer()
    {
        // It runs in front of a person waiting for their turn to start. A broker that is slow to
        // answer must cost a moment and then be dropped — and every failure is read as nothing
        // waiting, which is the safe direction: a session that does not know it has mail is a
        // great deal better than one that cannot be worked in.
        Assert.Contains("timeout=", Code, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_nothing_when_nothing_is_waiting()
    {
        // A hook that speaks every turn is one people learn to ignore, and this has to be worth
        // reading on the rare turn it appears. PostToolBatch makes it sharper: it fires after
        // every batch of tool calls, so a needless line would be the loudest thing in the
        // transcript.
        Assert.Contains("if unread or owed or notices:", Code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_notice_alone_never_holds_a_turn_open()
    {
        // A change notice wakes nothing (SPEC.md §9.5), and a Stop hook that speaks continues the turn — which is a
        // turn asked for. So at Stop the notices count only beside mail that would hold the turn anyway.
        Assert.Contains("if event == \"Stop\" and not (unread or owed):", Code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_count_is_asked_for_and_all_three_counts_are_required()
    {
        Assert.Contains("--with-files", Code, StringComparison.Ordinal);
        Assert.DoesNotContain("if len(counts) > 2 else 0", Code, StringComparison.Ordinal);
        Assert.Contains("return counts[0], counts[1], counts[2]", Code, StringComparison.Ordinal);
    }
}
