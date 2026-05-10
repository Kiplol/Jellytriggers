# Jellytriggers

A Jellyfin server plugin that surfaces content-warning info from
[doesthedogdie.com](https://www.doesthedogdie.com/) on the movie detail page,
filtered to the categories each viewer cares about.

This file is the canonical project brief for future sessions. If something
contradicts the code, the code wins — and please update this file.

---

## Current status (as of 2026-05-10)

The plugin compiles and was deployed once. The core API (triggers, key
management) works. The File Transformation integration has been fully debugged
and the code is now correct, but **the last-built DLL in the release directory
predates the final fixes** — the next task is to do a clean build and deploy
it.

### What is fixed and ready to build

- **`Api/Models/DtddItem.cs`** — Two JSON property names differing only in
  case (`tmdbId` / `tmdbid`) caused `InvalidOperationException` under
  `PropertyNameCaseInsensitive = true`. Collapsed to a single `TmdbId` with
  `[JsonPropertyName("tmdbId")]`. The API now responds correctly.

- **`Web/IndexHtmlInjector.cs`** — The callback was `void` and mutated the
  payload object. File Transformation calls our method via reflection as
  `(string)method.Invoke(null, new object[] { paramObj })` — mutation is
  ignored, and a void method returns `null`. FT then crashed on the null
  result. Fixed: method now returns `string` (the modified HTML) and wraps
  everything in a try/catch that returns the original HTML on any error.

- **`Web/jellytriggers.js`** — Two bugs:
  1. Stale slot reference: the `slot` variable captured before the async
     fetch could be detached from the DOM by Jellyfin's SPA by the time the
     fetch resolves. Fixed by checking `slot.isConnected` on resolution and
     falling back to `ensureSlot()`.
  2. Null payload crash in `renderBody`: the final `else` branch called
     `payload.Items` without checking `payload` first. Fixed with an explicit
     `else if (payload)` guard.

### What is NOT yet done

- **Build and deploy.** The release directory has the old broken DLL.
  Do a clean build and copy the new DLL to the release directory, then deploy
  to the Jellyfin server. See "Build / dev workflow" below.

- **Verify FT injection end-to-end.** After deploying, open a movie, open
  DevTools → Network, and confirm `script.js` appears. If it does, the pane
  should render. If not, check the Jellyfin log for
  `"registered index.html script injection"` — if that line is missing, File
  Transformation wasn't detected.

---

## File Transformation integration — hard-won learnings

This section records everything figured out about the FT callback contract.
Do not change any of it without re-reading all of this.

### How FT invokes our callback

FT discovers our method by reflection and calls it as:

```csharp
(string)method.Invoke(null, new object[] { paramObj })
```

where `paramObj` is the result of `JObject.ToObject(paramType)` applied to the
JSON `{"contents": "<full index.html>"}`.

Two rules flow from this:

1. **The method must return `string`** (the possibly-modified HTML). Returning
   void gives FT a null, which it tries to use and crashes on. The crash is
   caught by FT's middleware, the page loads but the injection never happens.

2. **The parameter type must be `JObject`** (from Newtonsoft.Json.Linq).
   Using a custom DTO (e.g. `FtPayload`) causes Newtonsoft in FT's
   AssemblyLoadContext to fail constructing the type because it belongs to a
   different load context. This exception is NOT caught by FT's middleware —
   it propagates to Jellyfin's request pipeline and causes **"Error processing
   request."** for every page load, breaking Jellyfin entirely.
   `JObject` is safe because Jellytriggers uses `ExcludeAssets=runtime` for
   Newtonsoft, so both plugins share the same host assembly instance.

The correct signature is therefore:

```csharp
public static string Transform(JObject payload)
```

`Api/Models/FtPayload.cs` exists in the project as dead code from an
experiment. It is not used and is harmless as-is, but can be deleted.

### File name pattern

The `fileNamePattern` field in the registration payload must be `"index.html"`
— a plain string match, not a regex. Using a regex like `"index\\.html$"` does
not match because FT does literal/string matching for non-`.*` patterns.

### Registration payload shape

```csharp
var payload = new JObject
{
    ["id"] = TransformationId.ToString("D"),   // stable Guid, do not change
    ["fileNamePattern"] = "index.html",
    ["callbackAssembly"] = typeof(Web.IndexHtmlInjector).Assembly.FullName!,
    ["callbackClass"] = typeof(Web.IndexHtmlInjector).FullName,
    ["callbackMethod"] = nameof(Web.IndexHtmlInjector.Transform),
};
register.Invoke(null, new object?[] { payload });
```

### What to check in logs after deploy

```
Jellytriggers: registered index.html script injection with File Transformation.
```

If you see the "File Transformation plugin not detected" message instead,
the FT plugin isn't installed or hasn't loaded yet. Install it from the
Jellyfin plugin catalogue and restart.

---

## What we're building

When a Jellyfin user opens a movie, a small pane appears on the detail page
showing only the DTDD trigger questions that user has marked as favorites on
doesthedogdie.com (e.g. "Does the dog die — Yes (202 vs 11)"). Anything they
haven't favorited stays hidden. The pane shows the community verdict, comment
count, and a link out to DTDD.

The pane has a small refresh control. If the user has just updated their
favorite categories on doesthedogdie.com, they can click it to force a fresh
pull instead of waiting for the cache to expire. There's also a bulk
"Refresh my triggers everywhere" action in the user-settings page that
invalidates the user's entire pane cache.

Out of scope for v1: TV shows, mobile/native Jellyfin clients (web client only),
multi-language category labels, episode-level granularity, anything other than
DTDD as a data source.

## Why this design

A short list of decisions and the reasoning, so we don't relitigate them.

- **C# / .NET 9 plugin, packaged as a DLL.** This is how Jellyfin plugins are
  built; the official template and most existing plugins (incl. the MAL plugin
  we used as a shape reference) follow this pattern. Distributed via a
  `manifest.json` repo URL the user adds in the Jellyfin dashboard.
  (NB: the template README still says .NET 8, but the current Jellyfin
  10.11.8 NuGet packages target .NET 9; we follow the packages.)
- **Per-user DTDD API keys.** Each Jellyfin user enters their own DTDD key.
  This is the right model because DTDD personalizes its responses per key
  (see "How DTDD personalization works" below) — using a single shared key
  would lose the per-user filtering entirely.
- **Pane delivered via injected JS, loaded by File Transformation.** The
  plugin ships a JS+CSS bundle and, on startup, registers an `index.html`
  transformation with the
  [File Transformation plugin](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
  (via reflection — FT can't be linked directly). With File Transformation
  installed, the pane loads with zero admin setup. We deliberately don't
  auto-patch jellyfin-web's files on disk — invasive, breaks across Jellyfin
  updates, and File Transformation already does the right thing without that
  cost. A manual `<script defer src="/Plugins/Jellytriggers/script.js"></script>`
  added to the web client also works for installs that don't run File
  Transformation, but it's not the supported path and isn't documented in
  the admin UI.
- **No in-Jellyfin category picker.** Users curate their trigger list on
  doesthedogdie.com; we honor it via the API. Less UI to build, and the source
  of truth lives where it's expected to live.

## How DTDD personalization works (load-bearing detail)

This took some probing to confirm and is the linchpin of the design:

When `/categories` or `/media/{id}` are called with `X-API-KEY: <user key>`,
**every topic in the response carries an `isFavorite` boolean** that reflects
that user's saved triggers on DTDD. So the pane is built from a single API
call:

```
GET https://www.doesthedogdie.com/media/{dtddMediaId}
Headers: Accept: application/json, X-API-KEY: <user's key>
```

Then filter `topicItemStats[]` to entries where `isFavorite === true`. Each
filtered entry has the fields we render:

- `doesName`         — question label, e.g. "Does the dog die"
- `yesSum`, `noSum`  — community verdict ("Yes" if yesSum > noSum)
- `numComments`      — for "14 comments" link
- `slug`             — for deep-linking back to DTDD
- `paywalled`        — render a "locked" badge instead of the verdict if true
- `topic.notName` / `topic.survivesName` — wording for "no" answers

There is also a `personalizedSafety` / `personalizedTimeline` field at the top
level that may contain a server-precomputed personalized view, but we observed
it as `null` on at least one movie. Treat as opportunistic: use if present,
otherwise filter `topicItemStats` by `isFavorite`.

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
│   │   └── FileTransformationRegistration.cs  # IHostedService that registers
│   │                                           # the index.html callback with FT
│   ├── Controllers/
│   │   └── JellytriggersController.cs    # /Plugins/Jellytriggers/...
│   └── Web/
│       ├── IndexHtmlInjector.cs          # FT callback — return string, JObject param
│       ├── jellytriggers.js              # injected web client script
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
  given Jellyfin item. Slim payload, ready to render. Auth: standard Jellyfin
  user auth; the controller looks up the calling user's stored DTDD key.
  Honors the pane cache.
- `POST /triggers/{itemId}/refresh` — Same response shape as the GET, but
  bypasses and overwrites the pane cache for `(dtddMediaId, jellyfinUserId)`.
  This is what the small refresh control in the pane calls. Synchronous — we
  return the freshly-fetched, freshly-filtered pane.
- `POST /refresh` — Invalidates **all** of the calling user's pane-cache
  entries (per-user bulk reset). Does not pre-warm; the next visit to each
  movie re-fetches on demand. This backs the "Refresh my triggers everywhere"
  button in user settings.
- `GET  /key`               — Returns whether the calling user has a DTDD key
  configured (never returns the key itself).
- `POST /key`               — Sets/updates the calling user's DTDD key.
  Side-effect: invalidates that user's pane cache, since a key change implies
  a different identity / different favorites.
- `DELETE /key`             — Clears it. Also drops the user's pane cache.
- `GET  /script.js`         — Serves the bundled JS (anonymous endpoint —
  browsers can't auth before fetching a script tag). File Transformation
  injects `<script defer src="/Plugins/Jellytriggers/script.js"></script>`
  into `index.html` automatically.
- `GET  /style.css`         — Serves the bundled CSS. Also anonymous.

## Matching Jellyfin items to DTDD media

Jellyfin stores TMDB and IMDb IDs on movie items via its built-in providers.
DTDD media records include `tmdbId` and `imdbId`. The matcher tries:

1. TMDB ID (most reliable, present on most items via TMDB provider).
2. IMDb ID.
3. `/dddsearch?q=<title>` plus year disambiguation, as a last resort.

Once resolved, store the DTDD media ID against the Jellyfin item (e.g., as a
provider ID, or in the plugin's own cache keyed by Jellyfin item ID). The
resolution rarely changes; cache it indefinitely.

## Caching

Two caches, both on disk under the plugin's data directory:

- **Resolution cache** — `jellyfinItemId -> dtddMediaId | NOT_FOUND`. TTL: long
  (e.g. 30 days for hits, 7 days for misses). Cheap, almost never invalidated.
- **Pane cache** — `(dtddMediaId, jellyfinUserId) -> filtered topic list`.
  TTL: short (default 24h, configurable). Each user's favorites can change on
  DTDD, so we don't want to over-cache. Invalidated explicitly by
  `POST /triggers/{itemId}/refresh` (single key) and `POST /refresh` (all keys
  for that user). Also wiped when the user changes or clears their DTDD key.

Optional scheduled task: "Pre-fetch triggers for all movies" — walks the movie
library, resolves DTDD IDs, fills the resolution cache. The pane cache stays
on-demand because it's per-user.

## Configuration surface

- **Admin (server-wide):** cache TTLs, optional rate limit, optional toggle to
  skip TV shows from any future scope expansion.
- **Per-user:** the user's DTDD API key, plus a "Refresh my triggers
  everywhere" button that calls `POST /refresh`. No category picker —
  favorites come from DTDD.

The admin config page calls out two things:

1. That File Transformation is required and links to its repository. We display
   detected/not-detected status pulled from `FileTransformationRegistration`'s
   last result so the admin can see at a glance whether the pane will load.
2. A note that themed installs (Catppuccin etc.) can restyle the pane via the
   server's Branding → Custom CSS field — every pane element ships with a
   stable `jt-*` class.

## Build / dev workflow

**Important:** The workspace mount path cannot service NuGet restore or `git`
temp-file operations. Always mirror the source to the sandbox's local
filesystem before building:

```bash
# Mirror source to sandbox (adjust paths for current session)
rsync -a /sessions/<session>/mnt/Jellytriggers/ /sessions/<session>/jt-build/

# Build from the local copy
cd /sessions/<session>/jt-build
/sessions/<session>/dotnet/dotnet build Jellyfin.Plugin.Jellytriggers.sln

# Publish (produces the DLL we ship)
/sessions/<session>/dotnet/dotnet publish -c Release Jellyfin.Plugin.Jellytriggers/
```

The .NET 9 SDK may need to be installed fresh each session to
`/sessions/<session>/dotnet/`. Use the official install script.

After a successful publish, copy the DLL and any other artifacts to the
release directory in the workspace and update `manifest.json`.

```bash
# Deploy to Jellyfin (macOS path)
cp Jellyfin.Plugin.Jellytriggers.dll \
  ~/Library/Application\ Support/jellyfin/plugins/Jellytriggers_<version>/
# then restart Jellyfin
```

The MAL plugin's `.vscode/launch.json` is a good reference if we want to
attach a debugger to a local Jellyfin checkout.

## Distribution

`manifest.json` hosted at the GitHub raw URL. Users add it once via
`Settings -> Plugins -> Repositories -> Add`, then install Jellytriggers from
the catalogue. New releases are uploaded as zipped DLLs and the manifest is
updated.

## Things to NOT do

- **Never commit a DTDD API key.** Keys are per-user and live in plugin user
  config, not in the repo, env files, or fixtures.
- **Don't modify `jellyfin-web` files on the user's filesystem.** The pane is
  delivered via an opt-in script tag, not by mutating the web client.
- **Don't store DTDD keys in plain text in the plugin's XML config without
  some form of obfuscation** (Jellyfin user-level config is generally not
  encrypted, but at minimum the value should not appear in logs or in any
  GET response). Treat them like the secrets they are.
- **Don't use a custom DTO as the FT callback parameter type.** See the "File
  Transformation integration" section. It will break Jellyfin page serving.
- **Don't return void from the FT callback.** FT reads the return value as
  a string. Void → null → NullReferenceException in FT → no injection.

## Things to verify against a real environment

Some assumptions are based on probing the public DTDD API and reading docs;
they should be confirmed once we have a running Jellyfin + plugin:

- That `topic.notName` and `topic.survivesName` are populated consistently
  enough to use for "No" wording. (Our Old Yeller probe showed both populated.)
- Jellyfin's exact mechanism for storing per-user plugin config. The plugin
  template's `BasePluginConfiguration` is server-wide; per-user data may need
  a custom store or a small EFCore use of Jellyfin's user data tables.
- DTDD rate limits (not documented publicly) — start conservative, log 429s.
- That File Transformation's reflection contract still matches when the
  plugin updates (we discover it by `AssemblyLoadContext` and invoke
  `Jellyfin.Plugin.FileTransformation.PluginInterface.RegisterTransformation`).
  If FT renames or re-shapes that surface, our `FileTransformationRegistration`
  hosted service logs and degrades gracefully, but the pane stops loading
  until we adapt.

## Reference

- DTDD API page (JS-rendered): https://www.doesthedogdie.com/api
- Working DTDD endpoints we've confirmed:
  - `GET /dddsearch?q=<title>` — search
  - `GET /media/{id}` — full media data with personalized `isFavorite` flags
  - `GET /categories` — all topics, with personalized `isFavorite` flags
- Jellyfin plugin template: https://github.com/jellyfin/jellyfin-plugin-template
- Shape reference (metadata-provider plugin in C#):
  https://github.com/ryandash/jellyfin-plugin-myanimelist
- Plex equivalent for prior art:
  https://github.com/valknight/DoesTheDogWatchPlex
- File Transformation (the install path):
  https://github.com/IAmParadox27/jellyfin-plugin-file-transformation
- MediaBar plugin (FT callback reference implementation):
  https://github.com/IAmParadox27/jellyfin-plugin-media-bar
