# Quickstart: checking 037 by hand

Use a test deployment, never the live one (reading the live console has effects).

1. Start a broker and the web console on a scratch root. Seat an agent `orders` in a conversation.
2. As `orders`, place `mockup.png` (a real PNG) and `notes.md` in the conversation.
3. Open the conversation in the web console. A thumbnail of `mockup.png` and a tile for `notes.md` sit where they were
   placed. Click the thumbnail: the image opens full size; Escape closes it.
4. Open Media from the conversation's actions: one image in the grid, one file in the list, newest first.
5. Place `fake.png` holding `<html><script>alert(1)</script>`. Its thumbnail becomes "not a readable image". Requesting
   the image handler URL directly answers 415.
6. Tick two agents in the To box. Two chips show; clicking an x removes one and clears its tick.
7. In `parley-terminal`, open the conversation: a line says `orders placed mockup.png (image, …)`. `Alt+e` lists the
   media; Enter opens one in the machine's own program.
