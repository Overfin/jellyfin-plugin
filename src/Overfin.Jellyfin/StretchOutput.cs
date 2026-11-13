using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Overfin.Jellyfin;

/// <summary>
/// What the Matroska reader's blocks become for Overfin: WebVTT for text,
/// a .sup stream for pictures (PGS), as ffmpeg gives them. Overfin rebuilds
/// the WebVTT from its timings and plain text, and checks the pictures.
/// </summary>
internal static partial class StretchOutput
{
    /// <summary>The Matroska codecs read as text, and as pictures.</summary>
    public static StretchFormat? FormatOf(string codec) => codec switch
    {
        "S_TEXT/UTF8" or "S_TEXT/ASCII" or "S_TEXT/ASS" or "S_TEXT/SSA" or "S_ASS" or "S_SSA" or "S_TEXT/WEBVTT" => StretchFormat.WebVtt,
        "S_HDMV/PGS" => StretchFormat.Sup,
        _ => null,
    };

    /// <summary>
    /// WebVTT from text blocks: SRT's text as it is, ASS's and SSA's text
    /// field with its overrides taken off. A block without a duration lasts
    /// until the next, at most five seconds.
    /// </summary>
    public static byte[] WebVtt(string codec, List<MkvBlock> blocks)
    {
        var ass = codec is "S_TEXT/ASS" or "S_TEXT/SSA" or "S_ASS" or "S_SSA";
        var vtt = new StringBuilder("WEBVTT\n");
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var text = Encoding.UTF8.GetString(b.Data).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (ass)
            {
                text = AssText(text);
            }

            // One cue: no blank line inside it, no timing arrow in its text.
            var lines = new List<string>();
            foreach (var line in text.Split('\n'))
            {
                var l = line.Replace("-->", "->", StringComparison.Ordinal).Trim();
                if (l.Length > 0)
                {
                    lines.Add(l);
                }
            }

            if (lines.Count == 0)
            {
                continue;
            }

            var end = b.Duration is { } d && d > 0
                ? b.Start + d
                : Math.Min(i + 1 < blocks.Count ? blocks[i + 1].Start : double.MaxValue, b.Start + 5);
            vtt.Append('\n').Append(Time(b.Start)).Append(" --> ").Append(Time(end)).Append('\n');
            vtt.Append(string.Join('\n', lines)).Append('\n');
        }

        return Encoding.UTF8.GetBytes(vtt.ToString());
    }

    // AssText is an ASS or SSA block's text: its ninth field (ReadOrder,
    // Layer or Marked, Style, Name, MarginL, MarginR, MarginV, Effect, Text),
    // without {overrides}; \N and \n are new lines, \h a space.
    internal static string AssText(string block)
    {
        var fields = block.Split(',', 9);
        var text = fields.Length == 9 ? fields[8] : block;
        text = Overrides().Replace(text, string.Empty);
        return text.Replace("\\N", "\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\h", " ", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Overrides();

    private static string Time(double s)
    {
        var ms = (long)Math.Round(s * 1000);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}",
            ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000);
    }

    /// <summary>
    /// A .sup stream from PGS blocks: each block's segments (type, size,
    /// data), each with the header a .sup file gives it ("PG", the block's
    /// time on the 90 kHz clock, no decoding time). A segment that overruns
    /// its block ends the stream there.
    /// </summary>
    public static byte[] Sup(List<MkvBlock> blocks)
    {
        var sup = new MemoryStream();
        foreach (var b in blocks)
        {
            var pts = (uint)((long)Math.Round(b.Start * 90000) & 0xFFFFFFFF);
            var d = b.Data;
            for (var at = 0; at + 3 <= d.Length;)
            {
                var size = (d[at + 1] << 8) | d[at + 2];
                if (at + 3 + size > d.Length)
                {
                    throw new InvalidDataException("PGS segment overruns its block");
                }

                sup.Write([(byte)'P', (byte)'G', (byte)(pts >> 24), (byte)(pts >> 16), (byte)(pts >> 8), (byte)pts, 0, 0, 0, 0]);
                sup.Write(d, at, 3 + size);
                at += 3 + size;
            }
        }

        return sup.ToArray();
    }
}
