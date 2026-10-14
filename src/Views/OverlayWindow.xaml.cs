using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinLens.Models;
using WinLens.Native;
using WinLens.Services;
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

    private readonly Dictionary<OcrBlock, TextBox> _textBoxes = new();
    private string _currentTarget;
    private bool _suppressLangEvents;

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
        // OverlayCanvas sits on top and fills the window, so it receives every click that
        // a translation box doesn't handle first — including clicks over the dimmed area.
        OverlayCanvas.MouseLeftButtonDown += (_, _) => Close();
        Closed += (_, _) =>
        {
            // Release the heavy bits the window held: the decoded full-screen screenshot
            // and the per-line text boxes. Without this the visual tree keeps them alive.
            BackgroundImage.Source = null;
            OverlayCanvas.Children.Clear();
            _textBoxes.Clear();
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

            PlaceBackgroundImage();
            BuildOverlayBoxes();
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
        {
            PlaceBackgroundImage();
            BuildOverlayBoxes();
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
        _onTargetLangChanged(newTarget);

        await RetranslateAllAsync();
    }

    private async Task RetranslateAllAsync()
    {
        var tasks = _blocks.Select(async b =>
        {
            var translated = await _translator.TranslateAsync(b.OriginalText, _currentTarget, b.DetectedLanguage);
            b.TranslatedText = translated;
            if (_textBoxes.TryGetValue(b, out var tb))
                ApplyText(tb, translated);
        }).ToArray();
        await Task.WhenAll(tasks);
    }

    private void BuildOverlayBoxes()
    {
        OverlayCanvas.Children.Clear();
        _textBoxes.Clear();

        // Lock the screenshot once for the whole pass. Sampling used to go through
        // Bitmap.GetPixel, which locks and validates per call — around 200 calls per
        // block, on the UI thread, was a visible stall before the overlay faded in.
        using var pixels = new PixelReader(_screenshot);

        foreach (var block in _blocks)
        {
            var (bg, fg) = SampleColors(pixels, block.BoundingRect);

            // Convert pixel rect → DIPs, shifted by where the screenshot sits in the window.
            double dipX = (block.BoundingRect.X + OffsetX) * _pixelToDip;
            double dipY = (block.BoundingRect.Y + OffsetY) * _pixelToDip;
            double dipW = block.BoundingRect.Width  * _pixelToDip;
            double dipH = block.BoundingRect.Height * _pixelToDip;

            // Grow the filled background past the text so the original characters
            // underneath are fully covered (kills leftover edges like a stray "I").
            double padX = Math.Max(2.0, dipH * 0.18);
            double padY = Math.Max(1.0, dipH * 0.10);

            var border = new Border
            {
                Background = new SolidColorBrush(bg),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(padX, padY, padX, padY),
                Width  = dipW + padX * 2,
                Height = dipH + padY * 2,
                Cursor = Cursors.IBeam,
                SnapsToDevicePixels = true,
            };

            // Font size derived from the OCR rect height. Roughly 70% of line
            // height = visible cap height for Segoe UI.
            double fontSize = Math.Max(8, dipH * 0.7);

            var textBox = new TextBox
            {
                Text = block.TranslatedText ?? "",
                Foreground = new SolidColorBrush(fg),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsReadOnly = true,
                IsTabStop = false,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = fontSize,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                CaretBrush = new SolidColorBrush(fg),
                SelectionBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x7B, 0x6C, 0xF6)),
                Cursor = Cursors.IBeam,
                ContextMenu = BuildContextMenu(block),
                Width  = dipW,
                Height = dipH,
            };
            border.Child = textBox;

            // Stop bare-canvas click-to-close from firing for clicks on a box.
            border.MouseLeftButtonDown += (_, ev) => ev.Handled = true;

            Canvas.SetLeft(border, dipX - padX);
            Canvas.SetTop(border,  dipY - padY);
            OverlayCanvas.Children.Add(border);

            _textBoxes[block] = textBox;
            ApplyText(textBox, block.TranslatedText ?? "");
        }
    }

    /// <summary>Sets the text and shrinks the font so it fits the original box width.</summary>
    private void ApplyText(TextBox tb, string text)
    {
        tb.Text = text;
        tb.FontSize = FitFontSize(text, tb.FontFamily, tb.FontWeight, tb.Width, tb.Height);
    }

    private double FitFontSize(string text, FontFamily family, FontWeight weight, double maxW, double maxH)
    {
        double size = Math.Max(8, maxH * 0.78);
        if (string.IsNullOrWhiteSpace(text) || double.IsNaN(maxW) || maxW <= 0)
            return size;

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var tf = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);

        for (int i = 0; i < 30 && size > 7; i++)
        {
            var ft = new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                tf, size, System.Windows.Media.Brushes.Black, pixelsPerDip);
            if (ft.WidthIncludingTrailingWhitespace <= maxW - 2)
                break;
            size -= Math.Max(0.5, size * 0.08);
        }
        return Math.Max(7, size);
    }

    /// <summary>
    /// Reads pixels straight out of a locked GDI bitmap. Assumes the 32bpp ARGB, top-down
    /// layout GDI+ gives for the bitmaps <see cref="Native.ScreenCapture"/> creates, which
    /// is the same assumption <see cref="OcrService"/> makes when it converts them.
    /// </summary>
    private sealed class PixelReader : IDisposable
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
        public (byte R, byte G, byte B) Get(int x, int y)
        {
            int bgra = Marshal.ReadInt32(_data.Scan0, y * _data.Stride + x * 4);
            return ((byte)((bgra >> 16) & 0xFF), (byte)((bgra >> 8) & 0xFF), (byte)(bgra & 0xFF));
        }

        public void Dispose() => _bitmap.UnlockBits(_data);
    }

    private static (Color bg, Color fg) SampleColors(PixelReader pixels, Rect rect)
    {
        int left   = (int)Math.Floor(rect.X);
        int top    = (int)Math.Floor(rect.Y);
        int right  = (int)Math.Ceiling(rect.X + rect.Width);
        int bottom = (int)Math.Ceiling(rect.Y + rect.Height);

        long r = 0, g = 0, b = 0, n = 0;

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

        SampleStrip(pixels, x0, y0, x1, Math.Max(y0, topEdge - 1), ref r, ref g, ref b, ref n);
        SampleStrip(pixels, x0, Math.Min(y1, bottomEdge), x1, y1,  ref r, ref g, ref b, ref n);
        SampleStrip(pixels, x0, topEdge, Math.Max(x0, leftEdge), bottomRow, ref r, ref g, ref b, ref n);
        SampleStrip(pixels, Math.Min(x1, rightEdge), topEdge, x1, bottomRow, ref r, ref g, ref b, ref n);

        Color bg;
        if (n == 0)
            bg = Color.FromArgb(0xFF, 0, 0, 0);
        else
        {
            byte br = (byte)(r / n), bgc = (byte)(g / n), bb = (byte)(b / n);
            // Opaque: the fill must fully hide the original characters underneath,
            // otherwise they "ghost" through behind the translation.
            bg = Color.FromArgb(0xFF, br, bgc, bb);
        }

        double lum = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B;
        var fg = lum < 140 ? Colors.White : Colors.Black;
        return (bg, fg);
    }

    private static void SampleStrip(PixelReader pixels, int x0, int y0, int x1, int y1,
                                    ref long r, ref long g, ref long b, ref long n)
    {
        if (x1 <= x0 || y1 <= y0) return;
        int stepX = Math.Max(1, (x1 - x0) / 12);
        int stepY = Math.Max(1, (y1 - y0) / 4);
        for (int y = y0; y < y1; y += stepY)
        for (int x = x0; x < x1; x += stepX)
        {
            var (pr, pg, pb) = pixels.Get(x, y);
            r += pr; g += pg; b += pb; n++;
        }
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
