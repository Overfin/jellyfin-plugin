using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Overfin.Jellyfin;

/// <summary>
/// An item as Overfin's index keeps it, with Jellyfin's own field names:
/// what the index needs and nothing else. Never a path: a film's file name
/// and the name of the folder it's in, a series' folder name.
/// </summary>
public sealed record IndexItem(
    string Id,
    string Type,
    string LibraryId,
    string Name,
    string SortName,
    int? ProductionYear,
    string? OfficialRating,
    Dictionary<string, string> ProviderIds,
    string? SeriesId,
    string? SeasonId,
    int? IndexNumber,
    int? ParentIndexNumber,
    string DateCreated,
    string? PremiereDate,
    float? CommunityRating,
    long? RunTimeTicks,
    string? FolderName,
    string? FileName,
    List<IndexStream>? MediaStreams);

/// <summary>An audio or subtitle track, as the index reads its languages.</summary>
public sealed record IndexStream(
    int Index,
    string Type,
    string? Codec,
    string? Language,
    string? Title,
    int? Channels,
    bool IsDefault,
    bool IsForced,
    bool IsHearingImpaired,
    bool IsTextSubtitleStream);

/// <summary>A page of the index's items, and where the next starts.</summary>
public sealed record IndexPage(List<IndexItem> Items, string? Next);

/// <summary>Reads the library's items for Overfin's index.</summary>
internal static class IndexItems
{
    private static readonly BaseItemKind[] Kinds = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode];

    /// <summary>
    /// Every film, series, season and episode of every library, by ID, after
    /// `after` (an ID, or none for the start), at most `limit`. Ordered by
    /// ID, so items added or removed meanwhile never shift the pages.
    /// </summary>
    public static IndexPage Page(ILibraryManager library, string? after, int limit)
    {
        var all = new SortedDictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var folder in Libraries(library))
        {
            var ids = library.GetItemIds(new InternalItemsQuery { IncludeItemTypes = Kinds, Recursive = true, AncestorIds = [folder] });
            foreach (var id in ids)
            {
                all.TryAdd(id.ToString("N"), folder);
            }
        }

        var items = new List<IndexItem>();
        string? last = null;
        foreach (var (id, folder) in all)
        {
            if (after is not null && string.CompareOrdinal(id, after) <= 0)
            {
                continue;
            }

            if (items.Count == limit)
            {
                return new IndexPage(items, last);
            }

            last = id;
            if (library.GetItemById(Guid.ParseExact(id, "N")) is { } item && Of(item, folder) is { } it)
            {
                items.Add(it);
            }
        }

        return new IndexPage(items, null);
    }

    /// <summary>The items among ids that are still in a library.</summary>
    public static List<IndexItem> ByIds(ILibraryManager library, IEnumerable<Guid> ids)
    {
        var libraries = Libraries(library).ToHashSet();
        var items = new List<IndexItem>();
        foreach (var id in ids)
        {
            if (library.GetItemById(id) is not { } item)
            {
                continue;
            }

            var folder = library.GetCollectionFolders(item).Select(f => f.Id).FirstOrDefault(libraries.Contains);
            if (folder != Guid.Empty && Of(item, folder) is { } it)
            {
                items.Add(it);
            }
        }

        return items;
    }

    // Libraries are the server's collection folders.
    private static IEnumerable<Guid> Libraries(ILibraryManager library) =>
        library.GetVirtualFolders()
            .Select(f => Guid.TryParse(f.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty);

    /// <summary>An item as the index keeps it; null for another kind.</summary>
    internal static IndexItem? Of(BaseItem item, Guid libraryId)
    {
        var type = item switch
        {
            Movie => "Movie",
            Series => "Series",
            Season => "Season",
            Episode => "Episode",
            _ => null,
        };
        if (type is null)
        {
            return null;
        }

        string? folderName = null, fileName = null;
        if (item is Movie)
        {
            (folderName, fileName) = (NameOf(DirectoryOf(item.Path)), NameOf(item.Path));
        }
        else if (item is Series)
        {
            folderName = NameOf(item.Path);
        }

        List<IndexStream>? streams = null;
        if (item is Movie or Episode)
        {
            streams = item.GetMediaStreams()
                .Where(s => s.Type is MediaStreamType.Audio or MediaStreamType.Subtitle)
                .Select(s => new IndexStream(s.Index, s.Type.ToString(), s.Codec, s.Language, s.Title, s.Channels,
                    s.IsDefault, s.IsForced, s.IsHearingImpaired, s.IsTextSubtitleStream))
                .ToList();
        }

        Guid? seriesId = item switch { Episode e => e.SeriesId, Season s => s.SeriesId, _ => null };
        Guid? seasonId = item is Episode ep ? ep.SeasonId : null;
        return new IndexItem(
            item.Id.ToString("N"),
            type,
            libraryId.ToString("N"),
            item.Name ?? string.Empty,
            item.SortName ?? string.Empty,
            item.ProductionYear,
            item.OfficialRating,
            new Dictionary<string, string>(item.ProviderIds ?? new Dictionary<string, string>()),
            seriesId is { } sid && sid != Guid.Empty ? sid.ToString("N") : null,
            seasonId is { } eid && eid != Guid.Empty ? eid.ToString("N") : null,
            item.IndexNumber,
            item.ParentIndexNumber,
            item.DateCreated.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            item.PremiereDate?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            item.CommunityRating,
            item.RunTimeTicks,
            folderName,
            fileName,
            streams);
    }

    // The last name of a path, slashes or backslashes; null when there's none.
    internal static string? NameOf(string? path)
    {
        var p = path?.TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(p))
        {
            return null;
        }

        var i = p.LastIndexOfAny(['/', '\\']);
        return p[(i + 1)..];
    }

    internal static string? DirectoryOf(string? path)
    {
        var p = path?.TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(p))
        {
            return null;
        }

        var i = p.LastIndexOfAny(['/', '\\']);
        return i > 0 ? p[..i] : null;
    }
}
