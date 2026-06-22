# Physical Release Guard for Jellyfin

A Jellyfin plugin that automatically manages a `Hidden` tag for movies and series based on [TMDb](https://www.themoviedb.org/) physical release data.

## Why Would I Want This?

If you add movies/series to Jellyfin as soon as they're announced (e.g. via Sonarr/Radarr), they show up in your library immediately even though they aren't physically available to buy or watch yet. This plugin automatically hides those titles by tagging them with a 'Hidden' tag, keeping your library clean. When the physical release drops, the 'Hidden' tag is removed automatically, no manual cleanup needed.

## How It Works

For every **movie** and **series** in your Jellyfin library:

| Condition | Result |
|---|---|
| Movie has a TMDb physical release | No `Hidden` tag |
| Movie has TMDb data, but no physical release | Add `Hidden` tag |
| Series has TMDb DVD/physical episode-group evidence | No `Hidden` tag |
| Series has TMDb data, but no DVD/physical episode-group evidence | Add `Hidden` tag |
| Item is in an excluded library | No change |
| Item is explicitly excluded | No change |
| Item has an admin-pinned TMDb ID (manual link) | Lookup uses that ID directly |
| No TMDb data for the item | No change |
| Non-movie / non-series content | Ignored |

For movies, the plugin uses TMDb release type **5** (Physical) to determine whether a physical release exists. Digital and streaming releases are ignored.

TMDb does not expose an equivalent physical-release endpoint for TV series. For series, the plugin checks TMDb TV episode groups and treats DVD/physical-style groups as evidence of a physical release.

## Performance & Reliability (v1.11.0.0+)

The plugin is friendlier to TMDb and easier to operate at scale:

- **Polite TMDb client** — shared HttpClient with a token-bucket rate limit, configurable retries with exponential backoff and jitter. The whole plugin honors the same per-second budget, even if a scheduled scan, a single-library scan, and an auto-add event run at the same time.
- **Bounded-parallel scans** — multiple items are processed concurrently (configurable cap). The same cap also throttles the auto-scan-on-add path so a flood of new items doesn't trip TMDb.
- **Per-scan circuit breaker** — when enabled, a single scan aborts after a configurable number of consecutive TMDb failures. The next scan starts with a clean slate.
- **Unmatched TMDb Items panel** — items the matcher could not resolve (no search hit, no release data, persistent TMDb errors) are recorded and surfaced on the config page. From there you can Retry lookup, or pin a TMDb ID manually to bypass the search step on subsequent scans.
- **Manual TMDb links** — pin a specific TMDb ID for any item. The link is validated against TMDb on save (probes both `/movie/{id}` and `/tv/{id}`) and replaces the auto-search.

Defaults are conservative so existing installs don't suddenly hammer TMDb. Most users won't need to touch them.

## Installation

### Prerequisites

- Jellyfin server **10.11.x**
- A free [TMDb API key](https://www.themoviedb.org/settings/api)

### Via Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Click the **+** button and add this repository URL:
   ```
   https://raw.githubusercontent.com/CodeSieb/PhysicalReleaseGuard/main/manifest.json
   ```
3. Go to **Catalog**, find **Physical Release Guard**, and click **Install**
4. Restart Jellyfin
5. Go to **Dashboard → Plugins → Physical Release Guard** and enter your TMDb API key

### Manual Install

#### Build from source

```bash
git clone https://github.com/CodeSieb/PhysicalReleaseGuard.git
cd PhysicalReleaseGuard
dotnet restore
dotnet build -c Release
```

> ⚠️ You'll need the [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) installed to build.

The compiled `PhysicalReleaseGuard.dll` will be in `PhysicalReleaseGuard/bin/Release/net9.0/`.

#### Install in Jellyfin

1. Copy the built `PhysicalReleaseGuard.dll` to your Jellyfin `plugins/PhysicalReleaseGuard/` directory
2. Restart Jellyfin
3. Go to **Dashboard → Plugins → Physical Release Guard** and enter your TMDb API key
4. Optionally set the `TMDbApiKey` environment variable instead

## Usage

### Manual scan

Go to **Dashboard → Scheduled Tasks → Run Physical Release Guard Scan** and click the play button. You can also trigger a scan for a single library from the config page.

### Excluded libraries

Go to **Dashboard → Plugins → Physical Release Guard** and select any libraries you want to exclude. Excluded libraries are skipped entirely, so the plugin will not add or remove the `Hidden` tag for items in those libraries.

### Excluded movies and series

Go to **Dashboard → Plugins → Physical Release Guard** and select individual movies or series you want to exclude. Excluded items are skipped entirely, so the plugin will not add or remove the `Hidden` tag for those titles.

### Manual TMDb links

For items the auto-search fails to resolve, open the **Unmatched TMDb Items** panel on the config page. Either click **Retry Lookup** to re-run the auto-search, or paste a TMDb ID and click **Save Link** to pin it. Validated links bypass the search step on subsequent scans.

> [!NOTE]
> The `Hidden` tag only hides items from a user's library view if it is blocked in that user's **Parental Control** settings. Go to **Dashboard → Users → [user] → Parental Control** and add `Hidden` to the blocked tags list. Otherwise the tag is metadata-only and will not affect visibility.

### Scheduled scan

By default, the plugin runs a daily scan at 3:00 AM. You can adjust this in **Dashboard → Scheduled Tasks → Run Physical Release Guard Scan → Triggers**.

### Reading the logs

The plugin logs every decision:

- `Physical release found for movie 'MovieName' ... Removed 'Hidden' tag.`
- `No physical release for movie 'MovieName' ... Added 'Hidden' tag.`
- `Physical release found for series 'SeriesName' ... Removed 'Hidden' tag.`
- `No physical release for series 'SeriesName' ... Added 'Hidden' tag.`
- `No TMDb data found for movie/series ... No changes made.`
- `Could not retrieve release data from TMDb ... No changes made.`
- `Circuit breaker tripped after N consecutive failures. Aborting scan.`

## Troubleshooting

### Nothing happened after installing

Trigger a manual scan at **Dashboard → Scheduled Tasks → Run Physical Release Guard Scan** and click the play button. The scheduled scan runs daily at 3:00 AM by default.

### Items are still visible when they should be hidden

First, check that the item is not in an **excluded library** or on the **excluded items** list. If it isn't excluded, verify that TMDb has physical release data for that title — the plugin only acts on items where TMDb data is found. Also check the **Parental Control** note above.

### Hidden tag is set but items are still showing

The `Hidden` tag must be blocked in each user's **Parental Control** settings to actually hide items. See the note under [Excluded movies and series](#excluded-movies-and-series).

### Items appear in the Unmatched TMDb Items panel

Click **Retry Lookup** to re-run the auto-search. If it keeps failing, find the matching TMDb ID at [themoviedb.org](https://www.themoviedb.org), paste it into the row, and click **Save Link** to pin it. The next scan will use the pinned ID directly.

### Scan is slow

On large libraries the scan can take a while. It runs in the background, so you can continue using Jellyfin normally while it processes. The Performance & Reliability section lets you raise the max parallelism if your TMDb key allows.

### API key is not working

Verify your TMDb API key is valid at [themoviedb.org/settings/api](https://www.themoviedb.org/settings/api) and that it is entered correctly in **Dashboard → Plugins → Physical Release Guard**. You can also set the `TMDbApiKey` environment variable as an alternative.

## Contributing

Contributions are welcome! Feel free to open an issue or submit a pull request on [GitHub](https://github.com/CodeSieb/PhysicalReleaseGuard).

## License

MIT
