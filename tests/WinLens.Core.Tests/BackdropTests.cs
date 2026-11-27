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
    public void Colour_OfTwoTones_IsOneOfThem()
    {
        // Half on a card, half on the page beside it: never a colour that is not there.
        var samples = Enumerable.Repeat(White, 20).Concat(Enumerable.Repeat(0xE0E4EC, 20)).ToArray();

        Assert.Contains(Backdrop.Colour(samples), new[] { White, 0xE0E4EC });
    }

    [Fact]
    public void Colour_OfManySamples_IsStillTheCommonest()
    {
        var samples = Enumerable.Repeat(Blue, 500).Concat(Enumerable.Range(0, 400)).ToArray();

        Assert.Equal(Blue, Backdrop.Colour(samples));
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
    public void Sideways_PassesOverTheEndOfTheTextItself()
    {
        // The last letter reaches two pixels past its box, within the rows of the text.
        var picture = new Picture(300, 40, White).Paint(100, 12, 2, 6, Black).Paint(180, 0, 1, 40, Black);

        var run = Backdrop.Sideways(picture, 100, 1, 10, 19, White, 200, lead: 3);

        Assert.Equal(new BackdropRun(80, true), run);
    }

    [Fact]
    public void Sideways_StopsAtARuleRightNextToTheText()
    {
        // A rule from top to bottom two pixels on, plain background beyond it.
        var picture = new Picture(300, 40, White).Paint(102, 0, 1, 40, 0xDDDDDD);

        var run = Backdrop.Sideways(picture, 100, 1, 10, 19, White, 200, lead: 3);

        Assert.Equal(new BackdropRun(2, true), run);
    }

    [Fact]
    public void Sideways_StopsAtARuleThatTouchesTheText()
    {
        var picture = new Picture(300, 40, White).Paint(100, 0, 1, 40, 0xDDDDDD);

        Assert.Equal(new BackdropRun(0, true), Backdrop.Sideways(picture, 100, 1, 10, 19, White, 200, lead: 3));
    }

    [Fact]
    public void Sideways_PassesOverNothing_OnceTheBackgroundHasBegun()
    {
        // The end of a letter, a clear column, then something else: still within the lead.
        var picture = new Picture(300, 40, White).Paint(100, 12, 1, 6, Black).Paint(102, 12, 1, 6, Black);

        var run = Backdrop.Sideways(picture, 100, 1, 10, 19, White, 200, lead: 4);

        Assert.Equal(new BackdropRun(2, true), run);
    }

    [Fact]
    public void Sideways_TellsTextFromARule_OnlyWhereTheBandHasItsMargin()
    {
        // The band starts above the picture: its first row there is a row of the text.
        var picture = new Picture(300, 40, White).Paint(100, 0, 2, 6, Black);

        var run = Backdrop.Sideways(picture, 100, 1, -2, 7, White, 50, lead: 3);

        Assert.Equal(new BackdropRun(50, false), run);
    }

    [Fact]
    public void UpOrDown_PassesOverTheFootOfALetter_AndAnUnderline()
    {
        // Under the text only, not as far as the columns of the fill on either side.
        var picture = new Picture(300, 100, White).Paint(120, 50, 4, 2, Black).Paint(110, 52, 80, 1, Black);

        var run = Backdrop.UpOrDown(picture, 50, 1, 100, 199, White, 30, lead: 3);

        Assert.Equal(new BackdropRun(30, false), run);
    }

    [Fact]
    public void UpOrDown_StopsAtARuleRightUnderTheText()
    {
        var picture = new Picture(300, 100, White).Paint(0, 51, 300, 1, 0xDDDDDD);

        Assert.Equal(new BackdropRun(1, true), Backdrop.UpOrDown(picture, 50, 1, 100, 199, White, 30, lead: 3));
        Assert.Equal(new BackdropRun(1, true), Backdrop.UpOrDown(picture, 52, -1, 100, 199, White, 30, lead: 3));
    }

    [Fact]
    public void UpOrDown_StopsAtARuleThatTouchesTheText()
    {
        var picture = new Picture(300, 100, White).Paint(0, 50, 300, 1, 0xDDDDDD);

        Assert.Equal(new BackdropRun(0, true), Backdrop.UpOrDown(picture, 50, 1, 100, 199, White, 30, lead: 3));
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
