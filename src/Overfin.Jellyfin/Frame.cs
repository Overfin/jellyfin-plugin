using System.Collections.Generic;
using System.Globalization;

namespace Overfin.Jellyfin;

/// <summary>
/// One frame of a video, for an episode TMDB has no picture for: ffmpeg's
/// arguments, from values the route checked. HDR is mapped to standard range
/// with Jellyfin's own tone mapping, as its conversions do, or the picture
/// would look grey and washed out.
/// </summary>
internal static class Frame
{
    /// <summary>The widest frame given; narrower videos keep their width.</summary>
    public const int MaxWidth = 1920;

    /// <summary>The latest moment asked for, in seconds: two days.</summary>
    public const double Latest = 2 * 24 * 3600;

    /// <summary>Whether a moment is one: a number, from 0 to Latest.</summary>
    public static bool Valid(double at) => double.IsFinite(at) && at >= 0 && at <= Latest;

    /// <summary>
    /// ffmpeg's arguments: seek to `at` in the file (a seek, then the frames
    /// up to it decoded), the first video stream that isn't a cover picture,
    /// one frame, tone-mapped when hdr, at most MaxWidth wide, as a JPEG on
    /// the standard output.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string path, double at, bool hdr)
    {
        var scale = "scale=w=min(" + MaxWidth.ToString(CultureInfo.InvariantCulture) + "\\,iw):h=-2";
        var filter = hdr
            ? "tonemapx=tonemap=bt2390:transfer=bt709:matrix=bt709:primaries=bt709:range=tv:format=yuv420p," + scale
            : scale;
        return
        [
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-ss", at.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", "file:" + path,
            "-map", "0:V:0", "-frames:v", "1", "-vf", filter,
            "-q:v", "3", "-f", "mjpeg", "pipe:1",
        ];
    }
}
