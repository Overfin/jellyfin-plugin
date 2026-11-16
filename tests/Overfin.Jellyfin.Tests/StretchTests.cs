using System.Linq;
using Overfin.Jellyfin;
using Xunit;

public class StretchTests
{
    [Fact]
    public void Valid()
    {
        Assert.True(Stretch.Valid(0, 120));
        Assert.True(Stretch.Valid(1790, 1920));
        Assert.True(Stretch.Valid(0, Stretch.MaxLength));
        Assert.False(Stretch.Valid(0, Stretch.MaxLength + 1));
        Assert.False(Stretch.Valid(-1, 10));
        Assert.False(Stretch.Valid(10, 10));
        Assert.False(Stretch.Valid(20, 10));
        Assert.False(Stretch.Valid(double.NaN, 10));
        Assert.False(Stretch.Valid(0, double.PositiveInfinity));
    }

    [Fact]
    public void FormatOf()
    {
        Assert.Equal(StretchFormat.Sup, Stretch.FormatOf("PGSSUB"));
        Assert.Equal(StretchFormat.Sup, Stretch.FormatOf("hdmv_pgs_subtitle"));
        Assert.Equal(StretchFormat.WebVtt, Stretch.FormatOf("subrip"));
        Assert.Equal(StretchFormat.WebVtt, Stretch.FormatOf("ass"));
        Assert.Equal(StretchFormat.WebVtt, Stretch.FormatOf("mov_text"));
        Assert.Null(Stretch.FormatOf("dvd_subtitle"));
        Assert.Null(Stretch.FormatOf("dvbsub"));
        Assert.Null(Stretch.FormatOf(null));
    }

    // The path is always read as a file, whatever it looks like; times are
    // plain numbers, whatever the server's culture.
    [Fact]
    public void Arguments()
    {
        var args = Stretch.Arguments("/media/a:b.mkv", false, 7, 1790.5, 1920, StretchFormat.Sup);
        Assert.Equal(
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-copyts", "-start_at_zero", "-ss", "1790.5",
             "-i", "file:/media/a:b.mkv", "-map", "0:7", "-to", "1920", "-c:s", "copy", "-f", "sup", "pipe:1"],
            args);
        // Matroska keeps its own times, as its reader does.
        Assert.DoesNotContain("-start_at_zero", Stretch.Arguments("/media/a.mkv", false, 3, 0, 120, StretchFormat.WebVtt, null, true));
        Assert.True(Stretch.IsMatroska("mkv") && Stretch.IsMatroska("matroska,webm") && !Stretch.IsMatroska("mpegts") && !Stretch.IsMatroska(null));
        var text = Stretch.Arguments("/media/a.fr.srt", true, 12, 0, 120, StretchFormat.WebVtt);
        Assert.Contains("0:s:0", text);
        Assert.Equal(["-c:s", "webvtt", "-f", "webvtt", "pipe:1"], text.Skip(text.Count - 5));
    }

    // A separate text file's character set goes to ffmpeg before the input,
    // unless it's UTF-8 or not a plain name; never for a track in the video.
    [Fact]
    public void Charset()
    {
        var latin = Stretch.Arguments("/media/a.fr.srt", true, 0, 0, 120, StretchFormat.WebVtt, "windows-1252");
        var at = latin.ToList().IndexOf("-sub_charenc");
        Assert.True(at >= 0 && latin[at + 1] == "windows-1252" && at < latin.ToList().IndexOf("-i"));
        Assert.DoesNotContain("-sub_charenc", Stretch.Arguments("/media/a.fr.srt", true, 0, 0, 120, StretchFormat.WebVtt, "UTF-8"));
        Assert.DoesNotContain("-sub_charenc", Stretch.Arguments("/media/a.fr.srt", true, 0, 0, 120, StretchFormat.WebVtt, "x; -y"));
        Assert.DoesNotContain("-sub_charenc", Stretch.Arguments("/media/a.mkv", false, 3, 0, 120, StretchFormat.WebVtt, "windows-1252"));
    }

    // A separate .srt numbered 0 by Jellyfin shifts the file's own streams.
    [Fact]
    public void FileIndex()
    {
        var streams = new[] { (0, true), (1, false), (2, false), (3, false), (4, false) };
        Assert.Equal(2, Stretch.FileIndex(streams, 3));
        Assert.Equal(3, Stretch.FileIndex(streams, 4));
        Assert.Equal(-1, Stretch.FileIndex(streams, 0));
        Assert.Equal(-1, Stretch.FileIndex(streams, 9));
        Assert.Equal(3, Stretch.FileIndex(new[] { (0, false), (1, false), (2, false), (3, false) }, 3));
    }
}
