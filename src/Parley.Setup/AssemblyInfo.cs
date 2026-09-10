using System.Runtime.CompilerServices;

// **The surface is what the two tools use, and the tests see the rest through here.**
// `Parley.Setup` is referenced by `parley` and by `parley-top` and by nothing else, so a type
// neither of them names was public for the tests' benefit — which is a surface widened by the
// checking rather than by the shipping, and `SetupSurfaceTests` then records it as if considered.
// Granting the internals is what the rest of this repository does: Parley.Cli, Parley.Core,
// Parley.Store and Parley.Top each do exactly this.
//
// Parley.Top.Tests is granted as well as Parley.Cli.Tests because both drive enrolment: the
// terminal's own tests reach the writer directly, which is the point of the terminal not
// reimplementing it.
[assembly: InternalsVisibleTo("Parley.Cli.Tests")]
[assembly: InternalsVisibleTo("Parley.Top.Tests")]
