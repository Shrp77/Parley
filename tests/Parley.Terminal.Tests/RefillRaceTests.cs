using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a broker's answer arriving while the screen is being drawn does not end the program.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by probing, and the first probe found nothing.</b> Every read this surface makes
/// finishes on a pool thread — <c>Shell.SelectAsync</c> awaits the broker with
/// <c>ConfigureAwait(false)</c> and then calls a region's <c>Show</c> — while the main loop draws
/// on another. <c>ListView.OnDrawingContent</c> reads its source's count and then indexes it, so
/// a source swapped between those two reads throws out of the toolkit on the draw thread, which
/// in a terminal is the end of the process.
/// </para>
/// <para>
/// The first version of this probe reported zero failures across four thousand draws — because
/// the region under it had never been given a size, so the row handler had not run once. The
/// counters below are what turned a green probe into a red one, and they are kept for the same
/// reason: <b>a test that never renders a row passes whatever the surface does.</b>
/// </para>
/// <para>
/// <b>Every region, not just the one the probe started on.</b> All four are refilled from the same
/// read and all four now derive a row's colour from a field beside the rows, so a guard that
/// covered one would say nothing about the other three.
/// </para>
/// </remarks>
public sealed class RefillRaceTests
{
    /// <summary>How many draws each region is hammered for.</summary>
    /// <remarks>
    /// Four thousand reproduced the fault four hundred and thirty-four times before the fix, so
    /// this is far above the count needed to fail if it comes back.
    /// </remarks>
    private const int Draws = 4000;

    /// <summary>Two projects, so a walk has somewhere to go besides where it is.</summary>
    private static ProjectName Alpha { get; } = ProjectName.Create("alpha");

    /// <summary>The second one.</summary>
    private static ProjectName Beta { get; } = ProjectName.Create("beta");

    [Fact]
    public void The_status_line_is_one_row_whatever_its_text()
    {
        // Found by review, round 51, from a Linux CI failure of the test below: the status line's text is set from pool
        // threads, and a label sized to its text reads a cached size the setter clears, so a draw meeting a write threw
        // "Nullable object must have a value" in DimAuto.Calculate. One write in about 580,000 hit it, too rare to hold
        // here by racing; a fixed height takes the label out of that path, so this holds the height.
        using var directory = new BrokerDirectory(
            [new ProjectSettings(Alpha, string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
        using var shell = new Shell(directory);

        var status = (View)typeof(Shell)
            .GetField("_status", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(shell)!;

        Assert.Equal(Dim.Absolute(1), status.Height);
    }

    [Fact]
    public async Task Changing_view_from_many_threads_while_drawing_does_not_throw()
    {
        // **The second finding of the same run, by a different mechanism.** `ArrangeLocked` used
        // to remove all four regions and add back the current view's, which mutates the subview
        // list -- while the main loop walks that same list in `View.CollectAll` during layout.
        // `Shell._screen` locked that method against itself and the toolkit takes no lock, so it
        // never protected against the reader: eight threads changing view against three thousand
        // draws threw `Collection was modified` seven hundred and forty-nine times.
        //
        // Reachable by the commonest action there is. Opening a conversation queues a read, and
        // that read calls `GoAsync` after awaiting the broker, which is a pool thread.
        using var driving = Driving.Open(width: 100, height: 30);
        using var directory = new BrokerDirectory(
            [
                new ProjectSettings(Alpha, string.Empty, "http://127.0.0.1:1", null),
                new ProjectSettings(Beta, string.Empty, "http://127.0.0.1:2", null),
            ],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

        using var shell = new Shell(directory);

        driving.Show(shell);

        ViewPlace[] places =
        [
            ViewPlace.Top,
            ViewPlace.Into(Alpha),
            ViewPlace.Into(Alpha).Into("some-discussion"),
            ViewPlace.Into(Beta),
        ];

        var failures = new List<Exception>();
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<Viewing, byte>();
        var stop = false;

        var walking = Enumerable.Range(0, 8).Select(which => Task.Factory.StartNew(
            () =>
            {
                var round = 0;

                while (!Volatile.Read(ref stop))
                {
                    try
                    {
                        shell.GoAsync(places[(which + round) % places.Length])
                            .GetAwaiter().GetResult();
                        shell.Move(forwards: round % 2 == 0);
                        round++;
                    }
                    catch (Exception failure)
                    {
                        lock (failures)
                        {
                            failures.Add(failure);
                        }
                    }
                }
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();

        for (var draw = 0; draw < 3000; draw++)
        {
            seen.TryAdd(shell.Place.Viewing, 0);

            try
            {
                driving.App.LayoutAndDraw(true);
            }
            catch (Exception failure)
            {
                lock (failures)
                {
                    failures.Add(failure);
                }
            }
        }

        Volatile.Write(ref stop, true);

        await Task.WhenAll(walking)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        lock (failures)
        {
            Assert.True(
                failures.Count == 0,
                $"{failures.Count} failures, first: {failures.FirstOrDefault()}");
        }

        // Non-vacuity: the view has to have actually moved under the draws. One view seen means
        // the walkers finished before the drawing began, and this proved nothing.
        Assert.True(
            seen.Count > 1,
            $"the surface was only ever on {seen.Keys.FirstOrDefault()} while drawing");
    }

    [Fact]
    public async Task An_agents_region_refilled_while_it_draws_does_not_throw() =>
        await RaceAsync(
            new AgentsRegion(),
            (region, many) => region.Show(Agents(many), NodeCatalogue.Empty, null));

    [Fact]
    public async Task A_projects_region_refilled_while_it_draws_does_not_throw() =>
        await RaceAsync(
            new ProjectsRegion(),
            (region, many) => region.Show(
                Projects(many),
                new Dictionary<string, ProjectHealth>(StringComparer.Ordinal),
                null));

    [Fact]
    public async Task A_conversations_region_refilled_while_it_draws_does_not_throw() =>
        await RaceAsync(
            new ConversationsRegion(),
            (region, many) => region.Show(Conversations(many), null));

    [Fact]
    public async Task A_transcript_refilled_while_it_draws_does_not_throw() =>
        await RaceAsync(
            new TranscriptRegion(),
            (region, many) => region.Show(
                "threeway", "alpha", new ConversationExport(Messages(many), []), null));

    /// <summary>
    /// Refills a region from a pool thread while drawing it from this one.
    /// </summary>
    /// <typeparam name="TRegion">Which region.</typeparam>
    /// <param name="region">The region.</param>
    /// <param name="refill">Fills it with a given number of rows.</param>
    /// <returns>When the race has run and nothing has thrown.</returns>
    /// <remarks>
    /// The row counts alternate between many and one, because the hazard is a shorter list
    /// arriving between a reader's length check and its index. Filling with the same number every
    /// time would swap one list for an identically sized one and never reach the fault.
    /// </remarks>
    private static async Task RaceAsync<TRegion>(TRegion region, Action<TRegion, int> refill)
        where TRegion : View
    {
        using var driving = Driving.Open(width: 100, height: 30);

        var window = new Window { BorderStyle = LineStyle.None };

        region.X = 0;
        region.Y = 0;
        region.Width = Dim.Fill();
        region.Height = Dim.Fill();

        window.Add(region);
        driving.Show(window);

        var rendered = 0;
        var lengths = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

        var list = Assert.IsAssignableFrom<GuardedList>(Assert.Single(
            region.SubViews.OfType<GuardedList>().ToList()));

        list.RowRender += (_, _) =>
        {
            Interlocked.Increment(ref rendered);
            lengths.TryAdd(list.Source?.Count ?? -1, 0);
        };

        var failures = new List<Exception>();
        var stop = false;

        var refilling = Task.Factory.StartNew(
            () =>
            {
                var round = 0;

                while (!Volatile.Read(ref stop))
                {
                    try
                    {
                        refill(region, round % 2 == 0 ? 40 : 1);
                        round++;
                    }
                    catch (Exception failure)
                    {
                        lock (failures)
                        {
                            failures.Add(failure);
                        }
                    }
                }
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        for (var draw = 0; draw < Draws; draw++)
        {
            try
            {
                driving.App.LayoutAndDraw(true);
            }
            catch (Exception failure)
            {
                lock (failures)
                {
                    failures.Add(failure);
                }
            }
        }

        Volatile.Write(ref stop, true);

        await refilling.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        lock (failures)
        {
            Assert.True(
                failures.Count == 0,
                $"{failures.Count} failures across {Draws} draws, first: {failures.FirstOrDefault()}");
        }

        // Non-vacuity, both halves. Rows must actually have been drawn, and the writer must
        // actually have got in among the draws -- one length seen means the two never interleaved
        // and this test proved nothing.
        Assert.True(rendered > 0, "no row was ever rendered, so nothing was raced against");
        Assert.True(
            lengths.Count > 1,
            $"the list was only ever {lengths.Keys.FirstOrDefault()} rows while drawing, so the "
            + "refill never landed during a draw");
    }

    private static List<AgentRecord> Agents(int many) =>
        [.. Enumerable.Range(0, many).Select(at => new AgentRecord(
            AgentName.Create($"agent{at}@here"),
            "mcp",
            AgentTier.Pull,
            null,
            null,
            null,
            true,
            AgentStatus.Active,
            null,
            AgentPresence.Running))];

    private static List<ProjectSettings> Projects(int many) =>
        [.. Enumerable.Range(0, many).Select(at => new ProjectSettings(
            ProjectName.Create($"project{at}"),
            string.Empty,
            $"http://127.0.0.1:{9000 + at}",
            null))];

    private static List<ConversationSummary> Conversations(int many) =>
        [.. Enumerable.Range(0, many).Select(at => new ConversationSummary(
            $"conversation{at}",
            [AgentName.Create("orders@here")],
            at,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            at % 2 == 0))];

    private static List<Message> Messages(int many) =>
        [.. Enumerable.Range(0, many).Select(at => new Message(
            at + 1,
            ExchangeId: 1,
            MessageType.Request,
            AgentName.Create("orders@here"),
            AgentName.Create("inventory@here"),
            "test",
            """{"said":"a question"}""",
            "threeway",
            DateTimeOffset.UnixEpoch,
            null))];
}
