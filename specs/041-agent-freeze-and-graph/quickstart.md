# Quickstart: feature 041

1. Start a throwaway deployment with two projects that serve one agent.
2. Open any console page; open the graph tab: projects, agents, conversations, spin-offs and background counts appear.
3. Pin it, change page: it stays open.
4. Freeze the agent from its node: the confirmation names both projects; confirm. The turn ends, the process ends, the agent reads
   *frozen* in both.
5. Restart the broker: the freeze is lifted and the agent is started for its open exchange. Freeze it again with *Do not
   unfreeze on logon* ticked and restart the broker: this time it is not started, and its exchange is still open.
6. Send it a request: stored, not started.
7. Unfreeze: it is started and collects the request.
