using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the terminal says, before a conversation is closed, how long its text is kept and how to keep a copy (SPEC.md
/// §9.4).
/// </summary>
/// <remarks>
/// The dialog once said only "Nothing is deleted", while closing made the text prunable at the next hourly pass. Closing
/// no longer shortens the idle period, and the dialog says so.
/// </remarks>
public sealed class ClosingSaysHowLongTextIsKeptTests
{
    [Fact]
    public async Task The_dialog_names_the_idle_period_and_export()
    {
        using var driving = Driving.Open();
        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:1");
        var client = new BrokerClient(new Wire.Operators.OperatorsClient(channel));
        var row = new ConversationRow(
            "pricing", DateTimeOffset.UnixEpoch, 2, [AgentName.Create("orders@test")], false, false, false);

        await Closing.OpenAsync(driving.App, client, row, _ => { }, _ => Task.CompletedTask);

        var choosing = (ChoiceDialog)driving.Find(Closing.Title);
        Assert.Contains("quiet for the retention idle period", choosing.Described, StringComparison.Ordinal);
        Assert.Contains("export it to keep a copy", choosing.Described, StringComparison.Ordinal);
    }
}
