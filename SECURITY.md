# Security

The plugin runs inside Jellyfin, with all of Jellyfin's rights, so it keeps to
these rules. A change that breaks one is a bug. Overfin's own rules are in its
[SECURITY.md](https://github.com/Overfin/server/blob/main/SECURITY.md).

1. Every `/Overfin/` route is for Overfin alone. Jellyfin's administrator
   policy refuses everyone who isn't an administrator (401, 403); then only an
   API key whose token's SHA-256 is the one Overfin set in the plugin's
   configuration gets in, compared in constant time; any other administrator
   gets a 404. Until Overfin sets it, no one gets in.
2. The plugin only reads: it never changes an item, a user, a setting or a
   file, and never writes into media folders.
3. It calls nothing on the network.
4. Its answers carry only what each route says: no paths and no user data;
   file and folder names only from the index's route (a film's file and
   folder, a series' folder).
5. Its releases are built by this repository's CI from a tag, with actions and
   dependencies pinned, and Overfin pins the version it ships.
6. It runs only Jellyfin's own ffmpeg, without a shell, with fixed
   arguments: the item, version and track by Jellyfin's IDs, times as numbers
   it checked, the path Jellyfin keeps for the file, always read as a file.
   Never a URL, and nothing else from a request. Only files on disk: a track
   Jellyfin marks as from the network is refused. Each run has a 30-second
   time limit and an output size limit (5 MB of text, 64 MB of pictures), and
   at most four run at once.
7. It reads Matroska and WebM files itself only to find a stretch of
   subtitles, read only. Every size is checked against the element that holds
   it and against fixed limits: 16 MB for the file's headers, 64 MB for its
   index, 4 MB for a subtitle block (16 MB once uncompressed), 4 GB walked for
   one stretch. It runs within the same time limit and four-at-once as ffmpeg
   (rule 6). Anything it doesn't expect, it leaves to ffmpeg.

## Reporting a vulnerability

Please report it privately, through this repository's **Security** tab
(*Report a vulnerability*), not in a public issue.
