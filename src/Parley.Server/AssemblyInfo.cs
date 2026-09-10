using System.Runtime.CompilerServices;

// Opened for one seam, on the same terms as the grants Parley.Cli, Parley.Setup, Parley.Top and
// Parley.Client each make.
//
// What needed it: the wake bound's state. Whether a turn is already coming for an agent, and how
// long a claim stands, are decisions this class makes and nothing outside it acts on — so they were
// public for no reason but that a test read them, which is a surface existing for its checks.
// Internal and granted is the honest shape: the tests still see the state they have to assert on,
// and nothing else can start depending on it.
[assembly: InternalsVisibleTo("Parley.Server.Tests")]
