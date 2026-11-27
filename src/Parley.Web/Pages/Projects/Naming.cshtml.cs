using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// How to rename a project on this machine.
/// </summary>
/// <remarks>
/// <para>
/// Instructions rather than a form: a project is renamed only in the terminal, as it is removed only there, though
/// the console creates one and enrols repositories through <c>Parley.Setup</c>. A rename makes a new store and leaves
/// the old one's agents behind, which is a destructive act and belongs with the other destructive acts in one place.
/// </para>
/// <para>
/// <b>This used to say the file is "written by exactly one" program, and that was already untrue
/// when it was written.</b> Two processes write it: <c>parley</c> from a shell and
/// <c>parley-terminal</c> from a screen, both through <c>Parley.Setup</c>, which is one
/// *implementation* rather than one writer. What makes that safe is not exclusivity but the lock
/// — <c>ProjectManifest.TryUpdate</c> holds <c>projects/.lock</c> with no sharing, and the port
/// ledger does the same. The distinction matters because the old wording made this page look like
/// a structural impossibility when it is a decision: adding the reference is all that stands
/// between here and a form, so the reason to keep it out has to be an argued one.
/// </para>
/// <para>
/// <b>The argued one is that renaming is not worth a second caller.</b> It is rare, it is two
/// commands, and the page below gives them. Where a surface genuinely needs to write, the shape
/// is to call <c>Parley.Setup</c> rather than to write here — that keeps the one implementation
/// and costs the console nothing on its dependency graph, since <c>Parley.Setup</c> reaches only
/// <c>Configuration</c> and <c>Transport</c> and no store.
/// </para>
/// <para>
/// The naming is a fact about this machine and nothing else. §4.1 gives the protocol no concept
/// of a project at all — membership is which endpoint an agent connected to — so renaming
/// changes what this machine calls a deployment and reaches nothing on the wire. No agent
/// notices, no history moves, and nothing needs restarting.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class NamingModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>A new name the reader typed in the project page's title box, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Only carried here. Nothing is written: the box on the project page hands the name to the page that
    /// describes what a rename does, so the console still never writes the layout.
    /// </remarks>
    [BindProperty(SupportsGet = true)]
    public string? NewName { get; set; }

    /// <summary>Whether <see cref="NewName"/> is a name a project can have.</summary>
    public bool NewNameValid => Parley.Core.ProjectName.TryCreate(NewName, out _);

    /// <summary>Where that project's broker listens.</summary>
    public string Endpoint { get; private set; } = string.Empty;

    /// <summary>The store that broker owns, when this machine's manifest names one.</summary>
    public string Store { get; private set; } = string.Empty;

    /// <summary>Whether this project is named in a manifest at all.</summary>
    /// <remarks>
    /// A machine with no project reaches a broker by address, and the console offers that broker
    /// under a stand-in name. There is nothing to rename in that case —
    /// what is wanted is the first naming, which is a different command and a different sentence.
    /// </remarks>
    public bool Named { get; private set; }

    /// <summary>Reads what this machine knows about the project.</summary>
    /// <returns>The page, or a redirect when the project is not one this machine knows.</returns>
    public IActionResult OnGet()
    {
        var project = directory.Find(Project);

        if (project is null)
        {
            return RedirectToPage("/Index");
        }

        Endpoint = project.Endpoint;
        Store = project.Store;
        Named = project.Store.Length > 0;

        return Page();
    }
}
