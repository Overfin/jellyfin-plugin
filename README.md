# Overfin's Jellyfin plugin

The companion plugin of [Overfin](https://github.com/Overfin/server): a few
routes under `/Overfin/` on Overfin's own Jellyfin, where Jellyfin's API falls
short. Overfin installs it itself, with the Jellyfin it's built for; nobody
installs it by hand, and it answers no one but Overfin.

Today it only says its version and Jellyfin's (`GET /Overfin/Info`). Planned,
one at a time: text subtitles by stretches of time, without reading the whole
file; one frame of a video; a numbered feed of the library's changes.

## Building

It's built against Jellyfin's own packages, at the version of the Jellyfin it
goes with (12.2.0 today), on .NET 10:

```sh
dotnet test tests/Overfin.Jellyfin.Tests
dotnet build -c Release src/Overfin.Jellyfin
```

## How it's made

Overfin's Jellyfin plugin is designed by Overfin's maintainer, who makes the
architecture, security and product decisions, and written by Claude
(Anthropic's AI); Claude's commits carry a `Co-Authored-By: Claude` trailer.
It's written from Jellyfin's plugin interfaces only, with no code copied from
Jellyfin or other plugins, and it hasn't had an independent security review
yet.

## License

GPL-3.0 (it's built against Jellyfin's GPL assemblies). Overfin itself is MIT:
it talks to the plugin over HTTP only.
