using Google.Protobuf.Reflection;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That what agents read of the past is shown to the person and to nobody else (SPEC.md §5.8).
/// </summary>
/// <remarks>
/// The record of a read rides on the operator's read of a conversation. A reply on the Agents service that reached it would
/// tell one agent who else had been reading, which is the person's to know. Walked through the protocol's own descriptors,
/// as AgentsCannotSeeTurnsTests walks them, so a field added somewhere nobody predicted is still found.
/// </remarks>
[Unit]
public sealed class AgentsNeverSeeWhoReadTests
{
    [Fact]
    public void No_reply_the_Agents_service_gives_reaches_a_record_of_a_read()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reached = new List<string>();

        foreach (var method in Wire.Agents.Descriptor.Methods)
        {
            Walk(method.OutputType, method.Name, seen, reached);
        }

        Assert.True(reached.Count == 0, "an agent could read who has read what through: " + string.Join(", ", reached));
    }

    [Fact]
    public void The_operators_read_of_a_conversation_does()
    {
        var reached = new List<string>();

        Walk(Wire.ExportReply.Descriptor, "Export", new HashSet<string>(StringComparer.Ordinal), reached);

        Assert.Contains(reached, path => path.EndsWith(".reads", StringComparison.Ordinal));
    }

    private static void Walk(MessageDescriptor message, string path, HashSet<string> seen, List<string> reached)
    {
        if (!seen.Add(message.FullName))
        {
            return;
        }

        foreach (var field in message.Fields.InFieldNumberOrder())
        {
            if (field.FieldType != FieldType.Message)
            {
                continue;
            }

            if (field.MessageType.Name == "HistoryReadRow")
            {
                reached.Add($"{path}.{message.Name}.{field.Name}");
            }

            Walk(field.MessageType, path, seen, reached);
        }
    }
}
