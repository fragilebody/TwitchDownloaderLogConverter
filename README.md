# TwitchDownloader Log Converter

A cross-platform desktop app (Windows, macOS, Linux) that turns chat logs from [logs.ivr.fi](https://logs.ivr.fi/) into a chat JSON for [TwitchDownloader](https://github.com/lay295/TwitchDownloader). Use it to render chat for VODs whose chat replay is missing, broken or deleted.

The output is laid out like a normal TwitchDownloader chat download, so it works with the chat render, chat update and HTML/TXT conversion in TwitchDownloaderCLI and TwitchDownloaderWPF.

## Download

Get the latest build from [Releases](../../releases):

| Platform | File |
|---|---|
| Windows | `TwitchDownloaderLogConverter-win-x64.zip` (or `win-arm64`) |
| macOS (Apple Silicon) | `TwitchDownloaderLogConverter-osx-arm64.zip` |
| macOS (Intel) | `TwitchDownloaderLogConverter-osx-x64.zip` |
| Linux | `TwitchDownloaderLogConverter-linux-x64.tar.gz` (or `linux-arm64`) |

The builds are self-contained, so you don't need to install .NET.

**macOS:** the app isn't notarized. If macOS says it is damaged or can't be opened, run:
```bash
xattr -cr TwitchDownloaderLogConverter.app
```

## Usage

Choose a source:
- **From VOD**: paste a VOD URL or ID. The channel, start time, length, title and chapters come from Twitch, and that exact time range is fetched from logs.ivr.fi.
- **Channel + time range**: for VODs that no longer exist. Enter the channel, the stream start (UTC) and the length.
- **TwitchTracker**: pick a segment (for example one game) of a past stream from its [TwitchTracker](https://twitchtracker.com/) page. See below.
- **Local log file**: a file saved from logs.ivr.fi (`?json=1` or `?ndjson=1`, optionally gzipped). Add a VOD ID to get its metadata, or set the start and length by hand.

Options:
- Chat delay offset, to line chat up with the video.
- Emote overrides: your own images for removed or changed emotes (see below).
- Twitch emotes found by name in old logs (pre-2019 imports) that have no emote data.
- Commenter profile info (avatar, bio), like a normal download.
- Embedded emotes, badges and cheermotes (Twitch, BTTV, FFZ, 7TV).
- Gzip compression.

## Streams from TwitchTracker

TwitchTracker stream pages (like `twitchtracker.com/{channel_name}/streams/{id}`) list when each category started. The app splits a stream at those points so you can grab chat for one segment, even when the VOD is gone. TwitchTracker doesn't allow scraping, so the app never downloads from it. Instead, a bookmarklet copies the timeline from the page you have open:

1. On the **TwitchTracker** tab, click **Copy bookmarklet**. Add a bookmark in your browser and paste the copied text as its URL.
2. Open the stream's page on twitchtracker.com and click the bookmark. It copies the stream data.
3. Back in the app, click **Paste stream data** and select one or more segments. Start and length are filled in, and you can still edit them.

TwitchTracker records category changes every 10 minutes, so a segment can start up to 10 minutes later than the real switch. You can adjust the start and length manually to compensate for this.

## Emote overrides

Old logs can mention emotes that Twitch has removed or replaced. For example, the original PogChamp (removed in 2021) now shows as today's PogChamp, and removed sub emotes or old BTTV emotes show as plain text. To control how they look, put your own images in a folder and choose it under **Emote overrides**:

```
my-emotes/
  PogChamp.png     ← used wherever "PogChamp" appears in chat
  gachiGASM.gif    ← animated GIFs work too
  forsenE.webp
```

- The file name (without extension) is the emote name, case-sensitive. Supported formats: PNG, GIF, WebP and JPEG.
- Overrides replace Twitch, BTTV, FFZ and 7TV emotes with the same name, and are embedded in the output JSON.
- Use square images at least 28 px tall (56 or 112 px look sharpest). They're scaled to Twitch emote size.
- When rendering, keep at least one of BTTV, FFZ or 7TV enabled in TwitchDownloader. Overrides are loaded as third-party emotes, so they're skipped if all three are turned off.

## What gets converted

The output matches what Twitch VOD chat contains:
- Chat messages keep their emotes, badges, name color and bits.
- Sub, resub, gift sub and watch-streak notices are formatted the way VOD chat shows them, so TwitchDownloader renders them as highlighted sub messages.
- Timeouts, bans, raids and other IRC events are dropped, because VOD chat doesn't include them.
- Messages from users who opted out of logs.ivr.fi can't be recovered.

## Building

```bash
git clone --recursive https://github.com/fragilebody/TwitchDownloaderLogConverter.git
cd TwitchDownloaderLogConverter
dotnet run --project src/TwitchDownloaderLogConverter
```

Requires the .NET 10 SDK. TwitchDownloaderCore is included as a git submodule in `external/`, from [fragilebody/TwitchDownloader](https://github.com/fragilebody/TwitchDownloader). That fork of [lay295/TwitchDownloader](https://github.com/lay295/TwitchDownloader) carries fixes this project depends on.

Run the tests with:
```bash
dotnet test --project tests/TwitchDownloaderLogConverter.Tests
```

Releases are built by GitHub Actions when a `v*` tag is pushed.

## Credits

- [TwitchDownloader](https://github.com/lay295/TwitchDownloader) by lay295 and contributors (MIT), for the chat JSON format, the Twitch API calls and image embedding.
- [logs.ivr.fi](https://logs.ivr.fi/), for the chat logs.

This project isn't affiliated with Twitch, TwitchDownloader or logs.ivr.fi.
