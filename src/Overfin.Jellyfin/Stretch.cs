using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Overfin.Jellyfin;

/// <summary>What a stretch of subtitles comes out as.</summary>
public enum StretchFormat
{
    /// <summary>Text, converted to WebVTT.</summary>
    WebVtt,

    /// <summary>Pictures (PGS), copied as they are, in a .sup stream.</summary>
    Sup,
}

/// <summary>
/// A stretch of one subtitle track: the arguments for Jellyfin's ffmpeg,
/// from values the route checked. Nothing in them comes from a request but
/// numbers; the path is the one Jellyfin keeps for the item or the track.
/// </summary>
internal static class Stretch
{
    /// <summary>The longest stretch one request may ask for, in seconds.</summary>
    public const double MaxLength = 600;

    /// <summary>
    /// Whether from and to make a stretch: numbers, from at 0 or later, to
    /// after it, at most MaxLength apart.
    /// </summary>
    public static bool Valid(double from, double to) =>
        double.IsFinite(from) && double.IsFinite(to) && from >= 0 && to > from && to - from <= MaxLength;

    /// <summary>
    /// The format a subtitle codec comes out as, or null for one the plugin
    /// doesn't serve (DVD and DVB pictures).
    /// </summary>
    public static StretchFormat? FormatOf(string? codec) => codec?.ToLowerInvariant() switch
    {
        "pgssub" or "hdmv_pgs_subtitle" or "pgs" => StretchFormat.Sup,
        "dvdsub" or "dvd_subtitle" or "dvbsub" or "dvb_subtitle" or "vobsub" or "xsub" or "" or null => null,
        _ => StretchFormat.WebVtt,
    };

    /// <summary>
    /// ffmpeg's arguments: read from `from` (a seek, not a read of the whole
    /// file), keep the file's own times, take one stream, stop writing at
    /// `to`, write it to the standard output. The end is the output's: as the
    /// input's (-t or -to before -i), some files are read to their end
    /// (Inception's 4K Matroska: 36 s for a stretch instead of 1.5 s). external: the track is a file
    /// of its own, its first subtitle stream; else stream `index` of the
    /// video file.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string path, bool external, int index, double from, double to, StretchFormat format)
    {
        var args = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-copyts",
            "-ss", Seconds(from),
            "-i", "file:" + path,
            "-map", external ? "0:s:0" : "0:" + index.ToString(CultureInfo.InvariantCulture),
            "-to", Seconds(to),
        };
        if (format == StretchFormat.Sup)
        {
            args.AddRange(["-c:s", "copy", "-f", "sup"]);
        }
        else
        {
            args.AddRange(["-c:s", "webvtt", "-f", "webvtt"]);
        }

        args.Add("pipe:1");
        return args;
    }

    /// <summary>
    /// The file's own number for Jellyfin's stream `index`: Jellyfin numbers
    /// separate subtitle files first, so a stream inside the file is its
    /// place among the file's own streams, in Jellyfin's order. -1 when it
    /// isn't one of them.
    /// </summary>
    public static int FileIndex(IEnumerable<(int Index, bool External)> streams, int index)
    {
        var place = 0;
        foreach (var s in streams.Where(s => !s.External).OrderBy(s => s.Index))
        {
            if (s.Index == index)
            {
                return place;
            }

            place++;
        }

        return -1;
    }

    private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
}
