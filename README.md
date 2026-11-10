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
- **Local log file**: a file saved from logs.ivr.fi (`?json=1` or `?ndjson=1`, optionally gzipped). Add a VOD ID to get its metadata, or set the start and length by hand.

Options:
- Chat delay offset, to line chat up with the video.
- Commenter profile info (avatar, bio), like a normal download.
- Embedded emotes, badges and cheermotes (Twitch, BTTV, FFZ, 7TV).
- Gzip compression.

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

Requires the .NET 10 SDK. [TwitchDownloaderCore](https://github.com/lay295/TwitchDownloader) is included as a git submodule in `external/`.

Run the tests with:
```bash
dotnet test --project tests/TwitchDownloaderLogConverter.Tests
```

Releases are built by GitHub Actions when a `v*` tag is pushed.

## Credits

- [TwitchDownloader](https://github.com/lay295/TwitchDownloader) by lay295 and contributors (MIT), for the chat JSON format, the Twitch API calls and image embedding.
- [logs.ivr.fi](https://logs.ivr.fi/), for the chat logs.

This project isn't affiliated with Twitch, TwitchDownloader or logs.ivr.fi.
