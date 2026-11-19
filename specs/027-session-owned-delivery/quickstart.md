# Quickstart: seeing the fix

## The race, as a test

```
dotnet build
tests/Parley.Broker.Tests/bin/Debug/net10.0/Parley.Broker.Tests.exe -class Parley.Broker.Tests.SessionOwnedDeliveryTests
tests/Parley.Server.Tests/bin/Debug/net10.0/Parley.Server.Tests.exe -class Parley.Server.Tests.FastRestartTests
```

What the broker test does, with no timing in it:

1. `billing` sends `orders` a Request.
2. Session A of `orders` attaches and collects it.
3. Session B of `orders` attaches **while A is still attached** — the broker has not heard A leave.
4. B collects: nothing (A still holds the work — the two-windows rule).
5. A detaches.
6. B collects: the Request, ahead of anything new.

Before the change, step 6 returns nothing. Mutation check: delete the give-back step and watch step 6
fail.

The same class holds the other rules, each with no timing:

- **Hand back:** A collects; A hands back; B (opened earlier) collects and receives it.
- **Reclaim:** A collects; A's stream drops; A′ attaches naming A; A′ collects nothing new and holds it.
- **Older window:** B opens; A collects; A's stream drops; B collects nothing.
- **Broker restart, two windows:** A and B each collect one Request; the broker restarts; B′ reclaims
  B, then A′ reclaims A (and the reverse order); each holds only its own.

## By hand

1. `parley up` in a project with two agents.
2. Have one agent send the other a Request, and let the receiver collect it without answering.
3. Close the receiver's editor and reopen it immediately.
4. The reopened session's first `parley_inbox` shows the Request.
5. With two windows open on the receiver, collect in one: the other does not show it.
6. Stop and restart the broker with both windows open: each window keeps its own work.
7. Close one window normally: the other window's next `parley_inbox` shows that window's work. If it
   does not, the editor killed the adapter rather than shutting it down; record that in research R11.
