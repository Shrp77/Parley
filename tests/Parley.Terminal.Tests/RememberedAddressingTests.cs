using Parley.Core;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That who the line in a conversation goes to survives a restart, and never leaks into another conversation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's report of 2026-10-02</b>: four agents agreed which of them would speak for the rest, the owner ticked
/// that one, and after an upgrade restarted the terminal every agent was ticked again — so the next line went to all four
/// and all four answered it.
/// </para>
/// <para>
/// A restart is two shells over one <see cref="AddressBook"/> file, each with its own broker: the remembered entry is keyed
/// by project and conversation name, which is all a restarted terminal has to go on.
/// </para>
/// </remarks>
public sealed class RememberedAddressingTests
{
    private static readonly AgentName Orders = Seated.Orders;
    private static readonly AgentName Billing = Seated.Billing;

    [Fact]
    public void A_narrowed_line_is_read_back_from_the_file()
    {
        var path = Scratch();

        try
        {
            new AddressBook(path).Remember("alpha", "room", [Orders]);

            Assert.Equal([Orders], new AddressBook(path).Recall("alpha", "room"));
            Assert.Null(new AddressBook(path).Recall("alpha", "elsewhere"));
            Assert.Null(new AddressBook(path).Recall("beta", "room"));

            // Everyone again removes the entry rather than writing a second spelling of the default.
            new AddressBook(path).Remember("alpha", "room", null);

            Assert.Null(new AddressBook(path).Recall("alpha", "room"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_damaged_file_costs_the_memory_and_nothing_else()
    {
        var path = Scratch();

        try
        {
            File.WriteAllText(path, "{ this is not json");

            var book = new AddressBook(path);

            Assert.Null(book.Recall("alpha", "room"));

            book.Remember("alpha", "room", [Orders]);

            Assert.Equal([Orders], new AddressBook(path).Recall("alpha", "room"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Unticking_an_agent_is_still_unticked_after_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Scratch();

        try
        {
            await using (var before = await Seated.OpenAsync(ct, addressing: new AddressBook(path)))
            {
                await before.AskAsync(Billing, Seated.Conversation, ct);

                Assert.Equal(2, before.Shell.Drafting!.Recipients.Count);

                Untick(before, Billing);
                await before.Shell.Settled;

                Assert.Equal([Orders], before.Shell.Drafting!.Recipients);
            }

            await using var after = await Seated.OpenAsync(ct, addressing: new AddressBook(path));

            await after.AskAsync(Billing, Seated.Conversation, ct);

            Assert.Equal([Orders], after.Shell.Drafting!.Recipients);
            Assert.Contains(
                after.Shell.Room.Written,
                row => row.StartsWith(RoomRegion.NotAddressed, StringComparison.Ordinal)
                    && row.Contains(Billing.Value, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Ticking_everybody_back_forgets_the_narrowing()
    {
        // Otherwise a remembered list would leave out an agent that joins later, which is the opposite of what
        // "everyone" meant when it was chosen.
        var ct = TestContext.Current.CancellationToken;
        var book = new AddressBook(null);

        await using var seated = await Seated.OpenAsync(ct, addressing: book);

        await seated.AskAsync(Billing, Seated.Conversation, ct);

        Untick(seated, Billing);
        await seated.Shell.Settled;

        Assert.NotNull(book.Recall(Walking.Alpha.Value, Seated.Conversation));

        Untick(seated, Billing);
        await seated.Shell.Settled;

        Assert.Null(book.Recall(Walking.Alpha.Value, Seated.Conversation));
    }

    [Fact]
    public async Task Narrowing_in_one_conversation_does_not_follow_you_into_another()
    {
        // **Found while building this.** The draft was rebuilt from the previous one whatever conversation that had been
        // for, so the second conversation's line was addressed to the first conversation's agent — and sending it would
        // have pulled that agent into a conversation it was not in.
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);

        await seated.AskAsync(Billing, Seated.Conversation, ct);

        Untick(seated, Billing);
        await seated.Shell.Settled;

        Assert.Equal([Orders], seated.Shell.Drafting!.Recipients);

        await seated.AskAsync(Billing, "another-room", ct);

        Assert.Equal([Billing], seated.Shell.Drafting!.Recipients);
    }

    /// <summary>Puts the cursor on an agent in the Room and presses Space, which ticks or unticks it.</summary>
    /// <param name="seated">The fixture.</param>
    /// <param name="agent">The agent.</param>
    private static void Untick(Seated seated, AgentName agent)
    {
        seated.Focus<RoomRegion>();
        seated.Row(seated.Shell.Room.List, agent.Value);
        seated.Shell.NewKeyDownEvent(Key.Space);
    }

    /// <summary>A file name in the temporary folder, unique to one test.</summary>
    /// <returns>The path; nothing is created.</returns>
    private static string Scratch() => Path.Combine(Path.GetTempPath(), $"parley-addressing-{Guid.NewGuid():N}.json");
}
