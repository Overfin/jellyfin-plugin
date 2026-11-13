using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Overfin.Jellyfin;

/// <summary>The plugin's version and Jellyfin's, for Overfin to check at start.</summary>
public record InfoAnswer(string Plugin, string Jellyfin);

/// <summary>
/// The library's changes since a number: Reset when the reader must read
/// everything again first (from Generation and Last).
/// </summary>
public record ChangesAnswer(string Generation, long Last, bool Reset, List<Change> Changes);

/// <summary>The plugin's routes, for Overfin alone.</summary>
[ApiController]
[Route("Overfin")]
[Authorize(Policy = "RequiresElevation")]
[OverfinOnly]
public class OverfinController : ControllerBase
{
    /// <summary>The most a stretch of text may weigh, as WebVTT.</summary>
    private const int MaxText = 5 << 20;

    /// <summary>The most a stretch of pictures (PGS) may weigh.</summary>
    private const int MaxPictures = 64 << 20;

    private readonly IServerApplicationHost _host;
    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _sources;
    private readonly IMediaEncoder _encoder;
    private readonly ChangeFeed _feed;

    public OverfinController(IServerApplicationHost host, ILibraryManager library, IMediaSourceManager sources, IMediaEncoder encoder, ChangeFeed feed)
    {
        _host = host;
        _library = library;
        _sources = sources;
        _encoder = encoder;
        _feed = feed;
    }

    /// <summary>The most items one page of the index may hold.</summary>
    private const int MaxPage = 2000;

    /// <summary>
    /// GET /Overfin/Items?after=&amp;limit= : the films, series, seasons and
    /// episodes of every library, as Overfin's index keeps them, by ID, after
    /// `after`; or, with ids (at most 200, comma-separated), those of them
    /// still in a library. Never a path (IndexItem).
    /// </summary>
    [HttpGet("Items")]
    public ActionResult<IndexPage> Items([FromQuery] string? after, [FromQuery] int limit = 500, [FromQuery] string? ids = null)
    {
        if (ids is not null)
        {
            var list = new List<Guid>();
            foreach (var s in ids.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (list.Count == 200 || !Guid.TryParseExact(s, "N", out var id))
                {
                    return NotFound();
                }

                if (id != Guid.Empty)
                {
                    list.Add(id);
                }
            }

            return new IndexPage(IndexItems.ByIds(_library, list), null);
        }

        if (limit < 1 || limit > MaxPage || (after is not null && !Guid.TryParseExact(after, "N", out _)))
        {
            return NotFound();
        }

        return IndexItems.Page(_library, after?.ToLowerInvariant(), limit);
    }

    /// <summary>
    /// GET /Overfin/Changes?generation=&amp;since=&amp;limit= : the items changed
    /// after `since` in this generation of the feed (Jellyfin's start), at
    /// most `limit` (5000); Reset, with the current generation and number,
    /// when the reader must read the library again first.
    /// </summary>
    [HttpGet("Changes")]
    public ActionResult<ChangesAnswer> Changes([FromQuery] string? generation, [FromQuery] long since = 0, [FromQuery] int limit = 1000)
    {
        if (limit < 1 || limit > 5000)
        {
            return NotFound();
        }

        // The number first: changes noted while the reader reads everything
        // again come after it.
        var last = _feed.Last;
        if (generation is null || _feed.Since(generation, since, limit) is not { } got)
        {
            return new ChangesAnswer(_feed.Generation, last, true, []);
        }

        return new ChangesAnswer(_feed.Generation, got.Last, false, got.Changes);
    }

    /// <summary>GET /Overfin/Info: which plugin and which Jellyfin answer.</summary>
    [HttpGet("Info")]
    public ActionResult<InfoAnswer> Info() =>
        new InfoAnswer(typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "", _host.ApplicationVersionString);

    /// <summary>
    /// GET /Overfin/Subtitles/{item}/{source}/{index}?from=&amp;to=: the
    /// subtitles of one track between two times (seconds, the file's own),
    /// read from there rather than from the start of the file. Text comes as
    /// WebVTT, pictures (PGS) as a .sup stream, copied. Only tracks in files
    /// on disk: a stream from the network is a 404, never fetched.
    /// </summary>
    [HttpGet("Subtitles/{item:guid}/{source}/{index:int}")]
    public async Task<IActionResult> Subtitles(Guid item, string source, int index, [FromQuery] double from, [FromQuery] double to, CancellationToken cancel)
    {
        if (!Stretch.Valid(from, to) || index < 0)
        {
            return NotFound();
        }

        var video = _library.GetItemById(item);
        var media = video is null ? null : _sources.GetStaticMediaSources(video, false).FirstOrDefault(s => s.Id == source);
        var track = media?.MediaStreams.FirstOrDefault(s => s.Index == index && s.Type == MediaStreamType.Subtitle);
        if (media is null || track is null || media.Protocol != MediaProtocol.File)
        {
            return NotFound();
        }

        var format = Stretch.FormatOf(track.Codec);
        var path = track.IsExternal ? track.Path : media.Path;
        if (format is null || string.IsNullOrEmpty(path))
        {
            return NotFound();
        }

        var fileIndex = track.IsExternal ? 0 : Stretch.FileIndex(media.MediaStreams.Select(s => (s.Index, s.IsExternal)), index);
        if (fileIndex < 0)
        {
            return NotFound();
        }

        var max = format == StretchFormat.Sup ? MaxPictures : MaxText;
        var contentType = format == StretchFormat.Sup ? "application/octet-stream" : "text/vtt; charset=utf-8";

        // A track inside a Matroska file: read from the file's own index, which
        // stops at the stretch's end whatever the track; ffmpeg otherwise.
        if (!track.IsExternal)
        {
            var (fromMkv, mkvFailure) = await Ffmpeg.Within(token => FromMkv(path, fileIndex, from, to, format.Value, max, token), cancel).ConfigureAwait(false);
            if (mkvFailure == RunFailure.TooSlow)
            {
                return StatusCode(StatusCodes.Status504GatewayTimeout);
            }

            if (fromMkv is not null)
            {
                return File(fromMkv, contentType);
            }
        }

        var args = Stretch.Arguments(path, track.IsExternal, fileIndex, from, to, format.Value);
        var (output, failure) = await Ffmpeg.Run(_encoder.EncoderPath, args, max, cancel).ConfigureAwait(false);
        if (failure != RunFailure.None)
        {
            return StatusCode(failure == RunFailure.TooSlow ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway);
        }

        return File(output, contentType);
    }

    /// <summary>The most a frame may weigh, as a JPEG.</summary>
    private const int MaxFrame = 10 << 20;

    /// <summary>
    /// GET /Overfin/Frame/{item}/{source}?at=: one frame of that version at
    /// that moment (seconds), as a JPEG at most 1920 pixels wide, HDR mapped
    /// to standard range. Only files on disk: a stream from the network is a
    /// 404, never fetched.
    /// </summary>
    [HttpGet("Frame/{item:guid}/{source}")]
    public async Task<IActionResult> Frame(Guid item, string source, [FromQuery] double at, CancellationToken cancel)
    {
        if (!Jellyfin.Frame.Valid(at))
        {
            return NotFound();
        }

        var video = _library.GetItemById(item);
        var media = video is null ? null : _sources.GetStaticMediaSources(video, false).FirstOrDefault(s => s.Id == source);
        var stream = media?.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video && !s.IsExternal);
        if (media is null || stream is null || media.Protocol != MediaProtocol.File || string.IsNullOrEmpty(media.Path))
        {
            return NotFound();
        }

        var hdr = stream.VideoRange == VideoRange.HDR;
        var (output, failure) = await Ffmpeg.Run(_encoder.EncoderPath, Jellyfin.Frame.Arguments(media.Path, at, hdr), MaxFrame, cancel).ConfigureAwait(false);
        if (failure != RunFailure.None)
        {
            return StatusCode(failure == RunFailure.TooSlow ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway);
        }

        // A moment past the end gives no frame.
        return output.Length == 0 ? NotFound() : File(output, "image/jpeg");
    }

    // FromMkv reads a stretch with the Matroska reader; null when the file or
    // the track isn't one it reads, or the result isn't what was asked.
    private static byte[]? FromMkv(string path, int fileIndex, double from, double to, StretchFormat format, int max, CancellationToken cancel)
    {
        try
        {
            if (Mkv.Read(path, fileIndex, from, to, cancel) is not { } read || StretchOutput.FormatOf(read.Codec) != format)
            {
                return null;
            }

            var bytes = format == StretchFormat.Sup ? StretchOutput.Sup(read.Blocks) : StretchOutput.WebVtt(read.Codec, read.Blocks);
            return bytes.Length <= max ? bytes : null;
        }
        catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
