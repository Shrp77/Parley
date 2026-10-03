using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Web.Pages.Conversations;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the conversation page says what a participant is doing in the terminal's words (specs/024 FR-013).
/// </summary>
/// <remarks>
/// <para>
/// Both surfaces read <see cref="Activities"/> — the same derivation, mark and sentence — so they agree by construction; the
/// terminal's room is pinned by <c>RoomActivityTests</c>, and this pins the page. Set on the model directly, because what is
/// checked is the words the roster is handed, not the broker reads in front of them, which the terminal's tests drive.
/// </para>
/// </remarks>
public sealed class ActivityOnTheWebTests
{
    private static readonly AgentName Billing = AgentName.Create("billing@here");

    private static DetailModel Model(IReadOnlyDictionary<AgentName, Activity> activity)
    {
        var directory = new BrokerDirectory(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

        var model = new DetailModel(directory, new SeatBook(directory.AgentsFor));

        typeof(DetailModel).GetProperty(nameof(DetailModel.ParticipantActivity))!.SetValue(model, activity);

        return model;
    }

    [Fact]
    public void A_stopped_agent_reads_as_stopped_with_what_it_was_refused()
    {
        var stopped = new Activity(Doing.Stopped, DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(-48), PermissionDenials: 1);

        var said = Model(new Dictionary<AgentName, Activity> { [Billing] = stopped }).Doing(Billing);

        Assert.NotNull(said);
        Assert.StartsWith(Activities.StoppedMark + " stopped 2h4", said, StringComparison.Ordinal);
        Assert.EndsWith("— 1 permission refused", said, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_with_nothing_to_say_falls_back_to_the_roster()
    {
        // Presence alone has no sentence here; the roster's own state is shown instead.
        var running = new Activity(Doing.Running);

        Assert.Null(Model(new Dictionary<AgentName, Activity> { [Billing] = running }).Doing(Billing));
    }
}
