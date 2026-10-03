using Google.Protobuf.Reflection;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That nothing an agent can call returns anything about anybody's turns (SPEC.md §11.6, §10.1, specs/024 FR-005).
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
public sealed class AgentsCannotSeeTurnsTests
{
    private static readonly string[] Forbidden = ["turn", "permission_denial", "permission_refus"];

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
        }
    }
}
