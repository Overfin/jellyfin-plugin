using System;
using Overfin.Jellyfin;
using Xunit;

public class IndexItemsTests
{
    // An item in two libraries is listed in the same one whichever order
    // they come in.
    [Fact]
    public void LowerLibrary()
    {
        var a = Guid.Parse("10000000-0000-0000-0000-000000000000");
        var b = Guid.Parse("f0000000-0000-0000-0000-000000000000");
        Assert.Equal(a, IndexItems.Lower(a, b));
        Assert.Equal(a, IndexItems.Lower(b, a));
        Assert.Equal(b, IndexItems.Lower(b, b));
    }
}
