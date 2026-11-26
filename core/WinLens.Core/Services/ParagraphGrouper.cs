using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WinLens.Services;

/// <summary>How the lines of a paragraph line up with each other.</summary>
public enum ParagraphAlignment { Left, Center, Right }

/// <summary>
/// One or more OCR lines that read as a single run of text and are translated together.
/// <see cref="Text"/> is the lines joined the way a reader would, <see cref="Lines"/> keeps
/// where each one sat, top to bottom.
/// </summary>
public sealed record OcrParagraph(
    string Text, IReadOnlyList<OcrLine> Lines, OcrRect Bounds, ParagraphAlignment Alignment)
{
    public string? Language => Lines[0].Language;
}

/// <summary>
/// Finds the lines that are one sentence wrapped over several lines, so the translator
/// sees the whole sentence instead of one fragment per line.
/// <para>
/// Windows OCR reports lines, not paragraphs, so this has to be inferred, and a wrong
/// guess is not symmetric. Leaving a paragraph split only costs context (the behaviour
/// before this existed). Joining lines that were separate items (a menu, a file list,
/// two chat messages) makes the translator read them as one sentence, which is worse.
/// Every rule here is therefore written to refuse when in doubt.
/// </para>
/// <para>
/// Two lines are joined when the geometry says "same block of text" (one directly under
/// the other, same font size, a line pitch a paragraph would have, edges or centres
/// aligned), when the upper line was actually full (the first word of the lower line
/// would not have fitted after it, which is what makes a renderer wrap), and when the
/// text itself reads on: the upper line does not end a sentence and the lower one does
/// not start a list item.
/// </para>
/// </summary>
public static class ParagraphGrouper
{
    // Share of the font size (em) that the ink of a Latin, Cyrillic or Greek line covers:
    // the x-height, plus the part above it when the line has a capital, digit or ascender,
    // plus the part below the baseline when it has a descender. OCR boxes hug the ink, so
    // "no one saw us" and "Typography" come back with very different heights at the same
    // font size; dividing by the share recovers a size the two agree on.
    private const double XHeight = 0.52;
    private const double AscenderRise = 0.20;
    private const double DescenderDrop = 0.21;
    // Scripts without that structure (CJK, Arabic, Thai, ...) fill a steady share of the em.
    private const double FullInk = 0.92;

    // Two lines are the same font when their sizes are within this ratio.
    private const double SameFontRatio = 0.8;

    // Baseline-to-baseline distance of consecutive lines of one paragraph, in em. Tighter
    // than MinPitch the boxes overlap; looser than MaxPitch they are separate blocks.
    private const double MinPitch = 0.7;
    private const double MaxPitch = 2.3;
    // A line break inside a paragraph is never much taller than the ones next to it; the
    // taller gap is the paragraph break.
    private const double PitchJitter = 1.25;
    // Lines whose baselines are closer than this (in em) are on the same row.
    private const double SameRow = 0.5;

    // Edges or centres closer than this (in em) count as aligned.
    private const double AlignTolerance = 0.4;
    // How far a first-line indent, or the text after a bullet, may sit from the other lines.
    private const double MaxIndent = 3.5;

    // Headline-sized text (this many times the usual size on screen) set tight is wrapped
    // display text: a thumbnail caption, a banner, a meme. It needs no further proof.
    private const double DisplaySize = 1.5;
    private const double DisplayPitch = 1.35;

    // Lines starting with a capital are joined only in running text, which is set tighter
    // than the rows of a list or a menu.
    private const double ProsePitch = 1.65;

    private const double SpaceWidth = 0.3;   // em
    private const double FirstWordSlack = 1.2;

    /// <summary>
    /// Groups <paramref name="lines"/> into paragraphs. Every line ends up in exactly one
    /// paragraph; a line that joins nothing is a paragraph of its own. The result is in
    /// reading order (top to bottom, then left to right).
    /// </summary>
    public static List<OcrParagraph> Group(IReadOnlyList<OcrLine> lines)
    {
        var nodes = lines
            .Select(l => new Node(l))
            .OrderBy(n => n.Top).ThenBy(n => n.Left)
            .ToArray();

        LinkNeighbours(nodes);

        double usualEm = Median(nodes.Select(n => n.Em));
        foreach (var n in nodes)
        {
            if (n.Below is { } below)
                n.Join = Judge(n, below, nodes, usualEm);
        }
        ConfirmWeakJoins(nodes);

        var result = new List<OcrParagraph>(nodes.Length);
        foreach (var head in nodes)
        {
            if (head.Above is { Joins: true })
                continue; // already inside the paragraph that starts higher up

            var members = new List<Node> { head };
            for (var n = head; n.Joins; n = n.Below!)
                members.Add(n.Below!);
            result.Add(Build(members));
        }
        return result;
    }

    // ------------------------------------------------------------------ geometry

    private enum Edge { None, Left, Center, Right }

    private enum Verdict { Apart, Weak, Strong }

    private sealed class Node
    {
        public readonly OcrLine Line;
        public readonly double Left, Top, Right, Bottom, Width, Height, CenterX;
        public readonly double Em, Baseline;
        public readonly TextTraits Text;

        /// <summary>The one line directly under this one, when that is unambiguous.</summary>
        public Node? Below;
        public Node? Above;
        public Edge Edge;          // how this line and Below line up
        public Verdict Join;       // whether Below continues this line
        public bool Joins => Join == Verdict.Strong;

        public Node(OcrLine line)
        {
            Line = line;
            var r = line.Rect;
            Left = r.X; Top = r.Y; Width = r.Width; Height = r.Height;
            Right = r.X + r.Width; Bottom = r.Y + r.Height; CenterX = r.X + r.Width / 2;
            Text = TextTraits.Of(line.Text);

            // A line of digits and symbols is shaped like Latin text.
            bool latinLike = Text.Cased || Text.Family == Family.None;
            double ink = latinLike
                ? XHeight + (Text.HasAscender ? AscenderRise : 0) + (Text.HasDescender ? DescenderDrop : 0)
                : FullInk;
            Em = Height / ink;
            Baseline = Bottom - (latinLike && Text.HasDescender ? DescenderDrop * Em : 0);
        }
    }

    /// <summary>
    /// Sets <see cref="Node.Below"/>/<see cref="Node.Above"/> where one line sits directly
    /// under another and nothing else competes for the place (two columns under one
    /// heading, or a line with two candidates above it, stay unlinked).
    /// </summary>
    private static void LinkNeighbours(Node[] nodes)
    {
        foreach (var a in nodes)
        {
            if (Nearest(a, nodes, down: true) is not { } b) continue;
            if (!ReferenceEquals(Nearest(b, nodes, down: false), a)) continue;
            var edge = Alignment(a, b);
            if (edge == Edge.None) continue;
            a.Below = b;
            a.Edge = edge;
            b.Above = a;
        }
    }

    /// <summary>
    /// The closest same-font line straight above or below <paramref name="a"/> at a
    /// paragraph's pitch, or null when there is none or two are equally close.
    /// </summary>
    private static Node? Nearest(Node a, Node[] nodes, bool down)
    {
        Node? best = null;
        double bestPitch = double.MaxValue, runnerUp = double.MaxValue;
        foreach (var b in nodes)
        {
            if (ReferenceEquals(a, b)) continue;
            double pitch = down ? Pitch(a, b) : Pitch(b, a);
            if (pitch < MinPitch || pitch > MaxPitch) continue;
            if (!SameFont(a, b) || !Stacked(a, b)) continue;

            if (pitch < bestPitch)
            {
                runnerUp = bestPitch;
                best = b;
                bestPitch = pitch;
            }
            else if (pitch < runnerUp)
            {
                runnerUp = pitch;
            }
        }
        // A second candidate on the same row (two columns side by side) leaves no single
        // neighbour; one a full line further away is simply the line after next.
        return runnerUp - bestPitch < SameRow ? null : best;
    }

    /// <summary>Baseline-to-baseline distance in em of the two lines' font.</summary>
    private static double Pitch(Node upper, Node lower) =>
        (lower.Baseline - upper.Baseline) / ((upper.Em + lower.Em) / 2);

    private static bool SameFont(Node a, Node b) =>
        Ratio(a.Em, b.Em) >= SameFontRatio || Ratio(a.Height, b.Height) >= SameFontRatio;

    private static double Ratio(double x, double y) =>
        x <= 0 || y <= 0 ? 0 : Math.Min(x, y) / Math.Max(x, y);

    /// <summary>True when the two lines share at least half of the narrower one's width.</summary>
    private static bool Stacked(Node a, Node b)
    {
        double overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        return overlap >= 0.5 * Math.Min(a.Width, b.Width);
    }

    private static Edge Alignment(Node a, Node b)
    {
        double tol = Tolerance(a, b);
        double left = Math.Abs(a.Left - b.Left), center = Math.Abs(a.CenterX - b.CenterX);
        bool rightEdges = Math.Abs(a.Right - b.Right) <= tol;
        // Two centred lines of similar width also start close together; they are centred
        // when their middles agree clearly better than their left edges do.
        if (center <= tol && center + tol / 4 < left) return Edge.Center;
        if (left <= tol) return Edge.Left;
        // Right-to-left scripts line up on the right; elsewhere a matching right edge is
        // more often justified text whose first line is indented.
        if (rightEdges && a.Text.Family is Family.Arabic or Family.Hebrew) return Edge.Right;

        double em = Math.Max(a.Em, b.Em);
        // First line of a paragraph set in from the rest.
        if (a.Left > b.Left && a.Left - b.Left <= MaxIndent * em) return Edge.Left;
        // "• text that wraps" / "  onto the next line": the bullet pushes the first line out.
        if (b.Left > a.Left && b.Left - a.Left <= MaxIndent * em && a.Text.StartsListItem) return Edge.Left;
        return rightEdges ? Edge.Right : Edge.None;
    }

    private static double Tolerance(Node a, Node b) =>
        Math.Max(3, AlignTolerance * Math.Max(a.Em, b.Em));

    // ------------------------------------------------------------------ the decision

    private static Verdict Judge(Node a, Node b, Node[] nodes, double usualEm)
    {
        if (a.Text.Family != b.Text.Family || a.Text.Family == Family.None)
            return Verdict.Apart;

        // An indented line starts a paragraph; it cannot also continue one.
        bool indented = a.Left - b.Left > Tolerance(a, b) && a.Edge == Edge.Left;
        if (indented && a.Above is { Join: not Verdict.Apart })
            return Verdict.Apart;

        // A paragraph keeps one alignment and an even pitch from line to line.
        if (a.Above is { Join: not Verdict.Apart } up)
        {
            if (up.Edge != a.Edge) return Verdict.Apart;
            if (Pitch(a, b) > Pitch(up, a) * PitchJitter) return Verdict.Apart;
        }
        if (b.Below is { } next && b.Edge == a.Edge && Pitch(a, b) > Pitch(b, next) * PitchJitter)
            return Verdict.Apart;

        if (b.Text.StartsListItem || a.Text.EndsHard)
            return Verdict.Apart;
        // "approx." / "e.g." can end a line mid-sentence; a capital after the stop cannot.
        if (a.Text.EndsSentence && !b.Text.StartsLower)
            return Verdict.Apart;

        double pitch = Pitch(a, b);
        double em = (a.Em + b.Em) / 2;
        double room = Room(a, b, nodes);
        double word = b.Text.FirstWordShare * b.Width;
        bool spaced = !a.Text.Unspaced;
        double lenient = word * FirstWordSlack + (spaced ? SpaceWidth * em : em);
        double strict = word + (spaced ? SpaceWidth * em : em);
        if (room >= lenient)
            return Verdict.Apart; // the next word would have fitted: the break was on purpose

        // Headline-sized text set tight.
        if (a.Em >= DisplaySize * usualEm && b.Em >= DisplaySize * usualEm && pitch <= DisplayPitch)
            return Verdict.Strong;

        if (a.Text.Cased)
        {
            if (a.Text.Words >= 2 && (b.Text.StartsLower || a.Text.EndsOpen))
                return Verdict.Strong;
            // Capitalised start (a name, a German noun, a title in title case): only in
            // running text, and only together with a neighbouring line (ConfirmWeakJoins).
            if (a.Text.Words >= 4 && pitch <= ProsePitch && room < strict)
                return Verdict.Weak;
            return Verdict.Apart;
        }

        // No letter case to go by.
        if (a.Text.EndsOpen && a.Text.Length >= 4)
            return Verdict.Strong;
        bool longEnough = a.Text.Unspaced ? a.Text.Length >= 6 : a.Text.Words >= 3;
        return longEnough && room < strict ? Verdict.Weak : Verdict.Apart;
    }

    /// <summary>
    /// How much empty space the upper line left before the edge its block wraps at. The
    /// edge is taken from every line on screen that starts where these do in the same
    /// font, so a short line is measured against the real column and not just against
    /// the line under it.
    /// </summary>
    private static double Room(Node a, Node b, Node[] nodes)
    {
        double tol = Tolerance(a, b);
        switch (a.Edge)
        {
            case Edge.Right:
            {
                double edge = Math.Min(a.Left, b.Left);
                foreach (var n in nodes)
                    if (SameFont(n, b) && Math.Abs(n.Right - b.Right) <= tol)
                        edge = Math.Min(edge, n.Left);
                return a.Left - edge;
            }
            case Edge.Center:
            {
                double widest = Math.Max(a.Width, b.Width);
                foreach (var n in nodes)
                    if (SameFont(n, b) && Math.Abs(n.CenterX - b.CenterX) <= tol)
                        widest = Math.Max(widest, n.Width);
                return widest - a.Width;
            }
            default:
            {
                double edge = Math.Max(a.Right, b.Right);
                foreach (var n in nodes)
                    if (SameFont(n, b) && (Math.Abs(n.Left - b.Left) <= tol || Math.Abs(n.Left - a.Left) <= tol))
                        edge = Math.Max(edge, n.Right);
                return edge - a.Right;
            }
        }
    }

    /// <summary>
    /// A weak join stands on geometry alone, and one full line followed by another line is
    /// also what the longest item of a list looks like. It is accepted when the two lines
    /// are a block of their own, or when a neighbouring line break agrees: in a paragraph
    /// of three lines or more, every line but the last is full.
    /// </summary>
    private static void ConfirmWeakJoins(Node[] nodes)
    {
        var confirmed = new List<Node>();
        foreach (var a in nodes)
        {
            if (a.Join != Verdict.Weak) continue;
            var b = a.Below!;
            double pitch = Pitch(a, b);
            bool alone = (a.Above == null || Pitch(a.Above, a) > pitch * PitchJitter)
                      && (b.Below == null || Pitch(b, b.Below) > pitch * PitchJitter);
            bool neighbourAgrees = a.Above is { Join: not Verdict.Apart } || b.Join != Verdict.Apart;
            if (alone || neighbourAgrees)
                confirmed.Add(a);
        }
        foreach (var n in nodes)
        {
            if (n.Join == Verdict.Weak)
                n.Join = confirmed.Contains(n) ? Verdict.Strong : Verdict.Apart;
        }
    }

    private static OcrParagraph Build(List<Node> members)
    {
        var lines = members.Select(m => m.Line).ToArray();
        double left = members.Min(m => m.Left), top = members.Min(m => m.Top);
        double right = members.Max(m => m.Right), bottom = members.Max(m => m.Bottom);
        var alignment = members.Count == 1 ? ParagraphAlignment.Left : members[0].Edge switch
        {
            Edge.Center => ParagraphAlignment.Center,
            Edge.Right => ParagraphAlignment.Right,
            _ => ParagraphAlignment.Left,
        };
        return new OcrParagraph(JoinLines(lines), lines,
            new OcrRect(left, top, right - left, bottom - top), alignment);
    }

    /// <summary>
    /// Joins wrapped lines into the sentence they came from: a space between words, nothing
    /// between Chinese or Japanese characters, and a word hyphenated at the line end
    /// ("trans-" / "lation") put back together.
    /// </summary>
    internal static string JoinLines(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 1) return lines[0].Text;

        var sb = new StringBuilder(lines[0].Text.TrimEnd());
        for (int i = 1; i < lines.Count; i++)
        {
            var next = lines[i].Text.Trim();
            if (next.Length == 0) continue;

            bool hyphenated = sb.Length >= 2 && sb[^1] is ('-' or '\u2010')
                && char.IsLetter(sb[^2]) && char.IsLower(next[0]);
            if (hyphenated) sb.Length--;
            else if (sb.Length > 0) sb.Append(' ');
            sb.Append(next);
        }
        return OcrPostProcessor.JoinCjk(sb.ToString());
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    // ------------------------------------------------------------------ text cues

    private enum Family { None, Latin, Cyrillic, Greek, Cjk, Hangul, Arabic, Hebrew, Devanagari, Thai }

    /// <summary>What the wording of one line says about where it starts and ends.</summary>
    private readonly record struct TextTraits(
        Family Family,
        int Words,
        int Length,
        bool HasAscender,
        bool HasDescender,
        bool StartsLower,
        bool StartsListItem,
        bool EndsSentence,
        bool EndsHard,
        bool EndsOpen,
        double FirstWordShare)
    {
        /// <summary>Scripts with capitals and x-height (Latin, Cyrillic, Greek).</summary>
        public bool Cased => Family is Family.Latin or Family.Cyrillic or Family.Greek;

        /// <summary>Scripts written without spaces, where a line can break after any character.</summary>
        public bool Unspaced => Family is Family.Cjk or Family.Thai;

        private const string Bullets = "•·∙‣⁃◦▪▫■□●○◆◇►▶▸➤→*+>#@☐☑✓✔✗✘";
        private const string Dashes = "-–—";
        private const string SentenceEnds = ".!?…。！？؟।";
        // A colon or semicolon introduces or closes something; the rest end code and paths,
        // never a line of prose.
        private const string HardEnds = ":;：；{}[]=<>|\\/_~^*+@#";
        private const string OpenEnds = ",，、-–—&(（«“‘";
        private const string Closers = "\"'”’»)）]】」』";
        private const string Openers = "\"'“‘«(（[【「『¿¡";

        public static TextTraits Of(string raw)
        {
            var text = raw.Trim();
            if (text.Length == 0) return default;

            var counts = new int[Enum.GetValues<Family>().Length];
            bool asc = false, desc = false;
            foreach (char c in text)
            {
                if (char.IsDigit(c)) asc = true;
                else if ("()[]{}|/\\$@".Contains(c)) { asc = true; desc = true; }
                else if (",;".Contains(c)) desc = true;
                if (!char.IsLetter(c)) continue;

                counts[(int)FamilyOf(c)]++;
                if (char.IsUpper(c) || "bdfhijkltßбёйїβδζθλξ".Contains(c)) asc = true;
                // Accented lowercase reaches up to about the capitals.
                else if (c > 0x7F && c < 0x250) asc = true;
                if ("gjpqyQçşţруфцщдγζημξρφχψς".Contains(c)) desc = true;
            }

            var family = Family.None;
            int most = 0;
            for (int i = 1; i < counts.Length; i++)
                if (counts[i] > most) { most = counts[i]; family = (Family)i; }
            // Kana or Han anywhere means the line is set in a CJK face, whatever else it mixes in.
            if (counts[(int)Family.Cjk] > 0) family = Family.Cjk;

            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int wordCount = words.Count(w => w.Any(char.IsLetterOrDigit));

            int first = 0;
            while (first < text.Length && Openers.Contains(text[first])) first++;
            bool startsLower = first < text.Length && char.IsLower(text[first]);

            int last = text.Length - 1;
            while (last > 0 && Closers.Contains(text[last])) last--;
            char end = text[last];

            return new TextTraits(
                family,
                wordCount,
                text.Length,
                asc,
                desc,
                startsLower,
                IsListItem(text, words[0]),
                SentenceEnds.Contains(end),
                HardEnds.Contains(end),
                OpenEnds.Contains(end),
                FirstWordShareOf(text, family is Family.Cjk or Family.Thai));
        }

        private static Family FamilyOf(char c) => OcrPostProcessor.GlyphOf(c) switch
        {
            OcrPostProcessor.Glyph.Cyrillic => Family.Cyrillic,
            OcrPostProcessor.Glyph.Greek => Family.Greek,
            OcrPostProcessor.Glyph.Arabic => Family.Arabic,
            OcrPostProcessor.Glyph.Hebrew => Family.Hebrew,
            OcrPostProcessor.Glyph.Devanagari => Family.Devanagari,
            OcrPostProcessor.Glyph.Thai => Family.Thai,
            OcrPostProcessor.Glyph.Han or OcrPostProcessor.Glyph.Kana => Family.Cjk,
            OcrPostProcessor.Glyph.Hangul => Family.Hangul,
            _ => Family.Latin,
        };

        /// <summary>"• item", "- item", "3. item", "a) item": the start of a list entry.</summary>
        private static bool IsListItem(string text, string firstWord)
        {
            char c = text[0];
            if (Bullets.Contains(c)) return true;
            if (Dashes.Contains(c)) return text.Length > 1 && char.IsWhiteSpace(text[1]);
            if (firstWord.Length is < 2 or > 4) return false;
            if (firstWord[^1] is not ('.' or ')')) return false;
            var label = firstWord.AsSpan(0, firstWord.Length - 1);
            if (label.Length == 1 && char.IsLetter(label[0])) return true;
            foreach (char d in label)
                if (!char.IsDigit(d)) return false;
            return true;
        }

        /// <summary>
        /// The share of the line's width its first unbreakable piece takes: the first word,
        /// or the first character where the script has no spaces. Narrow and wide glyphs
        /// are weighted so "illicit" and "mammoth" don't count the same.
        /// </summary>
        private static double FirstWordShareOf(string text, bool unspaced)
        {
            double total = 0, first = 0;
            bool inFirst = true;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inFirst && i > 0)
                {
                    if (char.IsWhiteSpace(c)) inFirst = false;
                    else if (unspaced && (IsWide(c) || IsWide(text[i - 1])) && !Closers.Contains(c) && !SentenceEnds.Contains(c) && !OpenEnds.Contains(c))
                        inFirst = false;
                }
                double w = GlyphWidth(c);
                total += w;
                if (inFirst) first += w;
            }
            return total <= 0 ? 1 : first / total;
        }

        private static bool IsWide(char c) =>
            OcrPostProcessor.GlyphOf(c) is OcrPostProcessor.Glyph.Han or OcrPostProcessor.Glyph.Kana
                or OcrPostProcessor.Glyph.Hangul or OcrPostProcessor.Glyph.Thai
            || c >= 0x3000 && c <= 0x303F || c >= 0xFF00 && c <= 0xFFEF;

        private static double GlyphWidth(char c)
        {
            if (char.IsWhiteSpace(c)) return 0.5;
            if (OcrPostProcessor.GlyphOf(c) == OcrPostProcessor.Glyph.Thai) return 1.0;
            if (IsWide(c)) return 2.0;
            if ("iljtfIr.,;:'|!()[]".Contains(c)) return 0.6;
            if ("mwMW@%".Contains(c)) return 1.6;
            if (char.IsUpper(c)) return 1.3;
            return 1.0;
        }
    }
}
