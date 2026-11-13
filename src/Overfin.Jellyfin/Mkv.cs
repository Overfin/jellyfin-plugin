using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace Overfin.Jellyfin;

/// <summary>One subtitle block of a Matroska track: its time and data.</summary>
internal sealed record MkvBlock(double Start, double? Duration, byte[] Data);

/// <summary>
/// Reads one subtitle track of a Matroska (or WebM) file between two times,
/// from the file's own index: it starts at the cluster before the stretch
/// and stops at the first cluster after it, whatever the track holds. ffmpeg
/// can't stop on a sparse track (forced subtitles: a few lines a film) and
/// reads such a file to its end for each stretch.
///
/// The file is untrusted: every size is checked against what holds it and
/// against fixed limits; anything unexpected gives up (null), and the caller
/// uses ffmpeg instead.
/// </summary>
internal static class Mkv
{
    // Element IDs, as the Matroska specification numbers them.
    private const uint EbmlId = 0x1A45DFA3, SegmentId = 0x18538067, SeekHeadId = 0x114D9B74, SeekId = 0x4DBB,
        SeekIdId = 0x53AB, SeekPositionId = 0x53AC, InfoId = 0x1549A966, TimestampScaleId = 0x2AD7B1,
        TracksId = 0x1654AE6B, TrackEntryId = 0xAE, TrackTypeId = 0x83, CodecIdId = 0x86,
        ContentEncodingsId = 0x6D80, ContentEncodingId = 0x6240, ContentEncodingTypeId = 0x5033,
        ContentEncodingScopeId = 0x5032, ContentCompressionId = 0x5034, ContentCompAlgoId = 0x4254,
        ContentCompSettingsId = 0x4255, TrackNumberId = 0xD7, CuesId = 0x1C53BB6B, CuePointId = 0xBB,
        CueTimeId = 0xB3, CueTrackPositionsId = 0xB7, CueClusterPositionId = 0xF1, ClusterId = 0x1F43B675,
        TimestampId = 0xE7, SimpleBlockId = 0xA3, BlockGroupId = 0xA0, BlockId = 0xA1, BlockDurationId = 0x9B,
        VoidId = 0xEC;

    private const int SubtitleTrackType = 0x11;
    private const long MaxHeader = 16 << 20; // SeekHead, Info, Tracks
    private const long MaxCues = 64 << 20;
    private const int MaxBlock = 4 << 20; // one subtitle block, as stored
    private const int MaxFrame = 16 << 20; // one subtitle block, uncompressed
    private const long MaxRead = 4L << 30; // bytes walked for one stretch, at most

    /// <summary>
    /// The blocks of the file's subtitle track `fileIndex` (its place among
    /// the file's tracks) starting in [from, to) seconds, with its codec; null
    /// when the file isn't one this reads.
    /// </summary>
    public static (string Codec, List<MkvBlock> Blocks)? Read(string path, int fileIndex, double from, double to, CancellationToken cancel)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 << 10);
        return ReadFrom(file, fileIndex, from, to, cancel);
    }

    /// <summary>Read, from a stream; a damaged one gives null.</summary>
    internal static (string Codec, List<MkvBlock> Blocks)? ReadFrom(Stream file, int fileIndex, double from, double to, CancellationToken cancel)
    {
        try
        {
            return Walk(file, fileIndex, from, to, cancel);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    private static (string Codec, List<MkvBlock> Blocks)? Walk(Stream file, int fileIndex, double from, double to, CancellationToken cancel)
    {
        var r = new Reader(file);
        // The EBML header, then the segment, whose positions count from its data.
        var head = r.Header();
        if (head.Id != EbmlId || head.Size is null)
        {
            return null;
        }

        r.Skip(head.Size.Value);
        var segment = r.Header();
        if (segment.Id != SegmentId)
        {
            return null;
        }

        var segmentStart = file.Position;

        // The SeekHead says where Info, Tracks and Cues are: the segment's
        // first element, after padding if any.
        var seekHead = r.Header();
        while (seekHead.Id == VoidId && seekHead.Size is not null)
        {
            r.Skip(seekHead.Size.Value);
            seekHead = r.Header();
        }

        if (seekHead.Id != SeekHeadId || seekHead.Size is null || seekHead.Size > MaxHeader)
        {
            return null;
        }

        // Some muxers keep the rest in more SeekHeads, which one names
        // after the other: followed, four at most, none twice.
        var where = new Dictionary<uint, long>();
        var seen = new HashSet<long> { segmentStart };
        var next = Seeks(r.Bytes(seekHead.Size.Value), segmentStart, where);
        for (var n = 0; n < 4 && !where.ContainsKey(CuesId) && next is { } at && seen.Add(at); n++)
        {
            next = Seeks(r.ElementAt(at, SeekHeadId, MaxHeader), segmentStart, where);
        }

        if (!where.TryGetValue(InfoId, out var infoAt) || !where.TryGetValue(TracksId, out var tracksAt)
            || !where.TryGetValue(CuesId, out var cuesAt))
        {
            return null;
        }

        // Info: how long a tick is (ns).
        var scale = 1_000_000UL;
        foreach (var e in Children(r.ElementAt(infoAt, InfoId, MaxHeader)))
        {
            if (e.Id == TimestampScaleId)
            {
                scale = UInt(e.Data);
            }
        }

        if (scale == 0)
        {
            return null;
        }

        // Tracks: the one at that place, a subtitle track, and how its blocks
        // are stored.
        var entries = new List<Element>();
        foreach (var e in Children(r.ElementAt(tracksAt, TracksId, MaxHeader)))
        {
            if (e.Id == TrackEntryId)
            {
                entries.Add(e);
            }
        }

        if (fileIndex >= entries.Count)
        {
            return null;
        }

        ulong number = 0;
        var type = 0UL;
        var codec = string.Empty;
        Element? encodings = null;
        foreach (var e in Children(entries[fileIndex].Data))
        {
            switch (e.Id)
            {
                case TrackNumberId: number = UInt(e.Data); break;
                case TrackTypeId: type = UInt(e.Data); break;
                case CodecIdId: codec = System.Text.Encoding.ASCII.GetString(e.Data).TrimEnd('\0'); break;
                case ContentEncodingsId: encodings = e; break;
            }
        }

        if (type != SubtitleTrackType || number == 0)
        {
            return null;
        }

        var stored = encodings is null ? Stored.Plain : StoredAs(encodings.Value);
        if (stored is null)
        {
            return null;
        }

        // Cues: the last cluster that starts at or before `from`.
        var fromTicks = from * 1e9 / scale;
        var toTicks = to * 1e9 / scale;
        // The cluster to start from: the last the index lists at or before
        // `from`, else its first.
        long start = -1, first = -1;
        double startTime = -1, firstTime = double.MaxValue;
        foreach (var point in Children(r.ElementAt(cuesAt, CuesId, MaxCues)))
        {
            if (point.Id != CuePointId)
            {
                continue;
            }

            double time = -1;
            long pos = -1;
            foreach (var e in Children(point.Data))
            {
                if (e.Id == CueTimeId)
                {
                    time = UInt(e.Data);
                }
                else if (e.Id == CueTrackPositionsId && pos < 0)
                {
                    foreach (var p in Children(e.Data))
                    {
                        if (p.Id == CueClusterPositionId)
                        {
                            pos = (long)UInt(p.Data);
                        }
                    }
                }
            }

            if (time < 0 || pos < 0)
            {
                continue;
            }

            if (time <= fromTicks && time > startTime)
            {
                (start, startTime) = (segmentStart + pos, time);
            }

            if (time < firstTime)
            {
                (first, firstTime) = (segmentStart + pos, time);
            }
        }

        if (start < 0)
        {
            start = first;
        }

        if (start < 0)
        {
            return null;
        }

        // The clusters from there, until one starts after `to`.
        var blocks = new List<MkvBlock>();
        file.Position = start;
        var walked = 0L;
        while (file.Position < file.Length)
        {
            cancel.ThrowIfCancellationRequested();
            var at = file.Position;
            var el = r.Header();
            if (el.Size is null)
            {
                return null; // a cluster of unknown size: streamed files, not read here
            }

            var end = file.Position + el.Size.Value;
            if (el.Id != ClusterId)
            {
                file.Position = end;
                continue;
            }

            double clusterTime = -1;
            while (file.Position < end)
            {
                var child = r.Header();
                if (child.Size is null || file.Position + child.Size.Value > end)
                {
                    return null;
                }

                var childEnd = file.Position + child.Size.Value;
                switch (child.Id)
                {
                    case TimestampId:
                        clusterTime = UInt(r.Bytes(child.Size.Value));
                        if (clusterTime > toTicks)
                        {
                            return (codec, blocks);
                        }

                        break;
                    case SimpleBlockId:
                        if (clusterTime < 0)
                        {
                            return null;
                        }

                        ReadBlock(r, child.Size.Value, number, clusterTime, null, scale, fromTicks, toTicks, stored, blocks);
                        break;
                    case BlockGroupId:
                        if (clusterTime < 0)
                        {
                            return null;
                        }

                        ReadGroup(r, childEnd, number, clusterTime, scale, fromTicks, toTicks, stored, blocks);
                        break;
                }

                file.Position = childEnd;
            }

            walked += end - at;
            if (walked > MaxRead)
            {
                return null;
            }
        }

        return (codec, blocks);
    }

    // Seeks reads a SeekHead's entries: which element is where, and where
    // the SeekHead it names is, if any.
    private static long? Seeks(byte[] seekHead, long segmentStart, Dictionary<uint, long> where)
    {
        long? next = null;
        foreach (var seek in Children(seekHead))
        {
            if (seek.Id != SeekId)
            {
                continue;
            }

            uint id = 0;
            long pos = -1;
            foreach (var e in Children(seek.Data))
            {
                if (e.Id == SeekIdId)
                {
                    id = (uint)UInt(e.Data);
                }
                else if (e.Id == SeekPositionId)
                {
                    pos = (long)UInt(e.Data);
                }
            }

            if (id == SeekHeadId && pos >= 0)
            {
                next ??= segmentStart + pos;
            }
            else if (id != 0 && pos >= 0)
            {
                where.TryAdd(id, segmentStart + pos);
            }
        }

        return next;
    }

    /// <summary>How a track's blocks are stored: as they are, after a header taken off, or zlib.</summary>
    internal sealed record Stored(byte[] Stripped, bool Zlib)
    {
        public static readonly Stored Plain = new([], false);
    }

    // StoredAs reads a track's ContentEncodings: compression of its frames
    // by zlib or by header stripping; anything else (encryption, other
    // compressions, more than one encoding) isn't read here.
    private static Stored? StoredAs(Element encodings)
    {
        Stored? stored = null;
        foreach (var encoding in Children(encodings.Data))
        {
            if (encoding.Id != ContentEncodingId || stored is not null)
            {
                return null;
            }

            ulong kind = 0, scope = 1, algo = 0;
            byte[] settings = [];
            var compressed = false;
            foreach (var e in Children(encoding.Data))
            {
                switch (e.Id)
                {
                    case ContentEncodingTypeId: kind = UInt(e.Data); break;
                    case ContentEncodingScopeId: scope = UInt(e.Data); break;
                    case ContentCompressionId:
                        compressed = true;
                        foreach (var c in Children(e.Data))
                        {
                            if (c.Id == ContentCompAlgoId)
                            {
                                algo = UInt(c.Data);
                            }
                            else if (c.Id == ContentCompSettingsId)
                            {
                                settings = c.Data;
                            }
                        }

                        break;
                }
            }

            if (kind != 0 || !compressed || scope != 1)
            {
                return null;
            }

            stored = algo switch
            {
                0 => new Stored([], true),
                3 => new Stored(settings, false),
                _ => null,
            };
            if (stored is null)
            {
                return null;
            }
        }

        return stored ?? Stored.Plain;
    }

    // ReadGroup reads a BlockGroup: its Block, when it's the track's, and
    // its BlockDuration. Another track's group is left at its block's first
    // bytes.
    private static void ReadGroup(Reader r, long end, ulong number, double clusterTime, ulong scale, double fromTicks, double toTicks, Stored stored, List<MkvBlock> blocks)
    {
        (long At, long Size)? block = null;
        ulong? duration = null;
        while (r.Position < end)
        {
            var h = r.Header();
            if (h.Size is null || r.Position + h.Size.Value > end)
            {
                throw new InvalidDataException("element larger than what holds it");
            }

            var next = r.Position + h.Size.Value;
            if (h.Id == BlockId)
            {
                var at = r.Position;
                if (r.Vint(out _) != number)
                {
                    return;
                }

                block = (at, h.Size.Value);
            }
            else if (h.Id == BlockDurationId)
            {
                duration = UInt(r.Bytes(h.Size.Value));
            }

            r.Seek(next);
        }

        if (block is { } b)
        {
            r.Seek(b.At);
            ReadBlock(r, b.Size, number, clusterTime, duration, scale, fromTicks, toTicks, stored, blocks);
        }
    }

    // ReadBlock reads a block's header and, when it's the track's and starts
    // in the stretch, its frame; others are skipped unread.
    private static void ReadBlock(Reader r, long size, ulong number, double clusterTime, ulong? duration, ulong scale, double fromTicks, double toTicks, Stored stored, List<MkvBlock> blocks)
    {
        var before = r.Position;
        var track = r.Vint(out _);
        if (track != number)
        {
            return;
        }

        var relative = (short)((r.Byte() << 8) | r.Byte());
        var flags = r.Byte();
        var headerLength = r.Position - before;
        if ((flags & 0x06) != 0 || size - headerLength > MaxBlock)
        {
            return; // laced: not for subtitles
        }

        var time = clusterTime + relative;
        if (time < fromTicks || time >= toTicks)
        {
            return;
        }

        var frame = Frame(r.Bytes(size - headerLength), stored);
        blocks.Add(new MkvBlock(time * scale / 1e9, duration is null ? null : duration.Value * (double)scale / 1e9, frame));
    }

    private static byte[] Frame(byte[] data, Stored stored)
    {
        if (stored.Zlib)
        {
            using var input = new ZLibStream(new MemoryStream(data, false), CompressionMode.Decompress);
            var output = new MemoryStream();
            var buffer = new byte[16 << 10];
            int n;
            while ((n = input.Read(buffer)) > 0)
            {
                if (output.Length + n > MaxFrame)
                {
                    throw new InvalidDataException("subtitle frame too large");
                }

                output.Write(buffer, 0, n);
            }

            return output.ToArray();
        }

        if (stored.Stripped.Length == 0)
        {
            return data;
        }

        var whole = new byte[stored.Stripped.Length + data.Length];
        stored.Stripped.CopyTo(whole, 0);
        data.CopyTo(whole, stored.Stripped.Length);
        return whole;
    }

    // ---- EBML -------------------------------------------------------------

    internal readonly record struct Element(uint Id, byte[] Data);

    // Children reads the elements inside one, all of known size within it.
    internal static IEnumerable<Element> Children(byte[] data)
    {
        var r = new Reader(new MemoryStream(data, false));
        while (r.Position < data.Length)
        {
            var h = r.Header();
            if (h.Size is null || h.Size.Value > data.Length - r.Position)
            {
                throw new InvalidDataException("element larger than what holds it");
            }

            yield return new Element(h.Id, r.Bytes(h.Size.Value));
        }
    }

    internal static ulong UInt(byte[] data)
    {
        if (data.Length > 8)
        {
            throw new InvalidDataException("integer too long");
        }

        ulong v = 0;
        foreach (var b in data)
        {
            v = (v << 8) | b;
        }

        return v;
    }

    internal sealed class Reader(Stream s)
    {
        public long Position => s.Position;

        public int Byte()
        {
            var b = s.ReadByte();
            return b < 0 ? throw new EndOfStreamException() : b;
        }

        // Vint reads a variable-size integer, its marker bit taken off;
        // `all` says whether every value bit was set (an unknown size).
        public ulong Vint(out bool all)
        {
            var first = Byte();
            var length = 1;
            while (length <= 8 && (first & (0x80 >> (length - 1))) == 0)
            {
                length++;
            }

            if (length > 8)
            {
                throw new InvalidDataException("bad variable-size integer");
            }

            ulong v = (ulong)(first & (0xFF >> length));
            var ones = v == (ulong)(0xFF >> length);
            for (var i = 1; i < length; i++)
            {
                var b = Byte();
                ones &= b == 0xFF;
                v = (v << 8) | (uint)b;
            }

            all = ones;
            return v;
        }

        // Header reads an element's ID (with its marker, as IDs are written)
        // and its size, null when unknown.
        public (uint Id, long? Size) Header()
        {
            var first = Byte();
            var length = first >= 0x80 ? 1 : first >= 0x40 ? 2 : first >= 0x20 ? 3 : first >= 0x10 ? 4 : 0;
            if (length == 0)
            {
                throw new InvalidDataException("bad element ID");
            }

            uint id = (uint)first;
            for (var i = 1; i < length; i++)
            {
                id = (id << 8) | (uint)Byte();
            }

            var size = Vint(out var unknown);
            if (unknown)
            {
                return (id, null);
            }

            if (size > long.MaxValue / 2)
            {
                throw new InvalidDataException("element too large");
            }

            return (id, (long)size);
        }

        public byte[] Bytes(long n)
        {
            if (n < 0 || n > int.MaxValue)
            {
                throw new InvalidDataException("element too large");
            }

            var b = new byte[n];
            s.ReadExactly(b);
            return b;
        }

        public void Skip(long n) => s.Seek(n, SeekOrigin.Current);

        public void Seek(long at) => s.Position = at;

        // ElementAt reads the element at a position, which must be that one,
        // of a known size within limit.
        public byte[] ElementAt(long at, uint id, long limit)
        {
            s.Position = at;
            var h = Header();
            if (h.Id != id || h.Size is null || h.Size.Value > limit)
            {
                throw new InvalidDataException("not the element the SeekHead names");
            }

            return Bytes(h.Size.Value);
        }
    }
}
