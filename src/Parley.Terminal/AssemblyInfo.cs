using System.Runtime.CompilerServices;

// The surface is internal and its tests drive it directly. That is not a convenience: what makes
// every driven test in this suite deterministic is reaching Shell's own work queue and waiting on
// it, and a test that could only press keys would be waiting on a timer instead.
//
// The same grant Parley.Cli makes to its own tests, for the same reason: a seam opened for checks
// that could not otherwise be written, and not a general invitation.
[assembly: InternalsVisibleTo("Parley.Terminal.Tests")]
