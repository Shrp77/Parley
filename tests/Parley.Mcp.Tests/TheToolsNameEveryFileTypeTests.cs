using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Parley.Core;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>That the file tools tell an agent exactly the extensions a placed file may have.</summary>
/// <remarks>
/// The descriptions name the extensions by hand rather than from <see cref="SharedFileName.AllowedExtensions"/>, because an
/// attribute's text must be a constant. A type added to the list and not to a description is one no agent is told it may
/// send; one left in a description after leaving the list is refused every time an agent believes it.
/// </remarks>
[Unit]
public sealed partial class TheToolsNameEveryFileTypeTests
{
    // A run of extensions as a sentence lists them: ".md, .txt and .webp" or ".md, .txt or .webp".
    [GeneratedRegex(@"\.[a-z0-9]+(?:, \.[a-z0-9]+)* (?:and|or) \.[a-z0-9]+")]
    private static partial Regex ListOfExtensions();

    public static TheoryData<string> Described => new()
    {
        "parley_file_place",
        "parley_file_place name",
        "parley_file_fetch",
    };

    [Theory]
    [MemberData(nameof(Described))]
    public void A_file_tool_names_every_allowed_extension_and_no_other(string described)
    {
        // Without this, an extension added to SharedFileName.AllowedExtensions, or dropped from it, leaves a tool
        // description telling agents the old list (review, lens 3 round 33).
        var text = Description(described);
        var lists = ListOfExtensions().Matches(text)
            .Select(match => Regex.Matches(match.Value, @"\.[a-z0-9]+").Select(found => found.Value).ToList())
            .Where(list => list.Intersect(SharedFileName.AllowedExtensions, StringComparer.Ordinal).Any())
            .ToList();

        var named = Assert.Single(lists);

        Assert.Equal(SharedFileName.AllowedExtensions, named);
    }

    private static string Description(string described)
    {
        var (tool, parameter) = described.Split(' ') is [var name, var argument] ? (name, argument) : (described, null);
        var method = typeof(ParleyTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(candidate => candidate.GetCustomAttributes<ModelContextProtocol.Server.McpServerToolAttribute>()
                .Any(attribute => attribute.Name == tool));

        ICustomAttributeProvider holder = parameter is null
            ? method
            : method.GetParameters().Single(candidate => candidate.Name == parameter);

        return holder.GetCustomAttributes(typeof(DescriptionAttribute), inherit: false)
            .Cast<DescriptionAttribute>()
            .Single()
            .Description;
    }
}
