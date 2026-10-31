using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That nothing a form posts can be supplied by the address the form posts to.
/// </summary>
/// <remarks>
/// <para>
/// <b>A form posts back to its page's own address, query included</b>, and a field the form leaves out (no box ticked,
/// an unticked checkbox) would otherwise bind from that query. An agent's message is drawn with live links, so a link
/// carrying <c>Targets=</c> or <c>ConfirmRemoval=true</c> chose who the person's send reached, or skipped a description,
/// while the page showed otherwise. So every property bound on a POST only is <c>[FromForm]</c>, and every POST
/// handler's parameter names where it comes from.
/// </para>
/// <para>
/// A property bound on a GET as well (<c>SupportsGet</c>) is the page's identity or a filter, which a link is meant to
/// carry and the form sends again.
/// </para>
/// </remarks>
[Integration]
public sealed class FormBindingTests
{
    private static IEnumerable<Type> Pages() =>
        typeof(Parley.Web.ConsoleHost).Assembly.GetTypes().Where(t => typeof(PageModel).IsAssignableFrom(t) && !t.IsAbstract);

    [Fact]
    public void Every_property_bound_on_a_post_only_is_bound_from_the_form()
    {
        // Must fail against a [BindProperty] without SupportsGet that lacks [FromForm].
        var loose = Pages()
            .SelectMany(page => page.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() is { SupportsGet: false }
                            && p.GetCustomAttribute<FromFormAttribute>() is null)
                .Select(p => $"{page.Name}.{p.Name}"))
            .ToList();

        Assert.True(loose.Count == 0, "Bound on a POST and not [FromForm]: " + string.Join(", ", loose));
    }

    [Fact]
    public void Every_post_handlers_parameter_names_where_it_is_bound_from()
    {
        // Must fail against an OnPost parameter with no binding source, which would take a value from the query.
        var loose = Pages()
            .SelectMany(page => page.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.StartsWith("OnPost", StringComparison.Ordinal))
                .SelectMany(m => m.GetParameters()
                    .Where(p => p.ParameterType != typeof(CancellationToken)
                                && !p.GetCustomAttributes().OfType<IBindingSourceMetadata>().Any())
                    .Select(p => $"{page.Name}.{m.Name}({p.Name})")))
            .ToList();

        Assert.True(loose.Count == 0, "A POST handler's parameter with no binding source: " + string.Join(", ", loose));
    }

    [Fact]
    public void The_check_finds_the_pages()
    {
        // Guarded, because a reflection scan that found no page would pass both tests above by checking nothing.
        Assert.Contains(Pages(), p => p.Name == "DetailModel");
        Assert.True(Pages().Count() > 10);
    }
}
