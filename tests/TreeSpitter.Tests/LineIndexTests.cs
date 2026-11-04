using Xunit;

namespace TreeSpitter.Tests;

public sealed class LineIndexTests
{
    [Fact]
    public void ALazyIndexHasTheRowsOfACompleteIndex()
    {
        // The line starts are 0, 2, 5, 6, 9 and 12
        var text = "a\nbc\n\nd\r\nef\n"u8.ToArray();
        var complete = new LineIndex(text, text.Length);
        var lazy = new LineIndex();
        lazy.Reset([.. "x\n\n"u8], 3);
        Assert.Equal(2, lazy.Row(3));

        // The same index for another text, queried out of order and past the end
        lazy.Reset(text, text.Length);
        foreach (var position in new[] { 7, 2, 0, 12, 5, 6, 1, 13, 3, 9 })
        {
            Assert.Equal(complete.Row(position), lazy.Row(position));
            Assert.Equal(complete.Column(position), lazy.Column(position));
        }

        Assert.Equal(3, complete.Row(7));
        Assert.Equal(1, complete.Column(7));
        Assert.Equal(5, complete.Row(12));
    }
}
