# Jellytriggers

A Jellyfin server plugin that surfaces content-warning info from
[doesthedogdie.com](https://www.doesthedogdie.com/) on the movie detail page,
filtered to the categories each viewer cares about.

This file is the canonical project brief for future sessions. If something
contradicts the code, the code wins — and please update this file.

---

## Current status (as of 2026-05-10)

**v0.1.0.11 is deployed and working.** The pane renders on movie detail pages,
DTDD matching works, and per-user API keys are stored and used correctly.

### What works end-to-end

- File Transformation integration: startup disk-patch + rescue middleware
- DTDD resolution: TMDB ID → IMDb ID → title+year fallback
- Per-user API key storage (entered directly in the pane)
- Pane renders favorite triggers, split into Yes-verdict (shown by default) and
  a "Show X more" toggle for No/paywalled items
- Refresh button bypasses both the resolution cache and pane cache
- Resolution cache and pane cache both on disk with configurable TTLs

### Known quirks

- **Cold-start "Error processing request"**: On the very first page load after
  a Jellyfin restart, FT occasionally wins the race against our rescue
  middleware and the page errors. A second load always works. Not worth fixing.
- **Resolution cache persists "not found" across key changes**: If a bad key
  caused a cached miss, use the ↻ refresh button (bypasses cache) or call
  `POST /Plugins/Jellytriggers/resolution-cache/clear` to nuke all entries.

### Pending ideas

- **Style matching**: Make the pane visually match Jellyfin's "Not available on
  any streaming services" card. CSS-only change — inspect Jellyfin's card
  classes and mirror them in `.jt-pane`.

---

## Hard-won fixes — do not revert

### FT integration (FileTransformationRegistration.cs + IndexHtmlInjector.cs)

FT v2.5.9.0 has a bug: its internal IServiceProvider is disposed after Jellyfin
startup. On a cold disk cache, every GET /web/ crashes with
`ObjectDisposedException`. FT also persists registration data to disk across
restarts, so stale registrations replay even if we stop calling Register.

Our approach (do not change without re-reading this):

1. **Do NOT register with FT at startup.** We used to, but the callback gets
   invoked by a disposed IServiceProvider. Registration is skipped entirely.

2. **Direct disk patch at startup.** `FileTransformationRegistration` reads
   `IApplicationPaths.WebPath` (not `IWebHostEnvironment.WebRootPath` — that
   is empty in Docker) and calls `IndexHtmlInjector.Transform` directly on
   the file at startup. This ensures our script tag is always in the HTML
   on disk regardless of FT's state.

3. **Rescue middleware** (`IndexHtmlRescueFilter` / `IndexHtmlRescueMiddleware`).
   Registered as an `IStartupFilter` so it's first in the pipeline. Catches
   `ObjectDisposedException` from FT for `/web/` requests and serves our
   disk-patched `index.html` directly as a fallback.

#### FT callback contract (kept for reference, even though we don't register)

FT invokes the callback as:
```csharp
(string)method.Invoke(null, new object[] { paramObj })
```
- Method must return `string` (void → null → NullReferenceException in FT).
- Parameter must be `JObject` (custom DTO fails across AssemblyLoadContexts).
- `fileNamePattern` must be `"index.html"` (literal match, not regex).

`Api/Models/FtPayload.cs` is dead code from an experiment. Harmless, can be deleted.

### DTDD type filter (TriggerLookupService.cs)

DTDD's `/dddsearch` API returns `type=null` for virtually all entries —
including movies. The original `IsMovie()` helper required `type == "Movie"`,
which meant every TMDB and IMDb ID match was silently rejected.

Fix: ID-based matches (TMDB, IMDb) no longer check type at all — a matching ID
is authoritative. The title+year fallback uses `IsMovieOrUnknown()`, which
accepts `null` or `"Movie"` and rejects explicit non-movie types (Series, etc.).

### Non-ASCII characters in jellytriggers.js

The JS file must contain **only ASCII characters**. The browser can mis-parse
UTF-8 multi-byte sequences in string literals (especially under `'use strict'`),
causing a `SyntaxError` that silently kills the entire script before any code
runs. Replace any special character with its escape sequence or ASCII equivalent:
- em dash `—` → `-`
- en dash `–` → `-` or `–`
- ellipsis `…` → `...` or `…`
- arrows, emoji, etc. → escape sequences or remove

---

## What we're building

When a Jellyfin user opens a movie, a small pane appears on the detail page
showing only the DTDD trigger questions that user has marked as favorites on
doesthedogdie.com. Anything they haven't favorited stays hidden.

The pane shows triggers in two groups:
- **Yes-verdict triggers** (community voted Yes) — shown by default
- **Remaining favorites** (No verdict or paywalled) — hidden behind "Show X more"

The pane has a small refresh control for forcing a fresh DTDD pull. There's also
a bulk "Refresh my triggers everywhere" action that invalidates the user's entire
pane cache.

Out of scope for v1: TV shows, mobile/native Jellyfin clients (web client only),
multi-language category labels, episode-level granularity, anything other than
DTDD as a data source.

## Why this design

- **C# / .NET 9 plugin, packaged as a DLL.** Standard Jellyfin plugin pattern.
  Distributed via a `manifest.json` repo URL added in the Jellyfin dashboard.
  (NB: the template README still says .NET 8, but Jellyfin 10.11.8 NuGet
  packages target .NET 9; we follow the packages.)
- **Per-user DTDD API keys.** DTDD personalizes responses per key — using a
  shared key would lose per-user filtering entirely.
- **Pane delivered via injected JS, loaded by File Transformation.** The plugin
  ships a JS+CSS bundle and patches `index.html` on disk at startup. FT is
  still installed and handles serving, but we no longer register a callback
  with it (see FT integration notes above). A manual script tag also works for
  installs without FT, but it's not the supported path.
- **No in-Jellyfin category picker.** Users curate their trigger list on
  doesthedogdie.com; we honor it via the API.

## How DTDD personalization works (load-bearing detail)

When `/media/{id}` is called with `X-API-KEY: <user key>`, every topic in the
response carries an `isFavorite` boolean for that user. So the pane is built
from a single API call:

```
GET https://www.doesthedogdie.com/media/{dtddMediaId}
Headers: Accept: application/json, X-API-KEY: <user's key>
```

Filter `topicItemStats[]` to entries where `isFavorite === true`. Fields we use:

- `doesName`         — question label, e.g. "Does the dog die"
- `yesSum`, `noSum`  — community verdict (Yes if yesSum > noSum)
- `numComments`      — for "14 comments" link
- `slug`             — for deep-linking back to DTDD
- `paywalled`        — render a locked badge instead of the verdict if true
- `topic.notName` / `topic.survivesName` — wording for "no" answers

`/categories` (with key) returns all 200+ topics with the same `isFavorite`
flag. Useful for an "All triggers" admin view, but not needed for the pane.

## Project layout

```
Jellytriggers/
├── Jellyfin.Plugin.Jellytriggers/
│   ├── Plugin.cs                         # entry point, GUID, Name
│   ├── Configuration/
│   │   ├── PluginConfiguration.cs        # cache TTLs, defaults, rate limits
│   │   └── configPage.html               # admin settings page (cache, info)
│   ├── Api/
│   │   ├── DoesTheDogDieClient.cs        # typed HTTP client (X-API-KEY auth)
│   │   └── Models/                       # DTOs for /dddsearch & /media
│   │       └── FtPayload.cs              # dead code — do not use as FT callback
│   │                                     # parameter (see FT learnings above)
│   ├── Services/
│   │   ├── TriggerLookupService.cs       # Jellyfin item -> DTDD media id
│   │   ├── TriggerCache.cs               # disk cache w/ TTL
│   │   ├── UserKeyStore.cs               # per-user DTDD key storage
│   │   ├── FileTransformationRegistration.cs  # IHostedService: disk-patches
│   │   │                                      # index.html at startup
│   │   ├── IndexHtmlRescueFilter.cs      # IStartupFilter: adds rescue middleware
│   │   └── IndexHtmlRescueMiddleware.cs  # catches FT ObjectDisposedException
│   │                                     # on /web/ and serves disk index.html
│   ├── Controllers/
│   │   └── JellytriggersController.cs    # /Plugins/Jellytriggers/...
│   └── Web/
│       ├── IndexHtmlInjector.cs          # strips old injection, re-injects
│       ├── jellytriggers.js              # injected web client script (ASCII only)
│       └── jellytriggers.css
├── Jellyfin.Plugin.Jellytriggers.sln
├── manifest.json                         # for the Jellyfin repository
├── build.yaml
├── README.md
└── CLAUDE.md                             # this file
```

## Server endpoints (plugin REST API)

All under `/Plugins/Jellytriggers/`:

- `GET  /triggers/{itemId}` — Returns the user's favorite triggers for the
  given Jellyfin item. Honors the pane cache.
- `POST /triggers/{itemId}/refresh` — Same response, bypasses and overwrites
  the pane cache. Called by the pane's ↻ refresh button.
- `POST /refresh` — Invalidates all of the calling user's pane-cache entries.
- `POST /resolution-cache/clear` — Wipes all resolution cache entries (all
  users). Use after a bad key caused stale "not found" entries.
- `GET  /key`               — Returns `{ hasKey: true/false }`.
- `POST /key`               — Sets/updates the calling user's DTDD key.
  Invalidates that user's pane cache.
- `DELETE /key`             — Clears the key. Also drops the user's pane cache.
- `GET  /script.js`         — Serves the bundled JS. Anonymous endpoint.
- `GET  /style.css`         — Serves the bundled CSS. Anonymous endpoint.

## Matching Jellyfin items to DTDD media

The matcher tries in order:
1. TMDB ID (most reliable). No type filter — DTDD returns `type=null` for most
   entries; a matching ID is treated as authoritative.
2. IMDb ID. Same — no type filter.
3. Title + year search fallback (if `AllowTitleSearchFallback` is enabled).
   Accepts `type=null` or `type=Movie`; rejects explicit non-movie types.

## Caching

Two caches, both on disk under the plugin's data directory:

- **Resolution cache** — `jellyfinItemId -> dtddMediaId | NOT_FOUND`. TTL: long
  (30 days for hits, 7 days for misses). Cheap, almost never invalidated.
  Force-bypassed by the ↻ refresh button. Fully cleared by
  `POST /resolution-cache/clear`.
- **Pane cache** — `(dtddMediaId, jellyfinUserId) -> filtered topic list`.
  TTL: short (default 24h, configurable). Invalidated by refresh button (single
  item) or `POST /refresh` (all items for a user). Also wiped on key change.

## Configuration surface

- **Admin (server-wide):** cache TTLs, optional rate limit, title-search
  fallback toggle.
- **Per-user:** DTDD API key (entered in the pane itself), plus a "Refresh my
  triggers everywhere" button.

## Build / dev workflow

**Important:** The workspace mount path cannot service NuGet restore or `git`
temp-file operations. Always mirror the source to the sandbox's local
filesystem before building:

```bash
# Mirror source to sandbox (adjust paths for current session)
rsync -a /sessions/<session>/mnt/Jellytriggers/ /tmp/jt-build/

# Install .NET 9 SDK if needed
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- \
  --version 9.0.100 --install-dir /tmp/dotnet

# Build and publish
cd /tmp/jt-build
/tmp/dotnet/dotnet publish -c Release Jellyfin.Plugin.Jellytriggers/

# Copy DLL to release directory
mkdir -p /sessions/<session>/mnt/Jellytriggers/release/jellytriggers-<version>/
cp .../publish/Jellyfin.Plugin.Jellytriggers.dll \
   /sessions/<session>/mnt/Jellytriggers/release/jellytriggers-<version>/
```

After a successful publish, copy the DLL to the release directory, create/update
`meta.json` for the new version, and deploy to Jellyfin.

Deploy on the Synology: copy the DLL into the Docker volume plugin folder,
name the folder `Jellytriggers_<version>`, delete any old version folder,
then restart Jellyfin. One plugin folder = one version, always.

## Things to NOT do

- **Never commit a DTDD API key.**
- **Don't put non-ASCII characters in `jellytriggers.js`.** They cause a silent
  SyntaxError at parse time under strict mode. Use escape sequences instead.
- **Don't add the `IsMovie()` type filter back to ID-based matches.** DTDD
  returns `type=null` for most items; the filter was blocking every match.
- **Don't modify `jellyfin-web` files on the user's filesystem.**
- **Don't store DTDD keys in logs or GET responses.**
- **Don't use a custom DTO as the FT callback parameter type** — use `JObject`.
- **Don't return void from the FT callback** — FT reads the return value as string.

## Reference

- DTDD API page: https://www.doesthedogdie.com/api
- Confirmed working endpoints:
  - `GET /dddsearch?q=<title>` — search
  - `GET /media/{id}` — full media data with personalized `isFavorite` flags
  - `GET /categories` — all topics with personalized `isFavorite` flags
- Jellyfin plugin template: https://github.com/jellyfin/jellyfin-plugin-template
- Shape reference: https://github.com/ryandash/jellyfin-plugin-myanimelist
- Plex equivalent: https://github.com/valknight/DoesTheDogWatchPlex
- File Transformation: https://github.com/IAmParadox27/jellyfin-plugin-file-transformation
- MediaBar plugin (FT callback reference): https://github.com/IAmParadox27/jellyfin-plugin-media-bar
