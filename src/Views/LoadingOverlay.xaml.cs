using System.Windows;

namespace WinLens.Views;

/// <summary>
/// Small, non-interactive spinner shown while a translation cycle runs (OCR + network).
/// Created after the screenshot is taken so it never appears in the captured image.
/// </summary>
public partial class LoadingOverlay : Window
{
    public LoadingOverlay()
    {
        InitializeComponent();
    }
}
