using Google.Protobuf.Reflection;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That no reply of the <c>Agents</c> service carries a message or an enum whose type is turn information, whatever the
/// field holding it is called.
/// </summary>
/// <remarks>
/// <see cref="AgentsCannotSeeTurnsTests"/> reads field and enum value names. A field named <c>current</c> of type
/// <c>TurnStarted</c>, or <c>work</c> of type <c>BackgroundWork</c> (both the node's turn report, SPEC.md §11.6), carries a
/// turn's start or an agent's background work to an agent and passed it, because neither name nor the inner fields'
/// names (<c>started_utc</c>, <c>tasks</c>) hold a forbidden word. The type's own name does.
/// </remarks>
[Unit]
public sealed class AgentsRepliesCarryNoTurnTypeTests
{
    private static readonly string[] Forbidden = ["turn", "permission_denial", "permission_refus", "background", "working", "busy"];

    [Fact]
    public void No_reply_the_Agents_service_gives_holds_a_turn_type()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<string>();

        foreach (var method in Wire.Agents.Descriptor.Methods)
        {
            Walk(method.OutputType, method.Name, seen, found);
        }

        Assert.True(found.Count == 0, "an agent could read a turn type through: " + string.Join(", ", found));
    }

    [Fact]
    public void The_node_report_does_hold_one()
    {
        // Not vacuous: the same reading finds the turn types where they belong.
        var found = new List<string>();

        Walk(Wire.TurnReport.Descriptor, "ReportTurn", new HashSet<string>(StringComparer.Ordinal), found);

        Assert.Contains(found, where => where.EndsWith(":TurnStarted", StringComparison.Ordinal));
        Assert.Contains(found, where => where.EndsWith(":BackgroundWork", StringComparison.Ordinal));
    }

    private static void Walk(MessageDescriptor message, string path, HashSet<string> seen, List<string> found)
    {
        if (!seen.Add(message.FullName))
        {
            return;
        }

        foreach (var field in message.Fields.InFieldNumberOrder())
        {
            var where = $"{path}.{message.Name}.{field.Name}";

            var type = field.FieldType switch
            {
                FieldType.Message => field.MessageType.Name,
                FieldType.Enum => field.EnumType.Name,
                _ => null,
            };

            if (type is not null && Forbidden.Any(word => type.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add($"{where}:{type}");
            }

            if (field.FieldType == FieldType.Message)
            {
                Walk(field.MessageType, path, seen, found);
            }
        }
    }
}
