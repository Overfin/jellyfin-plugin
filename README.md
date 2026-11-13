# Overfin's Jellyfin plugin

The companion plugin of [Overfin](https://github.com/Overfin/server): a few
routes under `/Overfin/` on Overfin's own Jellyfin, where Jellyfin's API falls
short. Overfin installs it itself, with the Jellyfin it's built for; nobody
installs it by hand, and it answers no one but Overfin.

What it does:

- `GET /Overfin/Info`: its version and Jellyfin's.
- `GET /Overfin/Subtitles/{item}/{source}/{index}?from=&to=`: one subtitle
  track between two times (seconds, at most ten minutes), read from that
  point of the file instead of the whole file: text as WebVTT, pictures
  (PGS) copied as they are. Matroska and WebM files are read from their own
  index by the plugin, which stops at the stretch's end whatever the track
  (ffmpeg can't, on a track with few lines); other files go through
  Jellyfin's ffmpeg.

- `GET /Overfin/Frame/{item}/{source}?at=`: one frame of a video at a
  moment, as a JPEG at most 1920 pixels wide, HDR mapped to standard range
  with Jellyfin's own tone mapping: for episodes TMDB has no picture for.

- `GET /Overfin/Items?after=&limit=` (or `?ids=`): the library's films,
  series, seasons and episodes as Overfin's index keeps them, by ID, so
  pages never shift. Never a path: a film's file and folder names, a
  series' folder name.
- `GET /Overfin/Changes?generation=&since=`: Jellyfin's own library events
  (added, changed, removed), numbered, kept in memory: a new generation at
  each start, when Overfin reads the library again.

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
