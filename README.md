# Jellytriggers

A Jellyfin plugin that surfaces content-warning info from
[doesthedogdie.com](https://www.doesthedogdie.com/) on the movie detail page,
filtered to the trigger categories each viewer has favorited.

> Status: early scaffolding. Plan and design notes live in
> [`CLAUDE.md`](./CLAUDE.md).

## What it does

When you open a movie in Jellyfin's web client, Jellytriggers shows a small
pane listing only the DTDD questions you've marked as favorites on
doesthedogdie.com — e.g. *"Does the dog die — Yes (202 vs 11)"*. Anything you
haven't favorited stays hidden. A refresh control on the pane (and a bulk
"Refresh my triggers everywhere" button in user settings) lets you pull fresh
data after changing your favorites on DTDD.

Out of scope for v1: TV shows, native/mobile Jellyfin clients (web only),
multi-language category labels, episode-level granularity.

## Building (planned)

```bash
dotnet build Jellyfin.Plugin.Jellytriggers.sln
dotnet publish -c Release Jellyfin.Plugin.Jellytriggers/
```

## License

GPL-3.0 (Jellyfin plugins link against GPL-3.0 binaries; see
[the template's licensing notes](https://github.com/jellyfin/jellyfin-plugin-template#licensing)).
