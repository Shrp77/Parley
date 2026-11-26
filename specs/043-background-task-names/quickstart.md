# Quickstart: seeing named background tasks

1. Build and install as usual (`install.ps1 -Force -Hidden`). Node and broker must be one build: protocol 0.21.
2. Send an agent a request that makes it start background work, such as "Look into these three things in parallel."
3. Open the web console's graph. Under the agent, each task shows its name: the agent's own, if it called
   `parley_describe_background`, or else the runner's short label. Hover a task for its description; press its name to
   follow its link.
4. Open the agent's page. *Background work* lists every task with its name, description and link, five showing before
   the list scrolls, twenty to a page.
5. In the terminal, the agent's row shows the first task's name and how many more. *Background work* in the row's menu
   lists them all.
6. When the work finishes, each task leaves every surface within a few seconds.

Checks by hand that no test makes:
- A real runner's task names appear (the tests replay recorded lines).
- An https link opens in a new tab and shows its host first.
