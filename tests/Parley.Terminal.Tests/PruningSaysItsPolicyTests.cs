using Grpc.Net.Client;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Core;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the terminal says, before a prune, what the age cap reaches, and after it, under which policy it ran (SPEC.md §9.4).
/// </summary>
/// <remarks>
/// The dialog once promised that nothing not yet collected is removed, which the age cap contradicts, and its result named
/// only counts.
/// </remarks>
[Integration]
public sealed class PruningSaysItsPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_dialog_says_what_the_age_cap_reaches_and_the_result_names_the_policy()
    {
        using var connection = ParleyDatabase.OpenInMemory();
        await using var broker = LoopbackOperator.Start(connection);
        using var driving = Driving.Open();
        using var channel = GrpcChannel.ForAddress(broker.Address);
        var client = new BrokerClient(new Wire.Operators.OperatorsClient(channel));
        var project = new ProjectSettings(ProjectName.Create("alpha"), "unused.db", broker.Address, null);
        var work = new List<Task>();
        var said = string.Empty;

        await Pruning.OpenAsync(
            driving.App,
            client,
            project,
            run => work.Add(run(Ct)),
            reported =>
            {
                said = reported;
                return Task.CompletedTask;
            });

        var choosing = (ChoiceDialog)driving.Find(Pruning.Title);
        Assert.Contains("until they pass the age cap", choosing.Described, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing not yet collected", choosing.Described, StringComparison.Ordinal);

        driving.Row(choosing.Choices, "Prune it");
        driving.Enter();
        var typing = (TextDialog)driving.Find(Pruning.Title);
        driving.Type("alpha");
        typing.Finish(taken: true);
        await Task.WhenAll(work);

        Assert.Contains(
            "Policy applied: a conversation quiet for 3 days is pruned, and nothing but open work is kept past 30 days.",
            said,
            StringComparison.Ordinal);
    }
}
