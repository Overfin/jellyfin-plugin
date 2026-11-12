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
4. Its answers carry only what each route says: no paths, no file names, no
   user data.
5. Its releases are built by this repository's CI from a tag, with actions and
   dependencies pinned, and Overfin pins the version it ships.

## Reporting a vulnerability

Please report it privately, through this repository's **Security** tab
(*Report a vulnerability*), not in a public issue.
