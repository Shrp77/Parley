using Google.Protobuf.Reflection;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That nothing an agent can call returns anything about anybody's turns (SPEC.md §11.6, §10.1).
/// </summary>
/// <remarks>
/// <para>
/// How another agent's turns went — what they cost, how long they ran, what they were refused — is the operator's view.
/// The registry row carries it; the roster an agent reads must not, and a field added there "because the data was right
/// there" is the leak this guards.
/// </para>
/// <para>
/// <b>Walked through the protocol's own descriptors, every reply and every message inside it</b>, rather than by naming
/// the one reply that worries us today: the mistake being guarded is a field added somewhere nobody predicted. Matched
/// on the word, as <c>AgentsCannotSetTheirOwnLineTests</c> matches its words, so a field named <c>last_turn</c> or
/// <c>permission_denials</c> fails it whatever message it lands in.
/// </para>
/// </remarks>
[Unit]
public sealed class AgentsCannotSeeTurnsTests
{
    // "background" since 039 put an agent's background work in the turn standing (RegistryRow.background_tasks); "working"
    // and "busy" because that is how a surface says a turn is running. Enum values are read too: a presence of WORKING is
    // turn information however its field is named.
    // "running_task" and "task_kind" since 043 named each background task on the registry row (RegistryRow.running_tasks).
    private static readonly string[] Forbidden =
        ["turn", "permission_denial", "permission_refus", "background", "working", "busy", "running_task", "task_kind"];

    [Fact]
    public void No_reply_the_Agents_service_gives_mentions_a_turn()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<string>();

        foreach (var method in Wire.Agents.Descriptor.Methods)
        {
            Walk(method.OutputType, method.Name, seen, found);
        }

        Assert.True(found.Count == 0, "an agent could read about turns through: " + string.Join(", ", found));
    }

    [Fact]
    public void The_operators_registry_does_carry_it()
    {
        // The other half, so the guard above is not passing because the walk reads nothing: the same words are found where
        // they belong.
        var found = new List<string>();

        Walk(Wire.RegistryReply.Descriptor, "DescribeRegistry", new HashSet<string>(StringComparer.Ordinal), found);

        Assert.Contains(found, field => field.EndsWith(".turns_reported", StringComparison.Ordinal));
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

            if (Forbidden.Any(word => field.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(where);
            }

            if (field.FieldType == FieldType.Message)
            {
                Walk(field.MessageType, path, seen, found);
            }

            if (field.FieldType == FieldType.Enum)
            {
                found.AddRange(field.EnumType.Values
                    // NOT_YOUR_TURN is whose turn it is to speak in an exchange, not a process's turn.
                    .Where(value => !value.Name.EndsWith("_NOT_YOUR_TURN", StringComparison.Ordinal))
                    .Where(value => Forbidden.Any(word => value.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                    .Select(value => $"{where}={value.Name}"));
            }
        }
    }
}
