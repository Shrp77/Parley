using System.Runtime.CompilerServices;

// Opened for one seam and named as such rather than as a general invitation, on the terms the other projects grant.
//
// What needed it: how long a runner's line takes to read. A line nested 200,000 deep must read in milliseconds, and
// through a process the time is lost among the process's own; the reader is asked directly instead.
[assembly: InternalsVisibleTo("Parley.Headless.Tests")]
