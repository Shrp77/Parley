using System.Runtime.CompilerServices;

// The node's command handling is internal, and one case of it cannot be reached from outside:
// what happens when the broker asks for a turn for a resident whose process has died. That window
// -- the broker still believes a session is attached, the process behind it has gone -- is exactly
// the silent drop this command exists to remove, so it is worth a test rather than an argument.
//
// The same grant Parley.Core makes to Parley.Store, for the same reason: a seam opened for a check
// that could not otherwise be written, and not a general invitation.
[assembly: InternalsVisibleTo("Parley.Cli.Tests")]
