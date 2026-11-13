using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Overfin.Jellyfin;
using Xunit;

public class MkvTests
{
    // ---- A small Matroska file, built here --------------------------------

    private static byte[] Id(uint id) =>
        id > 0xFFFFFF ? [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id]
        : id > 0xFFFF ? [(byte)(id >> 16), (byte)(id >> 8), (byte)id]
        : id > 0xFF ? [(byte)(id >> 8), (byte)id]
        : [(byte)id];

    // Sizes on 8 bytes: positions don't move as sizes change.
    private static byte[] Size(long n) => [0x01, (byte)(n >> 48), (byte)(n >> 40), (byte)(n >> 32), (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n];

    private static byte[] El(uint id, params byte[][] content)
    {
        var data = content.SelectMany(c => c).ToArray();
        return [.. Id(id), .. Size(data.Length), .. data];
    }

    private static byte[] UInt(ulong v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static byte[] Str(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Block(int track, short relative, byte[] frame) =>
        [(byte)(0x80 | track), (byte)(relative >> 8), (byte)relative, 0, .. frame];

    private sealed record Line(int Cluster, short Relative, string Text);

    // A file of `clusters` clusters a second apart (ms ticks), a video
    // block in each, the subtitle track's lines where given; after the
    // clusters, `tail` (the Cues come first, so garbage there is only read
    // by a reader that walks too far).
    private static byte[] File(int clusters, IEnumerable<Line> lines, string codec = "S_TEXT/UTF8", byte[]? encodings = null,
        Func<string, byte[]>? frame = null, byte[]? tail = null)
    {
        frame ??= Encoding.UTF8.GetBytes;
        var info = El(0x1549A966, El(0x2AD7B1, UInt(1_000_000)));
        var tracks = El(0x1654AE6B,
            El(0xAE, El(0xD7, UInt(1)), El(0x83, UInt(1)), El(0x86, Str("V_MPEG4/ISO/AVC"))),
            El(0xAE, [.. El(0xD7, UInt(2)), .. El(0x83, UInt(0x11)), .. El(0x86, Str(codec)), .. (encodings ?? [])]));
        var byCluster = lines.GroupBy(l => l.Cluster).ToDictionary(g => g.Key, g => g.ToList());
        var clusterBytes = new List<byte[]>();
        for (var c = 0; c < clusters; c++)
        {
            var parts = new List<byte[]> { El(0xE7, UInt((ulong)(c * 1000))), El(0xA3, Block(1, 0, new byte[200])) };
            foreach (var l in byCluster.GetValueOrDefault(c, []))
            {
                parts.Add(El(0xA0, El(0xA1, Block(2, l.Relative, frame(l.Text))), El(0x9B, UInt(500))));
            }

            clusterBytes.Add(El(0x1F43B675, [.. parts]));
        }

        // Layout in the segment: SeekHead, Info, Tracks, Cues, clusters, tail.
        var seekHeadLength = El(0x114D9B74, Seek(0, 0), Seek(0, 0), Seek(0, 0)).Length;
        var infoAt = seekHeadLength;
        var tracksAt = infoAt + info.Length;
        var cuesAt = tracksAt + tracks.Length;
        byte[] Cues(long clustersAt)
        {
            var points = new List<byte[]>();
            var at = clustersAt;
            for (var c = 0; c < clusters; c++)
            {
                if (c % 3 == 0) // the index lists every third cluster
                {
                    points.Add(El(0xBB, El(0xB3, UInt((ulong)(c * 1000))), El(0xB7, El(0xF7, UInt(1)), El(0xF1, UInt((ulong)at)))));
                }

                at += clusterBytes[c].Length;
            }

            return El(0x1C53BB6B, [.. points]);
        }

        var cuesLength = Cues(0).Length;
        var cues = Cues(cuesAt + cuesLength);
        var seekHead = El(0x114D9B74, Seek(0x1549A966, infoAt), Seek(0x1654AE6B, tracksAt), Seek(0x1C53BB6B, cuesAt));
        var segment = El(0x18538067, seekHead, info, tracks, cues, [.. clusterBytes.SelectMany(c => c)], tail ?? []);
        return [.. El(0x1A45DFA3, El(0x4282, Str("matroska"))), .. segment];

        static byte[] Seek(uint id, long at) => El(0x4DBB, El(0x53AB, Id(id).Length < 4 ? [.. new byte[4 - Id(id).Length], .. Id(id)] : Id(id)), El(0x53AC, UInt((ulong)at)));
    }

    private static (string Codec, List<MkvBlock> Blocks)? Read(byte[] file, double from, double to) =>
        Mkv.ReadFrom(new MemoryStream(file, false), 1, from, to, CancellationToken.None);

    private static readonly Line[] Lines =
    [
        new(1, 200, "one"), new(4, 100, "two"), new(4, 900, "three"), new(7, 0, "four"), new(12, 300, "five"),
    ];

    // ---- What it reads -----------------------------------------------------

    [Fact]
    public void OnlyTheStretch()
    {
        var read = Read(File(20, Lines), 4.5, 12);
        Assert.NotNull(read);
        Assert.Equal("S_TEXT/UTF8", read.Value.Codec);
        Assert.Equal(["three", "four"], read.Value.Blocks.Select(b => Encoding.UTF8.GetString(b.Data)));
        Assert.Equal([4.9, 7.0], read.Value.Blocks.Select(b => b.Start));
        Assert.All(read.Value.Blocks, b => Assert.Equal(0.5, b.Duration));
    }

    // A sparse track: it stops at the first cluster after the stretch, never
    // reading on to the next line (garbage after the clusters proves it).
    [Fact]
    public void StopsAfterTheStretch()
    {
        var garbage = Enumerable.Repeat((byte)0xFF, 64).ToArray();
        var read = Read(File(20, [new(1, 0, "only")], tail: garbage), 0, 3);
        Assert.Equal(["only"], read!.Value.Blocks.Select(b => Encoding.UTF8.GetString(b.Data)));
        // Reading to the end does reach the garbage, and gives up.
        Assert.Null(Read(File(20, [new(1, 0, "only")], tail: garbage), 0, 100));
    }

    [Fact]
    public void WebVtt()
    {
        var read = Read(File(20, [new(2, 0, "Hi\r\nthere"), new(3, 0, "a --> b")]), 0, 10)!.Value;
        Assert.Equal("WEBVTT\n\n00:00:02.000 --> 00:00:02.500\nHi\nthere\n\n00:00:03.000 --> 00:00:03.500\na -> b\n",
            Encoding.UTF8.GetString(StretchOutput.WebVtt(read.Codec, read.Blocks)));
        Assert.Equal("Hello\nworld here", StretchOutput.AssText(@"3,0,Default,,0,0,0,,{\i1}Hello\Nworld\hhere"));
    }

    // PGS, zlib-compressed as mkvmerge stores it: each segment gets its .sup
    // header, at the block's time.
    [Fact]
    public void PicturesCompressed()
    {
        var zlib = El(0x6D80, El(0x6240, El(0x5034, El(0x4254, UInt(0)))));
        byte[] Compress(string s)
        {
            var segments = new byte[] { 0x16, 0, 2, 0xAA, 0xBB, 0x80, 0, 0 };
            var output = new MemoryStream();
            using (var z = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(segments);
            }

            return output.ToArray();
        }

        var read = Read(File(5, [new(2, 0, "x")], "S_HDMV/PGS", zlib, Compress), 0, 5)!.Value;
        var sup = StretchOutput.Sup(read.Blocks);
        Assert.Equal(new byte[]
        {
            (byte)'P', (byte)'G', 0, 2, 0xBF, 0x20, 0, 0, 0, 0, 0x16, 0, 2, 0xAA, 0xBB,
            (byte)'P', (byte)'G', 0, 2, 0xBF, 0x20, 0, 0, 0, 0, 0x80, 0, 0,
        }, sup);
    }

    // Not Matroska, damaged, or not a subtitle track: not read here.
    [Fact]
    public void GivesUp()
    {
        Assert.Null(Mkv.ReadFrom(new MemoryStream(Encoding.ASCII.GetBytes("not a matroska file at all"), false), 1, 0, 10, CancellationToken.None));
        Assert.Null(Mkv.ReadFrom(new MemoryStream(File(5, Lines), false), 0, 0, 10, CancellationToken.None)); // the video track
        var cut = File(5, Lines)[..^10]; // cut off inside its last cluster
        Assert.Null(Read(cut, 0, 10));
        var bzip = El(0x6D80, El(0x6240, El(0x5034, El(0x4254, UInt(1)))));
        Assert.Null(Read(File(5, Lines, encodings: bzip), 0, 10));
    }
}
