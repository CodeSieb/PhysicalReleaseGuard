# Hidden Tag Plugin for Jellyfin

A Jellyfin plugin that automatically manages a `Hidden` tag for movies based on [TMDb](https://www.themoviedb.org/) physical release data.

## How It Works

For every **movie** in your Jellyfin library:

| Condition | Result |
|---|---|
| TMDb physical release exists | No `Hidden` tag |
| TMDb movie data exists, but no physical release | Add `Hidden` tag |
| No TMDb data for the movie | No change |
| TV series / non-movie content | Ignored |

The plugin uses TMDb release type **5** (Physical) to determine whether a physical release exists. Digital and streaming releases are ignored.

## Installation

### Prerequisites

- Jellyfin server **10.11.x**
- A free [TMDb API key](https://www.themoviedb.org/settings/api)

### Build from source

```bash
git clone https://github.com/YOUR_USERNAME/HiddenTagPlugin.git
cd HiddenTagPlugin
dotnet restore
dotnet build -c Release
```

> ⚠️ You'll need the [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) installed to build.

The compiled `HiddenTagPlugin.dll` will be in `HiddenTagPlugin/bin/Release/net8.0/`.

### Install in Jellyfin

1. Copy the built `HiddenTagPlugin.dll` to your Jellyfin `plugins/HiddenTagPlugin/` directory
2. Restart Jellyfin
3. Go to **Dashboard → Plugins → Hidden Tag Manager** and enter your TMDb API key
4. Optionally set the `TMDbApiKey` environment variable instead

## Usage

### Manual scan

Go to **Dashboard → Scheduled Tasks → Run Hidden Tag Scan** and click the play button.

### Scheduled scan

By default, the plugin runs a daily scan at 3:00 AM. You can adjust this in **Dashboard → Scheduled Tasks → Run Hidden Tag Scan → Triggers**.

### Reading the logs

The plugin logs every decision:

- `Physical release found for 'MovieName' ... Removed 'Hidden' tag.`
- `No physical release for 'MovieName' ... Added 'Hidden' tag.`
- `No TMDb data found for movie: MovieName. No changes made.`
- `Could not retrieve release data from TMDb ... No changes made.`

## License

MIT
