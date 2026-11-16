# Winche Assets: plan

## What it's for

Apps carry content that changes on its own schedule, not the code's: translations, HTML pages (help, demonstrations),
images, demonstration videos. Today it's bundled in the app, so a typo fix needs an app release. Winche Assets
publishes it from the server instead:

- **Releases:** a folder published as an immutable, numbered release, with a note and who published it.
- **Channels:** named pointers to a release (`production`, `beta`, `v2`, …). An app reads one channel. Rolling back is
  moving the channel to an earlier release. Old app builds that can't read newer content stay on another channel.
- **Rules:** per path, in the Winche.Rules language storage uses (e.g. translations readable before sign-in, videos
  for signed-in users, a folder by claim).
- **Location:** where the bytes live is configurable like Storage's (S3 or a folder on disk), with the same moves
  between locations.
- **Apps:** whole files, downloaded once and kept offline. Some paths ahead of use, the rest on first use. No
  streaming.
- **Functions:** `ctx.assets.channel("production")` reads the same content with the server's rights. (The function's
  own files are `ctx.resources`, versioned with the deployment: a different thing.)

## Model

| Thing | Where | What |
| --- | --- | --- |
| Content | the location, key `assets/{sha256}` | each file's bytes, stored once whatever the releases that use it |
| Release | table `winche_asset_releases` | `number`, `note`, `by`, `at`, its files: path → `{ hash, size, mimeType }` |
| Channel | table `winche_asset_channels` | `name` → release number, `updatedAt`, `by`, history of moves |
| Rules | the console's rules store (subsystem `assets`) | versioned like the other products' rules (console editor, `winche assets rules deploy`) |
| Location | settings area `assets.location` | as Storage's location settings |

- Paths are relative, `/`-separated, no `.`/`..`, case-sensitive; up to 10,000 files and 512 MB per file per
  release (settings).
- A release is never changed. Contents no release uses any more (releases deleted by a retention setting, e.g. keep the
  last 50 unless a channel points at them) are removed by a sweep.
- Types come from the extension (as the migration does), overridable per file in the release manifest.

## API (`/assets/v1`, per project)

| | |
| --- | --- |
| `GET /channels/{channel}` | The channel's release: `{ release, at, files: [{ path, hash, size, mimeType }] }`, only the paths the caller may read. `ETag` = release number; `If-None-Match` → 304. |
| `GET /channels/{channel}/files/{**path}` | The file (rules checked): its bytes with `ETag` = hash and long caching (`immutable`: the URL with `?v={hash}` never changes), or a 302 to a short-lived signed link when the location can make one (S3). |
| `GET /releases/{n}/files/{**path}` | A file of a given release (the SDK pins the release it's syncing, so a channel move mid-sync can't mix releases). |
| `HEAD` of both | For the SDK's checks. |

Rules see `request.auth` (uid, token claims), the `path` captures, `resource` (`size`, `mimeType`, `hash`) and
`channel`. Operations: `get` (a file), `list` (it shows in the channel's listing). Default deny.

Publishing goes through the console API (as the signed-in admin, or a CI token with the `assets` scope), like
functions deploys:

- `POST /api/assets/contents/missing { hashes }` → which contents the location doesn't have;
- `PUT /api/assets/contents/{hash}` (raw, up to the file limit) → stored after checking its hash;
- `POST /api/assets/releases { files, note, channel? }` → the new release (and moves the channel, if given);
- `POST /api/assets/channels/{name} { release }` (move / roll back), `DELETE` (remove a channel);
- `GET /api/assets/releases`, `/releases/{n}`, `/channels`.

## Packages

- `Winche.Assets`: model, store (Postgres), publishing, channel moves, the content sweep, rules guard, hooks
  (`assets` change source: `released`, `channelMoved`, for webhooks and functions triggers).
- `Winche.Assets.AspNetCore`: the `/assets/v1` endpoints.
- Bytes through Winche.Storage's location providers (S3, FileSystem): Assets has a location of its own (its settings
  area, its moves), so a project can keep assets in a different bucket from user files.
- `Winche.Functions.Assets`: `ctx.assets` for functions (`channel(name)`, `release(n)`, `text`, `json`, `bytes` up
  to 32 MB, `downloadUrl`, `list`), and triggers `assets.onReleased` / `assets.onChannelMoved`.

## CLI

- `winche assets deploy <folder> [--channel production] -m "note"`: hashes the folder, uploads only new contents,
  publishes the release (and moves the channel).
- `winche assets releases`, `winche assets release <n>` (files, sizes), `winche assets channels`.
- `winche assets channel <name> <release>` (move / roll back), `winche assets channel:remove <name>`.
- `winche assets rules validate | test | deploy | pull | diff` as for the other rules.
- `winche.json`: `"assets": { "folder": "assets", "channel": "production" }`, and `"assets": "assets.rules"` under `"rules"` (the rules commands handle it like the others).
- Emulator: serves the folder as the release of every channel, saving a file publishes again; rules apply.

## Console

An **Assets** product page (start / set up like the others):

- **Channels:** each with its release, when it moved and by whom; move / roll back (two-step sign-in where
  settings need it).
- **Releases:** list (number, note, by, when, files, total size, channels on it); a release's files with sizes,
  types and hashes; what changed against another release (added, changed, removed).
- **Rules:** the shared rules editor and versions.
- **Settings:** location (as Storage), file and release limits, retention.
- Empty states for no releases / no channels.

## Dart SDK (`winche_assets`)

Same shape as the other Dart SDKs (bound to `winche_core`'s app and session, rebuilding on user switches):

```dart
final assets = WincheAssets.instance;
assets.settings = WincheAssetsSettings(
  channel: 'production',
  prefetch: ['translations/**', 'pages/**'],   // downloaded with each new release, before it's used
  // everything else downloads on first use
);

final de = await assets.json('translations/de.json');        // from the cache when there; offline works
final html = await assets.text('pages/help/cpr.html');
final video = await assets.file('videos/intro.mp4');          // a local File, downloaded whole on first use
assets.releases.listen((r) => …);                             // a new release is ready (its prefetch done)
await assets.sync();                                          // check now (also on start and resume)
```

- **Sync:** reads the channel's listing (304 when unchanged), then downloads the prefetch paths' new or changed files
  for that release (pinned) before switching to it, so the app never sees half a release. Unchanged files (same
  hash) aren't downloaded again; files no longer in the release are removed when nothing needs them.
- **Cache:** content-addressed on disk (`{hash}`), a small index of releases; survives restarts; a byte budget
  evicts on-demand files least recently used (never prefetch ones).
- **Offline:** everything already downloaded reads offline, from the last synced release.
- **Signed-out access:** per the rules, like the other SDKs (`allowSignedOut`).
- **Errors:** `WincheAssetsException` with the shared codes (`not-found`, `permission-denied`, `unavailable`).
- Tests against the emulator and a fake server.

## Steps

1. ✅ **Server package** (`Winche.Assets`, `.AspNetCore`): store, publishing, channels, reads with rules, location
   through Winche.Storage's providers (move = copy then switch), sweep, hooks; tests (Postgres, in-memory archives).
2. ✅ **Winche Server:** the product (start, settings areas, rules repository, console API, CI scope), its tests.
3. ✅ **CLI:** deploy, releases, channels, rules; the emulator serving a folder; tests.
4. ✅ **Console:** the Assets page (channels, releases and their differences, rules, settings).
5. **Dart SDK** (`winche_assets`): sync, cache, reads, streams; tests; README.
6. **Functions:** `Winche.Functions.Assets` (`ctx.assets`, triggers); tests.
7. **A first app:** its translations and media moved to an assets folder, the app on `winche_assets`.

Each step is committed locally and released only when asked.

## Open questions

- Signed links for files on a disk location (signed `/assets/v1` URLs with an expiry, like Storage's), or always
  served by the server.
- Whether a channel can point at "latest" (moves with every deploy) or is always moved explicitly.
- Per-locale fallbacks (e.g. `de-AT` → `de`) in the SDK, or left to the app.
