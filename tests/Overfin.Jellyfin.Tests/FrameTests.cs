using System.Linq;
using Overfin.Jellyfin;
using Xunit;

public class FrameTests
{
    [Fact]
    public void Valid()
    {
        Assert.True(Frame.Valid(0));
        Assert.True(Frame.Valid(600.5));
        Assert.False(Frame.Valid(-1));
        Assert.False(Frame.Valid(double.NaN));
        Assert.False(Frame.Valid(Frame.Latest + 1));
    }

    // The path is read as a file; HDR is tone-mapped before the scaling.
    [Fact]
    public void Arguments()
    {
        var sdr = Frame.Arguments("/media/a:b.mkv", 612.25, false);
        Assert.Equal(["-nostdin", "-hide_banner", "-loglevel", "error", "-ss", "612.25", "-i", "file:/media/a:b.mkv",
            "-map", "0:V:0", "-frames:v", "1", "-vf", "scale=w=min(1920\\,iw):h=-2", "-q:v", "3", "-f", "mjpeg", "pipe:1"], sdr);
        var hdr = Frame.Arguments("/media/x.mkv", 1, true);
        Assert.StartsWith("tonemapx=", hdr[hdr.ToList().IndexOf("-vf") + 1]);
    }
}
