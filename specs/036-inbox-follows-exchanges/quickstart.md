# Quickstart: checking 036 by hand

Run against a test deployment, never the live console: reading `/Inbox` delivers mail.

1. Have an agent ask the person a question. It is listed under **Waiting on you**.
2. Reply to it. It moves to **In flight** with a chip (*Working* while the agent's turn runs, then *Idle*).
3. When the agent asks back, it is under **Waiting on you** again. When it sends its Result, it is under **Closed** with
   the outcome.
4. Send a Result on an agent's request. It goes straight to **Closed**, with the outcome chosen.
5. Under **Closed**, write a follow-up. A new Request reaches the agent in the same conversation and is listed under
   **In flight**.
6. In Settings, set the closed window to 1 day. Older exchanges leave **Closed** and are listed under **Show older**.
   Open the terminal's Inbox: it covers 1 day too.
7. Have an agent write "thanks for exchange #N". Click `#N`: a panel opens under that line with the exchange. Click it
   again: it closes. "on N" with no prefix is not a link.
8. In the terminal room, put the cursor on that message. *Open exchange #N* is on the status line; press it.
