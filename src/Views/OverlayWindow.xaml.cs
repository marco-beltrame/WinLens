using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinLens.Layout;
using WinLens.Models;
using WinLens.Native;
using WinLens.Services;
using WinLens.Services.Translation;
using DBitmap = System.Drawing.Bitmap;
using DRectangle = System.Drawing.Rectangle;
// System.Drawing.Imaging and System.Windows.Media both define PixelFormat; alias the
// GDI one rather than dropping the namespace import, which BitmapData/ImageLockMode need.
using DPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace WinLens.Views;

public partial class OverlayWindow : Window
{
    /// <summary>Where the window sits — always at least the captured area.</summary>
    private readonly DRectangle _windowBounds;

    /// <summary>The part of the screen the screenshot actually covers.</summary>
    private readonly DRectangle _imageBounds;

    private readonly DBitmap _screenshot;
    private readonly List<OcrBlock> _blocks;
    private readonly TranslationService _translator;
    private readonly Action<string> _onTargetLangChanged;
    private readonly IReadOnlyList<string> _recentTargets;
    private double _pixelToDip;

    // What a block's text is drawn in: one box per line of the original.
    private readonly Dictionary<OcrBlock, BlockBoxes> _boxes = new();
    // Text widths at ReferenceFontSize, so a word is only measured once per overlay.
    private readonly Dictionary<string, double> _measured = new(StringComparer.Ordinal);
    // Where a single line may put a translation longer than itself.
    private readonly LineRooms _rooms = new();
    // Scratch space for the colour samples of one line.
    private readonly int[] _samples = new int[512];

    private static readonly FontFamily OverlayFont = new("Segoe UI");
    private static readonly Typeface OverlayTypeface =
        new(OverlayFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private const double ReferenceFontSize = 32;
    private const double MinFontSize = 7;
    // The most rows the translation of a single line is set on.
    private const int MaxRows = 3;
    // Share of a line's ink height the font size may take before descenders are cut off.
    private const double FontPerLineHeight = 0.78;

    private string _currentTarget;
    private bool _suppressLangEvents;

    // Cancels the re-translation for the previous language when the user picks another
    // one before it finished, so late results can't overwrite the newer language.
    private CancellationTokenSource? _retranslateCts;

    /// <param name="imageBounds">
    /// Virtual-screen rectangle the screenshot was taken from. OCR block coordinates are
    /// relative to this rectangle's top-left.
    /// </param>
    /// <param name="windowBounds">
    /// Virtual-screen rectangle the overlay window should cover. Equal to
    /// <paramref name="imageBounds"/> for a full-screen or single-monitor capture; for a
    /// region capture it is the monitor(s) the region touches, so the control bar has room
    /// while the user's OTHER monitors stay uncovered and keep receiving input.
    /// </param>
    public OverlayWindow(
        DBitmap screenshot,
        DRectangle imageBounds,
        DRectangle windowBounds,
        List<OcrBlock> blocks,
        TranslationService translator,
        string initialTarget,
        Action<string> onTargetLangChanged,
        IReadOnlyList<string>? recentTargets = null)
    {
        InitializeComponent();
        _screenshot = screenshot;
        _imageBounds = imageBounds;
        // The window must never be smaller than the image, or part of the translation
        // would be clipped away.
        _windowBounds = DRectangle.Union(windowBounds, imageBounds);
        _blocks = blocks;
        _translator = translator;
        _currentTarget = initialTarget;
        _onTargetLangChanged = onTargetLangChanged;
        _recentTargets = recentTargets ?? Array.Empty<string>();

        // Pinned in PHYSICAL pixels, including across a DPI change when the window lands
        // on a monitor scaled differently from the primary. The px→DIP factor is a guess
        // until Loaded / OnDpiChanged read the DPI WPF really lays the window out with.
        _pixelToDip = WindowPlacement.SystemPixelToDip();
        WindowPlacement.PinToPhysicalBounds(this, _windowBounds);

        BackgroundImage.Source = ToBitmapSource(screenshot);

        PopulateLanguages();

        KeyDown += OnKeyDown;
        // Preview: Space must reach us even when the language box has keyboard focus.
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Deactivated += (_, _) => ShowOriginal(false);
        // OverlayCanvas sits on top and fills the window, so it receives every click that
        // a translation box doesn't handle first — including clicks over the dimmed area.
        OverlayCanvas.MouseLeftButtonDown += (_, _) => Close();
        Closed += (_, _) =>
        {
            _retranslateCts?.Cancel();

            // Release the heavy bits the window held: the decoded full-screen screenshot
            // and the per-line text boxes. Without this the visual tree keeps them alive.
            BackgroundImage.Source = null;
            OverlayCanvas.Children.Clear();
            _boxes.Clear();
            _measured.Clear();
            _screenshot.Dispose();

            // Reclaim only once the window is actually torn down and dereferenced —
            // running the compaction here (still inside Closed) would find everything
            // still rooted.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(MemoryHygiene.Trim));
        };
        Loaded += (_, _) =>
        {
            // WPF lays the window out with one DPI; use that exact value so box
            // coordinates land on the right physical pixels.
            _pixelToDip = WindowPlacement.PixelToDip(this, _pixelToDip);

            // Loaded runs after Show() has returned, outside the caller's try/catch: a
            // failure here would otherwise take the whole tray app down.
            if (!TryLayout()) return;
            BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150))));
            Activate();
            Focus();
        };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (newDpi.DpiScaleX <= 0) return;
        _pixelToDip = 1.0 / newDpi.DpiScaleX;

        // The boxes were laid out for the old DPI; rebuild them so they stay over the
        // original text. Block state (translations) lives on the OcrBlocks, so nothing is lost.
        if (IsLoaded)
            TryLayout();
    }

    private bool TryLayout()
    {
        try
        {
            PlaceBackgroundImage();
            BuildOverlayBoxes();
            return true;
        }
        catch (Exception ex)
        {
            Close();
            MessageBox.Show(ex.Message, "WinLens — error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Offset of the screenshot inside the window, in physical pixels.</summary>
    private int OffsetX => _imageBounds.X - _windowBounds.X;
    private int OffsetY => _imageBounds.Y - _windowBounds.Y;

    private void PlaceBackgroundImage()
    {
        Canvas.SetLeft(BackgroundImage, OffsetX * _pixelToDip);
        Canvas.SetTop(BackgroundImage,  OffsetY * _pixelToDip);
        BackgroundImage.Width  = _imageBounds.Width  * _pixelToDip;
        BackgroundImage.Height = _imageBounds.Height * _pixelToDip;
        BackgroundImage.Stretch = Stretch.Fill;
    }

    private void PopulateLanguages()
    {
        // Populate sets SelectedItem, which fires SelectionChanged. Without suppression
        // a saved target that's missing from LanguageList would make the fallback
        // selection (first item) look like a user choice: it would overwrite the saved
        // TargetLanguage and retranslate everything to the wrong language.
        _suppressLangEvents = true;
        try
        {
            var itemStyle = (Style)FindResource("ModernComboBoxItem");
            LanguagePicker.Populate(LanguageBox, itemStyle, _currentTarget, _recentTargets);
            if (LanguageBox.SelectedIndex < 0)
                LanguageBox.SelectedIndex = 0;
        }
        finally
        {
            _suppressLangEvents = false;
        }
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLangEvents) return;
        if (LanguageBox.SelectedItem is not ComboBoxItem item) return;
        var newTarget = item.Tag as string;
        if (string.IsNullOrEmpty(newTarget) ||
            string.Equals(newTarget, _currentTarget, StringComparison.OrdinalIgnoreCase))
            return;

        _currentTarget = newTarget;

        _retranslateCts?.Cancel();
        _retranslateCts?.Dispose();
        var cts = new CancellationTokenSource();
        _retranslateCts = cts;

        // async void: an exception escaping here would take the whole app down.
        try
        {
            _onTargetLangChanged(newTarget);
            await RetranslateAllAsync(newTarget, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Superseded by a newer language choice, or the overlay closed.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WinLens — error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RetranslateAllAsync(string target, CancellationToken ct)
    {
        var translated = await _translator.TranslateManyAsync(
            _blocks.Select(b => new TranslationRequest(b.OriginalText, b.DetectedLanguage)).ToArray(),
            target, ct);
        // Results arrive on the UI thread; drop them once a newer request has started.
        ct.ThrowIfCancellationRequested();
        _rooms.NewPass();
        using var pixels = new PixelReader(_screenshot);
        var grid = ScreenshotGrid(pixels);
        for (int i = 0; i < _blocks.Count; i++)
        {
            var b = _blocks[i];
            b.TranslatedText = translated[i];
            if (_boxes.TryGetValue(b, out var boxes))
                ApplyText(boxes, translated[i], grid);
        }
    }

    /// <summary>The screenshot under the window's coordinates.</summary>
    private PixelGrid ScreenshotGrid(PixelReader pixels) => new(pixels, 1 / _pixelToDip, OffsetX, OffsetY);

    /// <summary>One line of a block on screen: the fill that hides the original and the text over it.</summary>
    private sealed record LineBox(Border Border, TextBox Text, Rect Slot);

    /// <summary>
    /// The boxes of one block. A single line has one; a paragraph one per original line,
    /// with how wide the text of each may become.
    /// </summary>
    private sealed record BlockBoxes(
        LineBox[] Lines, ParagraphAlignment Alignment, double FontSize, double[] Capacities, LineRoom? Room = null);

    private void BuildOverlayBoxes()
    {
        OverlayCanvas.Children.Clear();
        _boxes.Clear();
        _rooms.Clear();

        // Lock the screenshot once for the whole pass. Sampling used to go through
        // Bitmap.GetPixel, which locks and validates per call — around 200 calls per
        // block, on the UI thread, was a visible stall before the overlay faded in.
        using var pixels = new PixelReader(_screenshot);
        var grid = ScreenshotGrid(pixels);

        // Where every line goes, before any is drawn: a paragraph has to know what else
        // sits on its rows, and a single line what it must not grow into.
        var lineRectsOf = _blocks
            .Select(b => b.LineRects.Count > 0 ? b.LineRects : new[] { b.BoundingRect })
            .ToArray();
        var slotsOf = lineRectsOf.Select(Slots).ToArray();

        var reachOf = new (double Left, double Right)[_blocks.Count][];
        for (int i = 0; i < _blocks.Count; i++)
        {
            var slots = slotsOf[i];
            if (slots.Length == 1)
            {
                _rooms.AddLine(i, ToOcrRect(slots[0]));
                continue;
            }

            var others = slotsOf.Where((_, j) => j != i).SelectMany(o => o).Select(ToOcrRect);
            var reach = reachOf[i] = ParagraphLayout.Reach(
                slots.Select(ToOcrRect).ToArray(), others, slots.Min(r => r.Height) * 0.5);
            for (int k = 0; k < slots.Length; k++)
                _rooms.AddParagraphLine(i, new OcrRect(
                    reach[k].Left, slots[k].Top, Math.Max(0, reach[k].Right - reach[k].Left), slots[k].Height));
        }

        for (int i = 0; i < _blocks.Count; i++)
        {
            var block = _blocks[i];
            var lineRects = lineRectsOf[i];
            var slots = slotsOf[i];
            var menu = BuildContextMenu(block);

            var lines = new LineBox[slots.Length];
            int colour = 0;
            for (int k = 0; k < lines.Length; k++)
            {
                var (bg, fg) = SampleColors(pixels, lineRects[k]);
                colour = (bg.R << 16) | (bg.G << 8) | bg.B;
                // The fill of a paragraph's line stops where the line above ends: it is
                // drawn later, and would cover the bottom of that line's text.
                double roomAbove = k > 0 ? slots[k].Top - slots[k - 1].Bottom : double.MaxValue;
                lines[k] = AddLineBox(slots[k], bg, fg, menu, roomAbove);
            }

            var capacities = slots.Length > 1
                ? Capacities(slots, block.Alignment, reachOf[i])
                : Array.Empty<double>();
            // Text written right to left grows the other way; it keeps the box it has.
            var room = slots.Length == 1 && !IsRightToLeft(block.OriginalText)
                ? _rooms.Find(grid, i, ToOcrRect(slots[0]), PadX(slots[0]), PadY(slots[0]), colour)
                : null;

            var boxes = new BlockBoxes(lines, block.Alignment,
                Math.Max(8, slots.Min(r => r.Height) * FontPerLineHeight), capacities, room);
            _boxes[block] = boxes;
            ApplyText(boxes, block.TranslatedText ?? "", grid);
        }
    }

    /// <summary>
    /// Where each line of a block is drawn, in DIPs inside the window. A single line keeps
    /// its OCR box; the lines of a paragraph become equally tall, evenly spaced rows so
    /// one font size sits the same in each of them.
    /// </summary>
    private Rect[] Slots(IReadOnlyList<Rect> lineRects)
    {
        // Pixel rect → DIPs, shifted by where the screenshot sits in the window.
        var slots = lineRects
            .Select(r => new Rect((r.X + OffsetX) * _pixelToDip, (r.Y + OffsetY) * _pixelToDip,
                                  r.Width * _pixelToDip, r.Height * _pixelToDip))
            .ToArray();
        if (slots.Length < 2) return slots;

        var rows = ParagraphLayout.EvenRows(slots.Select(r => (r.Top, r.Height)).ToArray());
        for (int k = 0; k < slots.Length; k++)
        {
            // Never smaller than the original line, which the fill has to hide.
            double top = Math.Min(rows[k].Top, slots[k].Top);
            double bottom = Math.Max(rows[k].Top + rows[k].Height, slots[k].Bottom);
            // But never into the line above (lines set so tight that their boxes overlap):
            // the two meet halfway.
            if (k > 0 && top < slots[k - 1].Bottom)
            {
                var above = slots[k - 1];
                top = Math.Max(above.Top + 1, Math.Min(bottom - 1, (top + above.Bottom) / 2));
                slots[k - 1] = new Rect(above.X, above.Top, above.Width, top - above.Top);
            }
            slots[k] = new Rect(slots[k].X, top, slots[k].Width, Math.Max(1, bottom - top));
        }
        return slots;
    }

    private static OcrRect ToOcrRect(Rect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>
    /// How wide the text of each line of a paragraph may become: from where the line starts
    /// to as far as it may reach. Two DIPs less, the breathing room single lines get too.
    /// </summary>
    private static double[] Capacities(Rect[] slots, ParagraphAlignment alignment, (double Left, double Right)[] reach)
    {
        double mid = (slots.Min(r => r.Left) + slots.Max(r => r.Right)) / 2;
        return slots.Select((slot, k) => alignment switch
        {
            ParagraphAlignment.Center => 2 * Math.Min(mid - reach[k].Left, reach[k].Right - mid),
            ParagraphAlignment.Right => slot.Right - reach[k].Left,
            _ => reach[k].Right - slot.Left,
        } - 2).ToArray();
    }

    // How far the fill of a line goes past its text, so that the original characters
    // underneath are fully covered (kills leftover edges like a stray "I").
    private static double PadX(Rect slot) => Math.Max(2.0, slot.Height * 0.18);
    private static double PadY(Rect slot) => Math.Max(1.0, slot.Height * 0.10);

    private LineBox AddLineBox(Rect slot, Color bg, Color fg, ContextMenu menu, double roomAbove = double.MaxValue)
    {
        double padX = PadX(slot), padY = PadY(slot);
        double padTop = Math.Max(0, Math.Min(padY, roomAbove));

        var border = new Border
        {
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(padX, padTop, padX, padY),
            Width  = slot.Width + padX * 2,
            Height = slot.Height + padTop + padY,
            Cursor = Cursors.IBeam,
            SnapsToDevicePixels = true,
        };

        var textBox = new TextBox
        {
            Foreground = new SolidColorBrush(fg),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsReadOnly = true,
            IsTabStop = false,
            FontFamily = OverlayFont,
            FontWeight = FontWeights.SemiBold,
            // Font size derived from the OCR rect height. Roughly 70% of line
            // height = visible cap height for Segoe UI.
            FontSize = Math.Max(8, slot.Height * 0.7),
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            CaretBrush = new SolidColorBrush(fg),
            SelectionBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x7B, 0x6C, 0xF6)),
            Cursor = Cursors.IBeam,
            ContextMenu = menu,
            Width  = slot.Width,
            Height = slot.Height,
        };
        border.Child = textBox;
        DataObject.AddCopyingHandler(textBox, OnCopying);

        // Stop bare-canvas click-to-close from firing for clicks on a box.
        border.MouseLeftButtonDown += (_, ev) => ev.Handled = true;

        Canvas.SetLeft(border, slot.X - padX);
        Canvas.SetTop(border,  slot.Y - padTop);
        OverlayCanvas.Children.Add(border);

        return new LineBox(border, textBox, slot);
    }

    /// <summary>
    /// A translation set on several rows has line breaks of ours in it. A copy of a
    /// selection across them gets the text as it was translated.
    /// </summary>
    private static void OnCopying(object sender, DataObjectCopyingEventArgs e)
    {
        if (sender is not TextBox tb || !tb.SelectedText.Contains('\n')) return;
        e.CancelCommand();
        try { Clipboard.SetText(ParagraphLayout.Unwrap(tb.SelectedText)); } catch { /* clipboard busy */ }
    }

    private static bool IsRightToLeft(string text)
    {
        foreach (char c in text)
            if (c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF')
                return true;
        return false;
    }

    /// <summary>
    /// Shows <paramref name="text"/> in a block's boxes. A paragraph is poured over its
    /// lines again; a single line takes what room it has.
    /// </summary>
    private void ApplyText(BlockBoxes boxes, string text, PixelGrid grid)
    {
        if (boxes.Lines.Length > 1)
            ApplyParagraph(boxes, text);
        else
            ApplyLine(boxes, text, grid);
    }

    /// <summary>
    /// Shows the translation of a single line. One longer than the original first takes
    /// the free room beside the line, then goes onto two or three rows where there is
    /// room above or below, and only after that gets a smaller font.
    /// </summary>
    private void ApplyLine(BlockBoxes boxes, string text, PixelGrid grid)
    {
        var (border, tb, slot) = boxes.Lines[0];
        double padX = border.Padding.Left, padY = border.Padding.Bottom;

        var box = ToOcrRect(slot);
        string shown = text;
        double size;
        if (boxes.Room is { } room)
        {
            var place = _rooms.Place(grid, room, text, s => MeasureWidth(s, boxes.FontSize),
                boxes.FontSize * OverlayFont.LineSpacing, MinFontSize / boxes.FontSize, MaxRows);
            box = place.Box;

            if (place.Rows.Count > 1)
            {
                shown = string.Join("\n", place.Rows);
                size = Math.Max(MinFontSize, boxes.FontSize * place.Scale);
            }
            else if (box.Width > slot.Width && place.Scale >= 1)
            {
                size = boxes.FontSize;   // the room beside the line was enough
            }
            else
            {
                size = FitFontSize(text, box.Width, slot.Height);
            }
        }
        else
        {
            size = FitFontSize(text, slot.Width, slot.Height);
        }

        tb.Text = shown;
        tb.FontSize = size;
        tb.Width = box.Width;
        tb.Height = box.Height;
        // Rows start under each other, as wrapped text does; the block they form is what
        // stays in the middle.
        tb.TextAlignment = boxes.Room is { Centred: true } && shown == text ? TextAlignment.Center : TextAlignment.Left;
        border.Width = box.Width + padX * 2;
        border.Height = box.Height + padY * 2;
        Canvas.SetLeft(border, box.X - padX);
        Canvas.SetTop(border, box.Y - padY);
    }

    private void ApplyParagraph(BlockBoxes boxes, string text)
    {
        var lines = boxes.Lines;
        double left = lines.Min(l => l.Slot.Left), right = lines.Max(l => l.Slot.Right);
        var capacities = boxes.Capacities;
        var shares = lines.Select(l => l.Slot.Width).ToArray();

        var fit = ParagraphLayout.Flow(text, capacities, shares,
            s => MeasureWidth(s, boxes.FontSize), MinFontSize / boxes.FontSize);
        double fontSize = Math.Max(MinFontSize, boxes.FontSize * fit.Scale);

        for (int k = 0; k < lines.Length; k++)
        {
            var (border, tb, slot) = lines[k];
            string line = fit.Lines[k];
            double size = fontSize;
            double width = line.Length == 0 ? 0 : MeasureWidth(line, size);
            // Flow added up word widths; the line measured whole can differ by a hair.
            if (width > capacities[k] + 0.5)
            {
                // Only when the text could not be made to fit (one very long word): this
                // line alone shrinks further.
                size = FitFontSize(line, capacities[k] + 2, slot.Height, size);
                width = capacities[k];
            }
            width += 2;

            // The fill hides the original line and backs the new one, whichever reaches further.
            double x0, x1;
            switch (boxes.Alignment)
            {
                case ParagraphAlignment.Center:
                    double mid = (left + right) / 2;
                    double half = Math.Max(width / 2, Math.Max(mid - slot.Left, slot.Right - mid));
                    x0 = mid - half;
                    x1 = mid + half;
                    break;
                case ParagraphAlignment.Right:
                    x1 = slot.Right;
                    x0 = Math.Min(slot.Left, slot.Right - width);
                    break;
                default:
                    x0 = slot.Left;
                    x1 = Math.Max(slot.Right, slot.Left + width);
                    break;
            }

            tb.Text = line;
            tb.FontSize = size;
            tb.Width = x1 - x0;
            tb.TextAlignment = boxes.Alignment switch
            {
                ParagraphAlignment.Center => TextAlignment.Center,
                ParagraphAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
            double padX = border.Padding.Left;
            border.Width = tb.Width + padX * 2;
            Canvas.SetLeft(border, x0 - padX);
        }
    }

    /// <summary>The largest font size, from <paramref name="startSize"/> down, at which the text fits the width.</summary>
    private double FitFontSize(string text, double maxW, double maxH, double? startSize = null)
    {
        double size = startSize ?? Math.Max(8, maxH * FontPerLineHeight);
        if (string.IsNullOrWhiteSpace(text) || double.IsNaN(maxW) || maxW <= 0)
            return size;

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = 0; i < 30 && size > MinFontSize; i++)
        {
            var ft = new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                OverlayTypeface, size, System.Windows.Media.Brushes.Black, pixelsPerDip);
            if (ft.WidthIncludingTrailingWhitespace <= maxW - 2)
                break;
            size -= Math.Max(0.5, size * 0.08);
        }
        return Math.Max(MinFontSize, size);
    }

    /// <summary>
    /// Width of <paramref name="text"/> at <paramref name="fontSize"/>. Measured once at a
    /// fixed size and scaled: WPF's default (ideal) text layout is linear in the font size.
    /// </summary>
    private double MeasureWidth(string text, double fontSize)
    {
        if (!_measured.TryGetValue(text, out double atReference))
        {
            var ft = new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                OverlayTypeface, ReferenceFontSize, System.Windows.Media.Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _measured[text] = atReference = ft.WidthIncludingTrailingWhitespace;
        }
        return atReference * fontSize / ReferenceFontSize;
    }

    /// <summary>
    /// Reads pixels straight out of a locked GDI bitmap. Assumes the 32bpp ARGB, top-down
    /// layout GDI+ gives for the bitmaps <see cref="Native.ScreenCapture"/> creates, which
    /// is the same assumption <see cref="OcrService"/> makes when it converts them.
    /// </summary>
    private sealed class PixelReader : IPixels, IDisposable
    {
        private readonly DBitmap _bitmap;
        private readonly BitmapData _data;

        public int Width { get; }
        public int Height { get; }

        public PixelReader(DBitmap bitmap)
        {
            _bitmap = bitmap;
            Width = bitmap.Width;
            Height = bitmap.Height;
            _data = bitmap.LockBits(
                new DRectangle(0, 0, Width, Height),
                ImageLockMode.ReadOnly,
                DPixelFormat.Format32bppArgb);
        }

        /// <summary>Caller must keep x/y inside the bitmap — the hot path does not re-check.</summary>
        public int Rgb(int x, int y) =>
            Marshal.ReadInt32(_data.Scan0, y * _data.Stride + x * 4) & 0xFFFFFF;

        public void Dispose() => _bitmap.UnlockBits(_data);
    }

    /// <summary>The fill and text colours for a line: what is around it on the screenshot.</summary>
    private (Color bg, Color fg) SampleColors(PixelReader pixels, Rect rect)
    {
        int left   = (int)Math.Floor(rect.X);
        int top    = (int)Math.Floor(rect.Y);
        int right  = (int)Math.Ceiling(rect.X + rect.Width);
        int bottom = (int)Math.Ceiling(rect.Y + rect.Height);

        int pad = Math.Max(2, (bottom - top) / 6);
        int x0 = Math.Clamp(left  - pad, 0, pixels.Width  - 1);
        int x1 = Math.Clamp(right + pad, 0, pixels.Width  - 1);
        int y0 = Math.Clamp(top   - pad, 0, pixels.Height - 1);
        int y1 = Math.Clamp(bottom + pad, 0, pixels.Height - 1);

        // Clamp the inner edges too: an OCR rect can extend past the bitmap, and the
        // strips below index directly with no further bounds check.
        int topEdge    = Math.Clamp(top,        0, pixels.Height - 1);
        int bottomEdge = Math.Clamp(bottom + 1, 0, pixels.Height - 1);
        int leftEdge   = Math.Clamp(left - 1,   0, pixels.Width  - 1);
        int rightEdge  = Math.Clamp(right + 1,  0, pixels.Width  - 1);
        int bottomRow  = Math.Clamp(bottom,     0, pixels.Height - 1);

        int n = 0;
        SampleStrip(pixels, x0, y0, x1, Math.Max(y0, topEdge - 1), ref n);
        SampleStrip(pixels, x0, Math.Min(y1, bottomEdge), x1, y1,  ref n);
        SampleStrip(pixels, x0, topEdge, Math.Max(x0, leftEdge), bottomRow, ref n);
        SampleStrip(pixels, Math.Min(x1, rightEdge), topEdge, x1, bottomRow, ref n);

        // Opaque: the fill must fully hide the original characters underneath,
        // otherwise they "ghost" through behind the translation.
        int rgb = Backdrop.Colour(_samples.AsSpan(0, n));
        var bg = Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        double lum = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B;
        var fg = lum < 140 ? Colors.White : Colors.Black;
        return (bg, fg);
    }

    private void SampleStrip(PixelReader pixels, int x0, int y0, int x1, int y1, ref int n)
    {
        if (x1 <= x0 || y1 <= y0) return;
        int stepX = Math.Max(1, (x1 - x0) / 12);
        int stepY = Math.Max(1, (y1 - y0) / 4);
        for (int y = y0; y < y1; y += stepY)
        for (int x = x0; x < x1 && n < _samples.Length; x += stepX)
            _samples[n++] = pixels.Rgb(x, y);
    }

    private ContextMenu BuildContextMenu(OcrBlock block)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("ModernContextMenu") };

        var copyOriginal = new MenuItem { Header = "Copy original" };
        copyOriginal.Click += (_, _) =>
        {
            try { Clipboard.SetText(block.OriginalText); } catch { /* clipboard busy */ }
        };
        var copyTranslated = new MenuItem { Header = "Copy translation" };
        copyTranslated.Click += (_, _) =>
        {
            try { Clipboard.SetText(block.TranslatedText ?? ""); } catch { /* clipboard busy */ }
        };
        menu.Items.Add(copyOriginal);
        menu.Items.Add(copyTranslated);
        return menu;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (LanguageBox.IsDropDownOpen) return;

        // Hold Space to peek at the original text under the translation.
        if (e.Key == Key.Space)
        {
            ShowOriginal(true);
            e.Handled = true;
        }
        // Ctrl+C copies the whole translation, top to bottom — unless some text is
        // selected in one of the boxes: then it is the usual copy of that selection.
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control
                 && Keyboard.FocusedElement is not TextBox { SelectionLength: > 0 })
        {
            var text = string.Join(Environment.NewLine, _blocks
                .OrderBy(b => b.BoundingRect.Y).ThenBy(b => b.BoundingRect.X)
                .Select(b => string.IsNullOrWhiteSpace(b.TranslatedText) ? b.OriginalText : b.TranslatedText));
            try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
            e.Handled = true;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) ShowOriginal(false);
    }

    private void ShowOriginal(bool show) =>
        OverlayCanvas.Opacity = show ? 0 : 1; // Opacity, not Visibility: it keeps swallowing clicks

    /// <summary>
    /// Copies the screenshot into a frozen WPF bitmap. This used to encode the whole
    /// capture to BMP in a MemoryStream and let BitmapImage decode it again — on a
    /// multi-monitor desktop that is two extra ~100 MB copies plus the encode/decode,
    /// immediately after OCR has already peaked. WritePixels takes the locked GDI
    /// scanlines directly, so there is one copy and no codec.
    /// </summary>
    private static BitmapSource ToBitmapSource(DBitmap bmp)
    {
        var data = bmp.LockBits(
            new DRectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            DPixelFormat.Format32bppArgb);
        try
        {
            var wb = new WriteableBitmap(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(
                new Int32Rect(0, 0, bmp.Width, bmp.Height),
                data.Scan0,
                data.Stride * bmp.Height,
                data.Stride);
            wb.Freeze();
            return wb;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
