using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Google.Protobuf;
using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.Input;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Terminal.Tests;

/// <summary>That the agents pane's own refresh draws a background task's new name when the count stays the same (SPEC.md
/// §11.6).</summary>
/// <remarks>
/// <para>
/// The pane is redrawn on the mail watch's interval only when what it says has changed. That used to be each agent's
/// activity words, which for background work hold only the count, so an agent naming its task (parley_describe_background),
/// or one task giving way to another at the same count, never reached the row or the Alt+B list (lens 1 run 3 on #115).
/// </para>
/// <para>
/// <b>The refresh is called through reflection.</b> It runs only from the mail watch, which starts once a seat is held, and
/// holding one needs a broker that answers the attach stream; no key reaches it (a key's refresh draws whatever it reads,
/// so it cannot show the fault). The registry is scripted at the HTTP layer the gRPC client sends through, so what the pane
/// is given is what a broker would say. What is checked is what is drawn: the row, and the list Alt+B opens.
/// </para>
/// </remarks>
[Integration]
public sealed class ARenamedBackgroundTaskIsDrawnTests
{
    [Fact]
    public async Task A_task_the_agent_names_at_the_same_count_is_drawn_on_its_row_and_in_its_list()
    {
        var registry = new ScriptedRegistry { Reply = Rows("Count to three", said: false) };
        using var driving = Driving.Open();
        using var directory = new BrokerDirectory(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1", new GrpcChannelOptions { HttpHandler = registry, DisposeHttpClient = false }));
        using var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        await AgentsOnceAsync(shell);
        Assert.Contains(Drawn.Rows(shell.Agents.List), row => row.Contains("— Count to three", StringComparison.Ordinal));

        // The agent names its task: the same one task, the same standing, its own words.
        registry.Reply = Rows("Checking VAT on gift cards", said: true);
        await AgentsOnceAsync(shell);

        var rows = Drawn.Rows(shell.Agents.List).ToList();
        Assert.True(
            rows.Any(row => row.Contains("— Checking VAT on gift cards", StringComparison.Ordinal)),
            "the row still says: " + string.Join(" | ", rows));
        Assert.DoesNotContain(rows, row => row.Contains("Count to three", StringComparison.Ordinal));

        shell.Focus(2);
        shell.Agents.List.SelectedItem = rows.FindIndex(row => row.Contains("billing@test", StringComparison.Ordinal));
        driving.Press(Key.B.WithAlt);

        var view = Assert.IsType<BackgroundWorkView>(shell.BackgroundShown);
        Assert.Equal(["1. Checking VAT on gift cards (sub-agent)"], view.Shown);

        Prompting.Close(driving.App, shell.BackgroundSession);
    }

    private static async Task AgentsOnceAsync(Shell shell)
    {
        var once = typeof(Shell).GetMethod("AgentsOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Shell.AgentsOnceAsync is gone; this test must find the pane's refresh again.");

        await (Task)once.Invoke(shell, [TestContext.Current.CancellationToken])!;
    }

    private static Wire.RegistryReply Rows(string taskName, bool said)
    {
        var row = new Wire.RegistryRow
        {
            Name = "billing@test", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running, Connected = true, TurnsReported = true, LastTurnClean = true,
            LastTurnEndedUtc = DateTimeOffset.UtcNow.AddMinutes(-1).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            BackgroundTasks = 1,
        };
        row.RunningTasks.Add(new Wire.BackgroundTaskRow { Id = "a1", Kind = Wire.TaskKind.Agent, Name = taskName, Said = said });

        var reply = new Wire.RegistryReply();
        reply.Rows.Add(row);

        return reply;
    }

    /// <summary>Answers DescribeRegistry from a script a test can change between reads; anything else is UNIMPLEMENTED.</summary>
    private sealed class ScriptedRegistry : HttpMessageHandler
    {
        public Wire.RegistryReply Reply { get; set; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            IMessage? reply = request.RequestUri!.AbsolutePath == "/parley.v1.Operators/DescribeRegistry" ? Reply : null;
            var bytes = reply?.ToByteArray() ?? [];
            var framed = new byte[reply is null ? 0 : 5 + bytes.Length];

            if (reply is not null)
            {
                BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(1, 4), (uint)bytes.Length);
                bytes.CopyTo(framed, 5);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                RequestMessage = request,
                Content = new ByteArrayContent(framed),
            };

            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");

            // 12 is UNIMPLEMENTED.
            response.TrailingHeaders.Add("grpc-status", (reply is null ? 12 : 0).ToString(CultureInfo.InvariantCulture));

            return response;
        }
    }
}
