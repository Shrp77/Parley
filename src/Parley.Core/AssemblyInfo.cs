using System.Runtime.CompilerServices;

// Parley.Store needs Exchange.MarkSaved to complete a save. Nothing else should be able to
// call it: adopting a version stamp outside the persistence layer silently defeats the
// optimistic concurrency check, and a documented hazard is still a hazard. Making the method
// internal turns the rule from advice into something the compiler enforces.
[assembly: InternalsVisibleTo("Parley.Store")]
