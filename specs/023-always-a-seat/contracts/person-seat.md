# Contract: the person seat

What every participant, surface and implementation may rely on.

## Identity

- Every project a broker serves has exactly one person seat, `human-user@<project>`, from the moment it is
  served. The part after `@` is the project; the person seat has no machine.
- The facilitator is `parley-facilitator@<project>`, by the same rule, and has no machine either.
- The role `human-user` is reserved. Enrolment, attach and storage each refuse it to an agent, with the sentence
  *"`human-user` is the person's seat in every project, so no agent can be called that."* An attach that reports
  the seat's own adapter is admitted as the person: see the spec's *What the reservation does not do*.
- The person seat is never retired and never purged. Storage holds both: `tr_person_is_never_retired` and
  `tr_person_is_never_purged` abort with *"the person seat cannot be retired"* and *"… purged"*. `ask_again`
  refuses it with a sentence saying it is a person's seat.
- `reinstate` does **not** refuse it with a sentence. *Planned:* every operation above refused it with a
  sentence saying it is the person. *As built:* `Operators.Reinstate` changes only an agent that is retired,
  and the seat never is, so over the wire it returns a `DidItReply` with `changed = false` and no sentence. A
  surface that offers reinstating the person must say why nothing happened itself; the broker does not.
- The person seat is never the facilitator, and neither can take the other's role.

## Presence

| reported as | when | means |
|---|---|---|
| `here` | at least one surface attached as the person and used in the last 20 minutes | the person is at a screen |
| `away` | otherwise | the person is not at a screen; the seat is still there |

The person seat is never reported as `running`, `startable`, `stranded` or `retired`. Presence never gates
routing.

The facilitator, which also has no machine, is reported as `running` whenever its project is served, never as
`stranded`. SPEC.md §14: *"It is present whenever its project is served and absent otherwise — reported as
`running` wherever a presence is returned for it, since it has no machine to be connected or not."* For the
person, §14 says: *"The person is reachable whenever the project is served (§12.3), and whether they are at a
screen is the only thing a connection says about them."* Every surface leaves it out of its lists
anyway; this is what `registry()` returns.

## Sending to the person

- A Request, Clarify or Result to the person is admitted whether they are here or away.
- A Request's reply carries `wake = WITH_THE_PERSON`. Nothing asks any machine to start the person.
- What an agent is told (`parley_request`'s `receiver_is`): *"with the person: they have it and will answer.
  They may be away; waiting for their decision is correct."* No surface ever sends to the person, since the
  person is the one sending, so `SeatVoice` needs no sentence for it.
- Mail waits until the person collects it. Nothing expires it.

## Standing instructions to agents

Replacing the *"if nobody is seated"* clause in `ParleyInstructions` and in `parley_result`'s description:

> If no agent can provide it, ask the person — the participant whose role is `human-user`. They may be away. Your
> message waits for them, and waiting for their decision is correct; it is not a reason to report blocked.

## Surfaces

- Both surfaces act as the project's person seat as soon as they open, in every project, with no join step. The
  name comes from the registry row, never from anything the surface knows about itself.
- A surface drops its stream after 20 minutes without activity, and re-attaches on the next activity without
  telling the person anything. Nothing on either surface says the seat was released.
- Both show how much is waiting for the person without a conversation being opened.
- A flag whose owing participant is the person reads *"waiting on you: <asker> asked …"* and offers no
  "ask the agent" remedy.
- If a project's registry holds no person seat (an older broker), or cannot be read, the surface says so plainly
  and does not act as anyone in that project.
