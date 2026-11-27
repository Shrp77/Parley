# Quickstart: taking this feature's screenshot rounds

From the repository's root.

## See the console as it is today, at every size

```
dotnet run --project tools/Parley.Screens -- capture tools/Parley.Screens/rounds/042-baseline
```

The PNGs land in `artifacts/screens/042-baseline/`: each busiest page at phone, tablet portrait, tablet landscape and
desktop sizes, in light and dark.

## Compare a direction

A direction is a tokens file under `specs/042-theme-and-layout-refresh/directions/`. Its round names it in `shots.json`:

```json
{ "theme": "dark", "scale": 1, "maximumBytes": 0,
  "stylesheets": [ "../../../../specs/042-theme-and-layout-refresh/directions/a.css" ],
  "shots": [ ... ] }
```

```
dotnet run --project tools/Parley.Screens -- check tools/Parley.Screens/rounds/042-direction-a
dotnet run --project tools/Parley.Screens -- capture tools/Parley.Screens/rounds/042-direction-a
```

Before showing a direction, run the contrast test against it:

```
pwsh scripts/test.ps1 -Project Web
```

## Look around by hand

```
dotnet run --project tools/Parley.Screens -- hold tools/Parley.Screens/rounds/042-baseline
```

Open the printed address in a browser, and use its device toolbar to try other widths.

## After each pass

Take the same round again and send the person the before and after sets side by side, in the conversation
`UI-refresh-and-fixes`.
