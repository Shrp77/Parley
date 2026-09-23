# Quickstart: checking the file store end to end

What a person does to see the feature working once it is built. Two agents, `billing` and `orders`, in project `alpha`,
with a conversation `tax-inclusive-pricing` open between them. The location accepts only `.md` and `.txt` files; code
travels as `Name.cs.txt`.

## 1. Hand a file across

In `billing`'s session:

> Place `src/Pricing/PriceSnapshot.cs` in Parley for the pricing work, as `PriceSnapshot.cs.txt`, and place
> `docs/price-snapshot.md` beside it.

The agent calls `parley_file_place` twice; each reply names the conversation, the size and a sha256. It sends `orders` a
Request naming both files and their hashes.

In `orders`' session, after collecting:

> Fetch the two files billing placed into `vendor/pricing/`, expecting the hashes it named.

`parley_file_fetch` writes `vendor/pricing/PriceSnapshot.cs.txt` and `vendor/pricing/price-snapshot.md`;
`sha256sum`/`Get-FileHash` on each matches. `orders` renames its own copy of the class as it sees fit.

## 2. See it

```
parley files list --project alpha
```

Two rows under `tax-inclusive-pricing`: name, size, hash, `billing`, time; totals; `billing: 2 placements`.

## 3. Change it and watch the notice

On the broker's machine:

```
parley files open price-snapshot.md --project alpha
```

It opens in the editor registered for `.md`. Correct a line and save. Then:

```
parley files list --project alpha
```

A new hash and size, still placed by `billing`. `orders`' next `parley_inbox` holds one `file_notices` entry: the hash it
had, the hash now. `orders` was not woken.

From any other machine the same `open` prints `price-snapshot.md is on machine <broker>, at <path>` and launches nothing.

## 4. Replace and take back

`billing` places a revised `PriceSnapshot.cs.txt`: reply `replaced`, old and new hashes. Placing the same bytes again:
`already_current`. `orders` tries `parley_file_remove PriceSnapshot.cs.txt`: refused, placed by `billing`. `billing`
removes it: gone, and `orders`' next inbox says so.

## 5. Read-only during a download

Place a large `.txt` file (a log of a few hundred megabytes). Start a fetch of it from `orders`, and while it runs save the
same file in an editor on the broker's machine. On Windows the save is refused; on Linux and macOS the fetch fails saying
the file changed. Either way no mixed file lands in `orders`' repository.

## 6. Clean up

```
parley files folder --conversation tax-inclusive-pricing --project alpha
parley files prune --project alpha
```

The folder opens (or its machine and path are printed); prune describes what goes, takes `alpha` typed back, and the list
is empty afterwards. `parley open` shows the conversations unchanged.

## 7. The guards

- Place `run.bat`, `tool.exe` or `PriceSnapshot.cs`: refused, with the advice to add `.txt`.
- Copy `setup.exe` into a conversation's folder by hand: `parley files list` does not show it and it cannot be fetched or
  opened.
- Place a file named `../escape.txt`: refused by the name rules.
- `parley_file_fetch` with `to` outside the repository: refused.
