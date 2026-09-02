using System.Runtime.CompilerServices;

// A seat's bookkeeping is internal, and it stayed internal through the move.
//
// SeatHold.Record, Settled and DismissRead mutate what a person has taken delivery of. They were
// never public: the console could call them because the console and the seat were one assembly,
// and the boundary that kept everybody else out was the assembly wall. Moving the seat here moved
// that wall, and there are exactly four call sites on the other side of it.
//
// Granted rather than published, because the alternative was making them public, and public here
// means anything holding a SeatHold can desynchronise a person's delivered set from what the
// broker believes it sent. The grant keeps the members reachable by the one caller they were
// always written for, and keeps them unreachable by everything else -- the same trade
// Parley.Core makes to Parley.Store, and not a general invitation.
//
// It is also a signal rather than a solution. That two surfaces will drive a seat's bookkeeping
// from their own code is how they come to disagree about what a person has seen, which is the
// class of failure SC-003 names. The right fix is for the drive itself -- drain then record,
// answer then settle -- to live here beside the state it changes, so neither surface owns a copy.
// That is a design change and belongs with the terminal's own seating work (US2), not inside a
// move whose whole contract is that behaviour did not change.
[assembly: InternalsVisibleTo("Parley.Web")]
