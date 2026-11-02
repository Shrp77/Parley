# Quickstart: check it by hand

1. Copy a store at schema 4, and start a broker on the copy. Compare row counts per table.
2. Have an agent send a 100,000-character `parley_result`. The reply has `long_message`.
3. Have the requester call `parley_inbox`. In Claude Code the result arrives through a file, and the agent reads it whole.
4. In the terminal, open a conversation, paste 20,000 characters and press Enter. It is sent.
5. Paste 600,000 characters and press Enter. It is refused, and the text is still in the box. Do the same in the web console.
6. Open the health report on both. Every number agrees with the messages sent.
7. Prune a conversation. The numbers do not fall.
