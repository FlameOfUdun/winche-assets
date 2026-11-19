# Winche.Assets

Content apps read that changes on its own schedule, not the app's: translations, HTML pages, images, videos. It's
published from a folder as numbered **releases**, apps follow a **channel** (`production`, `beta`, …), and per-path
**rules** say who may read what. A typo fix is a new release, not a new app build; rolling back is moving the channel to
an earlier release.

- **Releases:** immutable and numbered, with a note and who published it. Each file's content is stored once, by its
  SHA-256, whatever the releases that use it, so publishing again only sends what changed.
- **Channels:** named pointers to a release. Apps read one; old app builds that can't read newer content can stay on
  another. Every move is kept (who, when, from which release).
- **Rules:** in the Winche rules language, as for documents and files. Default deny.
- **Where the bytes live:** the app's storage location by default (a folder on disk, or S3 and compatible buckets
  through Winche.Storage), or a location of its own the console can move it to.

It's a product of [Winche Server](https://github.com/FlameOfUdun/winche-server): start it from its console page.
These packages also add it to any ASP.NET Core app.

| Package | |
| --- | --- |
| `Winche.Assets` | Releases, channels, the content store, rules, settings, the content sweep, and change events (`released`, `channelMoved`) for webhooks and functions |
| `Winche.Assets.AspNetCore` | The apps' API, `/assets/v1` |
| `Winche.Functions.Assets` (in [winche-functions](https://github.com/FlameOfUdun/winche-functions)) | `ctx.assets` and the `assets.onReleased` / `onChannelMoved` triggers |

## Install

```sh
dotnet add package Winche.Assets
dotnet add package Winche.Assets.AspNetCore
```

## Setup

```csharp
builder.Services.AddWincheConfig(o => { o.ConnectionString = conn; o.MasterKey = config["Winche:MasterKey"]; });
builder.Services.AddWincheStorage(o => { /* … */ });   // where contents go, unless UseArchive says otherwise
builder.Services.AddWincheAssets(o =>
{
    o.ConnectionString = conn;
    o.UseRulesText("""
        match translations/{rest=**} { allow read; }                          // before sign-in too
        match {path=**} { allow read: if request.auth != null; }
        """);
    o.UseSettings();    // the console may change the limits and move contents to another location
});

var app = builder.Build();
app.MapWincheAssets();  // /assets/v1
```

- `UseArchive(sp => …)` keeps contents somewhere other than the app's storage location; `StreamFiles()` sends files
  through the server instead of a short-lived signed link to the location.
- `UseRulesFile("assets.rules")` reads the rules from a file; without any rules, nothing can be read.
- `Limits` (the starting values when `UseSettings()` lets the console change them): files per release (10,000), the
  largest file (512 MB), releases kept (50; older ones no channel points at are swept, with contents nothing uses).
- Without `AddWincheConfig`, settings and a location of its own can't be saved; everything else works.

## Apps' API (`/assets/v1`)

| | |
| --- | --- |
| `GET /channels/{channel}` | The channel's release: `{ channel, release, publishedAt, files: [{ path, hash, size, mimeType }] }`, only the files the caller may list. `ETag` and `If-None-Match` → `304`. |
| `GET /channels/{channel}/files/{**path}` | A file of the channel's release, rules checked. |
| `GET /releases/{n}/files/{**path}` | A file of a given release: cached for good (`immutable`), so an app syncing a release can't mix in a newer one. |

`HEAD` works on each. `?link=true` answers with a short-lived link to the file instead of its bytes, for clients that
download without their credentials. Rules see `request.auth` (uid and token claims), the path's captures,
`resource` (`size`, `mimeType`, `hash`) and `channel`; operations are `get` (a file) and `list` (it shows in the listing).

The Dart SDK, [`winche_assets`](https://pub.dev/packages/winche_assets), syncs a channel into an offline cache:
prefetched paths are downloaded with each release before it's used, everything else on first use.

## Publishing

From the [CLI](https://github.com/FlameOfUdun/winche-cli), as you or as a service account with the `assets` scope:

```sh
winche assets init                         # an assets/ folder, assets.rules and its tests, added to winche.json
winche assets deploy -m "German pages"     # sends only contents the server doesn't have, publishes, moves the channel
winche assets diff                         # how the folder differs from the channel's release
winche assets releases | release <n> | channels
winche assets channels:set production 41   # move a channel; an older release is a rollback
```

Admins can also make a release in the console (Assets → Files → New release): it's marked `source: "console"`, and
`winche assets deploy` asks before publishing a folder that would take files out of such a release.

The console's Assets page shows the channels (with each one's history in the panel on the right), the releases and
their files, what changed between two releases, the rules editor, and settings: limits and where contents are kept.
Server code publishes with `WincheAssets` (`MissingAsync`, `PutContentAsync`, `PublishAsync`, `MoveChannelAsync`) and
reads with `ReleaseAsync` / `OpenAsync`.

## Files on a CDN

A release can name files kept on a CDN instead of storing them (large videos, say). Apps never see the CDN's address:
the listing has only path, hash and size, each read is checked by the rules, and the answer is a link the CDN accepts
for a few minutes, so the address alone gets nothing. Apps download, check and keep them like any other file.

- **The CDN** signs links: CloudFront, with signed URLs required on the distribution and a key group it trusts. Set it
  in the console (Assets → Settings → CDNs: its base address, the public key's id, the private key as a value or a
  reference to AWS, how long links work), or in code with `UseCdn(new AssetCdn { Id, BaseUrl, KeyPairId }, privateKeyPem)`.
- **Publishing** names them by address in `assets.cdn.json` beside `winche.json`; `winche assets deploy` publishes them
  with the folder, uploading nothing for them:

  ```json
  { "files": { "videos/adult/bvm.mp4": "https://cdn.example.com/app/adult/bvm.mp4" } }
  ```

  The server reads each new or changed one once through a signed link (by its ETag) and keeps its hash; a release
  takes only hashes it read. Server code does the same with `InspectCdnFilesAsync` and `AssetFile.Url`.
- **Keep CDN files unchanged:** a release names a file's content by hash, so replacing a file at the same address breaks
  that release's downloads (apps refuse what doesn't match). Give a new cut a new address, then publish.

## License

Elastic License 2.0: see [LICENSE](LICENSE).
