using System.Text;
using WinLens.Services;

namespace WinLens.Core.Tests;

/// <summary>
/// Lays text out the way a screen would and reports it the way Windows OCR does: one
/// entry per line, each with a box that hugs the ink (a line without descenders is
/// shorter than one with them). Lets the paragraph tests describe a screen in words
/// ("a 400 px column of 16 px text") instead of hand-placed rectangles.
/// </summary>
internal sealed class ScreenText
{
    private readonly List<OcrLine> _lines = new();

    public IReadOnlyList<OcrLine> Lines => _lines;

    /// <summary>Y just under everything added so far (the bottom of the last line box).</summary>
    public double Bottom { get; private set; }

    /// <summary>Wraps <paramref name="text"/> into a column and adds its lines.</summary>
    /// <returns>The wrapped lines, so a test can check what the layout produced.</returns>
    public IReadOnlyList<string> Paragraph(
        string text, double x, double y, double width, double em = 16, double lineHeight = 1.4,
        string align = "left", double indent = 0, string? lang = "en-US")
    {
        var wrapped = Wrap(text, width, em, indent);
        for (int i = 0; i < wrapped.Count; i++)
        {
            double w = WidthOf(wrapped[i], em);
            double lineX = align switch
            {
                "center" => x + (width - w) / 2,
                "right" => x + width - w,
                _ => x + (i == 0 ? indent : 0),
            };
            Add(wrapped[i], lineX, y + i * lineHeight * em, em, lang);
        }
        return wrapped;
    }

    /// <summary>Adds separate one-line items under each other (a menu, a list, chat messages).</summary>
    public void Rows(IEnumerable<string> items, double x, double y, double em = 16, double pitch = 1.8,
        string? lang = "en-US")
    {
        int i = 0;
        foreach (var item in items)
            Add(item, x, y + i++ * pitch * em, em, lang);
    }

    /// <summary>Adds one line whose text box starts at (<paramref name="x"/>, <paramref name="lineTop"/>).</summary>
    public void Add(string text, double x, double lineTop, double em = 16, string? lang = "en-US")
    {
        // The line box is 1.2 em tall with the baseline 0.95 em down; the ink hangs off it.
        double baseline = lineTop + 0.95 * em;
        var (rise, drop) = Ink(text);
        double top = baseline - rise * em, bottom = baseline + drop * em;
        _lines.Add(new OcrLine(text, new OcrRect(x, top, WidthOf(text, em), bottom - top), lang));
        Bottom = Math.Max(Bottom, lineTop + 1.2 * em);
    }

    private static List<string> Wrap(string text, double width, double em, double indent)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        if (IsCjk(text))
        {
            foreach (char c in text)
            {
                double limit = width - (lines.Count == 0 ? indent : 0);
                if (line.Length > 0 && WidthOf(line.ToString() + c, em) > limit && !"，。、！？」』）".Contains(c))
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }
                line.Append(c);
            }
        }
        else
        {
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                double limit = width - (lines.Count == 0 ? indent : 0);
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && WidthOf(candidate, em) > limit)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                    line.Append(word);
                }
                else
                {
                    line.Clear();
                    line.Append(candidate);
                }
            }
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }

    private static bool IsCjk(string s) => s.Any(c => c >= 0x3040 && c <= 0x9FFF);

    /// <summary>How far the ink of a line reaches above and below the baseline, in em.</summary>
    private static (double Rise, double Drop) Ink(string text)
    {
        if (IsCjk(text)) return (0.86, 0.08);
        bool tall = text.Any(c => char.IsUpper(c) || char.IsDigit(c) || "bdfhijklt()[]/?!".Contains(c));
        bool hangs = text.Any(c => "gjpqy,;()[]".Contains(c));
        return (tall ? 0.72 : 0.52, hangs ? 0.21 : 0.0);
    }

    /// <summary>Advance widths close to Segoe UI's, in em.</summary>
    public static double WidthOf(string text, double em)
    {
        double w = 0;
        foreach (char c in text)
        {
            w += c switch
            {
                ' ' => 0.27,
                >= (char)0x2E80 => 1.0,
                'i' or 'j' or 'l' or 'I' or '.' or ',' or ':' or ';' or '\'' or '!' or '|' => 0.24,
                'f' or 't' or 'r' or '(' or ')' or '-' => 0.34,
                'm' or 'M' or 'W' => 0.88,
                'w' => 0.72,
                >= 'A' and <= 'Z' => 0.62,
                >= '0' and <= '9' => 0.54,
                _ => 0.53,
            };
        }
        return w * em;
    }
}
