using WinLens.Layout;

namespace WinLens.Core.Tests;

public class BackdropTests
{
    private const int White = 0xFFFFFF, Black = 0x000000, Blue = 0x2060C0;

    [Fact]
    public void Colour_OfAPlainBackground_IsThatColour()
    {
        Assert.Equal(Blue, Backdrop.Colour(Enumerable.Repeat(Blue, 40).ToArray()));
    }

    [Fact]
    public void Colour_IgnoresTheSamplesThatLandedOnSomethingElse()
    {
        // A dark page; a quarter of the samples hit the white descenders of the line above.
        var samples = Enumerable.Repeat(0x0F1220, 30).Concat(Enumerable.Repeat(White, 10)).ToArray();

        Assert.Equal(0x0F1220, Backdrop.Colour(samples));
    }

    [Fact]
    public void Colour_OnAFlatBackground_IsExact_NextToAnotherShade()
    {
        // A line near the edge of a white card: a third of the samples fall on the page.
        var samples = Enumerable.Repeat(White, 40).Concat(Enumerable.Repeat(0xF4F5F7, 20)).ToArray();

        Assert.Equal(White, Backdrop.Colour(samples));
    }

    [Fact]
    public void Colour_OfAGradient_IsItsMiddle()
    {
        // 0x40..0x5F on every channel: all of it is background, none is left out.
        var samples = Enumerable.Range(0x40, 32).Select(v => (v << 16) | (v << 8) | v).ToArray();

        int colour = Backdrop.Colour(samples);

        Assert.InRange(colour & 0xFF, 0x4E, 0x51);
        Assert.Equal(colour & 0xFF, (colour >> 8) & 0xFF);
        Assert.Equal(colour & 0xFF, (colour >> 16) & 0xFF);
    }

    [Fact]
    public void Colour_OfNoSamples_IsBlack()
    {
        Assert.Equal(Black, Backdrop.Colour(ReadOnlySpan<int>.Empty));
    }

    [Fact]
    public void Sideways_StopsAtWhateverIsThere()
    {
        // A line at rows 10..19; an icon 50 pixels to its right.
        var picture = new Picture(300, 40, White).Paint(150, 12, 8, 6, Blue);

        var run = Backdrop.Sideways(picture, 100, 1, 10, 19, White, 200);

        Assert.Equal(new BackdropRun(50, true), run);
    }

    [Fact]
    public void Sideways_SeesWhatTheMiddleRowMisses()
    {
        // Only the bottom rows of the band are taken.
        var picture = new Picture(300, 40, White).Paint(130, 18, 40, 2, Black);

        var run = Backdrop.Sideways(picture, 100, 1, 10, 19, White, 200);

        Assert.Equal(new BackdropRun(30, true), run);
    }

    [Fact]
    public void Sideways_GoesAsFarAsAsked_WhenNothingIsInTheWay()
    {
        var picture = new Picture(300, 40, White);

        Assert.Equal(new BackdropRun(120, false), Backdrop.Sideways(picture, 100, 1, 10, 19, White, 120));
    }

    [Fact]
    public void Sideways_EndsAtTheEdgeOfThePicture()
    {
        var picture = new Picture(300, 40, White);

        Assert.Equal(new BackdropRun(200, true), Backdrop.Sideways(picture, 100, 1, 10, 19, White, 500));
        Assert.Equal(new BackdropRun(100, true), Backdrop.Sideways(picture, 99, -1, 10, 19, White, 500));
    }

    [Fact]
    public void Sideways_ToleratesNoise_ButNotAnotherShade()
    {
        // A white card on a page a shade darker: the card ends 60 pixels on.
        var picture = new Picture(300, 40, White).Paint(120, 0, 10, 40, 0xFDFFFC).Paint(160, 0, 140, 40, 0xF4F5F7);

        Assert.Equal(new BackdropRun(60, true), Backdrop.Sideways(picture, 100, 1, 10, 19, White, 200));
    }

    [Fact]
    public void Sideways_KeepsToThePicture_WhenTheBandReachesPastIt()
    {
        var picture = new Picture(300, 40, White);

        Assert.Equal(new BackdropRun(50, false), Backdrop.Sideways(picture, 100, 1, -5, 60, White, 50));
    }

    [Fact]
    public void UpOrDown_StopsAtARuleAcrossTheColumns()
    {
        // A one-pixel rule 12 rows under the line, and the top of the picture above it.
        var picture = new Picture(300, 100, White).Paint(0, 62, 300, 1, 0xDDDDDD);

        Assert.Equal(new BackdropRun(12, true), Backdrop.UpOrDown(picture, 50, 1, 100, 180, White, 40));
        Assert.Equal(new BackdropRun(30, false), Backdrop.UpOrDown(picture, 39, -1, 100, 180, White, 30));
        Assert.Equal(new BackdropRun(40, true), Backdrop.UpOrDown(picture, 39, -1, 100, 180, White, 90));
    }

    [Fact]
    public void UpOrDown_SeesASingleColumnInTheWay()
    {
        var picture = new Picture(300, 100, White).Paint(140, 55, 1, 10, Black);

        Assert.Equal(new BackdropRun(5, true), Backdrop.UpOrDown(picture, 50, 1, 100, 180, White, 40));
    }

    [Theory]
    [InlineData(24, 24, true)]     // the label of a button
    [InlineData(24, 27, true)]     // an OCR box is never exact
    [InlineData(300, 320, true)]   // a title in the middle of a wide page
    [InlineData(16, 240, false)]   // flush left in its column
    [InlineData(40, 52, false)]
    public void Centred_WhenTheBackgroundEndsEquallyFarOnBothSides(int left, int right, bool expected)
    {
        Assert.Equal(expected, Backdrop.Centred(new BackdropRun(left, true), new BackdropRun(right, true)));
    }

    [Fact]
    public void Centred_IsNotClaimed_WhenASideWasNeverSeenToEnd()
    {
        Assert.False(Backdrop.Centred(new BackdropRun(200, false), new BackdropRun(200, false)));
        Assert.False(Backdrop.Centred(new BackdropRun(200, true), new BackdropRun(200, false)));
    }
}
