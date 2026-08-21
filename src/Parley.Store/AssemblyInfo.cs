using System.Runtime.CompilerServices;

// Parley.Store.Tests needs GhostNotice to construct the notice the reaper will write, so it can
// plant that exact row and check what the reaper does when its append is deduplicated. The
// alternative is a test that reproduces the payload by hand, which would pass while asserting
// that two copies of the same construction agree with each other.
//
// GhostNotice stays internal: nothing outside this assembly composes a §14 notice, and a public
// payload builder invites a second writer of the one message type the schema reserves to the
// broker.
[assembly: InternalsVisibleTo("Parley.Store.Tests")]
