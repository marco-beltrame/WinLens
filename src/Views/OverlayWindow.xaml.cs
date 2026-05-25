using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinLens.Models;
using WinLens.Services;
using DBitmap = System.Drawing.Bitmap;
using DRectangle = System.Drawing.Rectangle;

namespace WinLens.Views;

public partial class OverlayWindow : Window
{
    private readonly DRectangle _virtualBounds;
    private readonly DBitmap _screenshot;
    private readonly List<OcrBlock> _blocks;
    private readonly TranslationService _translator;
    private readonly Action<string> _onTargetLangChanged;
    private readonly IReadOnlyList<string> _recentTargets;
    private readonly double _pixelToDip;

    private readonly Dictionary<OcrBlock, TextBox> _textBoxes = new();
    private string _currentTarget;

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    public OverlayWindow(
        DBitmap screenshot,
        DRectangle virtualBounds,
        List<OcrBlock> blocks,
        TranslationService translator,
        string initialTarget,
        Action<string> onTargetLangChanged,
        IReadOnlyList<string>? recentTargets = null)
    {
        InitializeComponent();
        _screenshot = screenshot;
        _virtualBounds = virtualBounds;
        _blocks = blocks;
        _translator = translator;
        _currentTarget = initialTarget;
        _onTargetLangChanged = onTargetLangChanged;
        _recentTargets = recentTargets ?? Array.Empty<string>();

        uint dpi = GetDpiForSystem();
        if (dpi == 0) dpi = 96;
        _pixelToDip = 96.0 / dpi;

        // Position + size window in DIPs to cover the virtual screen.
        Left   = virtualBounds.X      * _pixelToDip;
        Top    = virtualBounds.Y      * _pixelToDip;
        Width  = virtualBounds.Width  * _pixelToDip;
        Height = virtualBounds.Height * _pixelToDip;

        BackgroundImage.Source = ToBitmapSource(screenshot);

        PopulateLanguages();

        KeyDown += OnKeyDown;
        OverlayCanvas.MouseLeftButtonDown += (_, _) => Close();
        Closed += (_, _) =>
        {
            // Release the heavy bits the window held: the decoded full-screen screenshot
            // and the per-line text boxes. Without this the visual tree keeps them alive.
            BackgroundImage.Source = null;
            OverlayCanvas.Children.Clear();
            _textBoxes.Clear();
            _screenshot.Dispose();

            // Reclaim only once the window is actually torn down and dereferenced â€” running
            // the compaction here (still inside Closed) would find everything still rooted.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(MemoryHygiene.Trim));
        };
        Loaded += (_, _) =>
        {
            BuildOverlayBoxes();
            BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150))));
            Activate();
            Focus();
        };
    }

    private void PopulateLanguages()
    {
        var itemStyle = (Style)FindResource("ModernComboBoxItem");
        LanguagePicker.Populate(LanguageBox, itemStyle, _currentTarget, _recentTargets);
        if (LanguageBox.SelectedIndex < 0)
            LanguageBox.SelectedIndex = 0;
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
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

        foreach (var block in _blocks)
        {
            var (bg, fg) = SampleColors(_screenshot, block.BoundingRect);

            // Convert pixel rect â†’ DIPs.
            double dipX = block.BoundingRect.X      * _pixelToDip;
            double dipY = block.BoundingRect.Y      * _pixelToDip;
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

    private static (Color bg, Color fg) SampleColors(DBitmap bmp, Rect rect)
    {
        int left   = (int)Math.Floor(rect.X);
        int top    = (int)Math.Floor(rect.Y);
        int right  = (int)Math.Ceiling(rect.X + rect.Width);
        int bottom = (int)Math.Ceiling(rect.Y + rect.Height);

        long r = 0, g = 0, b = 0, n = 0;

        int pad = Math.Max(2, (bottom - top) / 6);
        int x0 = Math.Clamp(left  - pad, 0, bmp.Width  - 1);
        int x1 = Math.Clamp(right + pad, 0, bmp.Width  - 1);
        int y0 = Math.Clamp(top   - pad, 0, bmp.Height - 1);
        int y1 = Math.Clamp(bottom + pad, 0, bmp.Height - 1);

        SampleStrip(bmp, x0, y0, x1, Math.Max(y0, top - 1),     ref r, ref g, ref b, ref n);
        SampleStrip(bmp, x0, Math.Min(y1, bottom + 1), x1, y1,  ref r, ref g, ref b, ref n);
        SampleStrip(bmp, x0, top, Math.Max(x0, left - 1), bottom, ref r, ref g, ref b, ref n);
        SampleStrip(bmp, Math.Min(x1, right + 1), top, x1, bottom, ref r, ref g, ref b, ref n);

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

    private static void SampleStrip(DBitmap bmp, int x0, int y0, int x1, int y1,
                                    ref long r, ref long g, ref long b, ref long n)
    {
        if (x1 <= x0 || y1 <= y0) return;
        int stepX = Math.Max(1, (x1 - x0) / 12);
        int stepY = Math.Max(1, (y1 - y0) / 4);
        for (int y = y0; y < y1; y += stepY)
        for (int x = x0; x < x1; x += stepX)
        {
            var p = bmp.GetPixel(x, y);
            r += p.R; g += p.G; b += p.B; n++;
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

    private static BitmapSource ToBitmapSource(DBitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
