using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    public OverfinController(IServerApplicationHost host, ILibraryManager library, IMediaSourceManager sources, IMediaEncoder encoder)
    {
        _host = host;
        _library = library;
        _sources = sources;
        _encoder = encoder;
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
