using System.Linq;
using Overfin.Jellyfin;
using Xunit;

public class ChangeFeedTests
{
    [Fact]
    public void Numbered()
    {
        var feed = new ChangeFeed();
        var a = System.Guid.NewGuid();
        var b = System.Guid.NewGuid();
        feed.Note(a, false);
        feed.Note(b, true);
        Assert.Equal(2, feed.Last);
        var got = feed.Since(feed.Generation, 0, 10);
        Assert.NotNull(got);
        Assert.Equal([(1L, a.ToString("N"), false), (2L, b.ToString("N"), true)],
            got.Value.Changes.Select(c => (c.Seq, c.Id, c.Removed)));
        Assert.Equal(["2"], feed.Since(feed.Generation, 1, 10)!.Value.Changes.Select(c => c.Seq.ToString()));
        Assert.Empty(feed.Since(feed.Generation, 2, 10)!.Value.Changes);
        Assert.Single(feed.Since(feed.Generation, 0, 1)!.Value.Changes);
    }

    // Another generation, a place ahead of the feed, or one let go: read again.
    [Fact]
    public void Resets()
    {
        var feed = new ChangeFeed();
        Assert.Null(feed.Since("another", 0, 10));
        Assert.Null(feed.Since(feed.Generation, 5, 10));
        for (var i = 0; i < ChangeFeed.Capacity + 3; i++)
        {
            feed.Note(System.Guid.NewGuid(), false);
        }

        Assert.Null(feed.Since(feed.Generation, 1, 10));
        Assert.NotNull(feed.Since(feed.Generation, 3, 10));
    }

    [Fact]
    public void Names()
    {
        Assert.Equal("Film (2001).mkv", IndexItems.NameOf("/media/Films/Film (2001)/Film (2001).mkv"));
        Assert.Equal("/media/Films/Film (2001)", IndexItems.DirectoryOf("/media/Films/Film (2001)/Film (2001).mkv"));
        Assert.Equal("Show", IndexItems.NameOf(@"D:\Series\Show\"));
        Assert.Null(IndexItems.NameOf(""));
        Assert.Null(IndexItems.DirectoryOf("file.mkv"));
    }
}
