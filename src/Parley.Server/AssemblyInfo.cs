using System.Runtime.CompilerServices;

// Opened for one seam, on the same terms as the grants Parley.Cli, Parley.Setup, Parley.Terminal and
// Parley.Client each make.
//
// What needs it: the wake bound's state. Whether a turn is already coming for an agent, and how
// long a claim stands, are decisions this class makes and nothing outside it acts on, so they stay
// internal and the tests see the state they have to assert on.
[assembly: InternalsVisibleTo("Parley.Server.Tests")]
