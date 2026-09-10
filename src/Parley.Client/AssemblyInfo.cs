using System.Runtime.CompilerServices;

// Opened for one seam and named as such rather than as a general invitation, which is the same
// terms Parley.Cli, Parley.Setup and Parley.Top each grant on.
//
// What needed it: the sentences a send reports back. Every one of them is derivable only by
// standing a broker up and putting a real recipient into each of four states -- running, closed,
// already being seen to, and on a machine with no node -- and two of those cannot be reached from
// a test at all, because they depend on what a node said it could launch. Checking the words
// directly is the difference between asserting that none of them reads as a failure and hoping so.
[assembly: InternalsVisibleTo("Parley.Client.Tests")]
