using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinLens.Views;

/// <summary>One language in the settings window's offline list.</summary>
public sealed class OfflineLanguageRow : INotifyPropertyChanged
{
    private bool _installed;
    private bool _downloading;
    private bool _busy;
    private double _progress;
    private long _sizeBytes;

    public OfflineLanguageRow(string code, string label)
    {
        Code = code;
        Label = label;
    }

    public string Code { get; }
    public string Label { get; }

    public bool Installed { get => _installed; set => Set(ref _installed, value); }
    public bool Downloading { get => _downloading; set => Set(ref _downloading, value); }
    /// <summary>Another language is downloading; one at a time.</summary>
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public long SizeBytes { get => _sizeBytes; set => Set(ref _sizeBytes, value); }

    public string Action => Downloading ? "Cancel" : Installed ? "Remove" : "Download";
    public bool CanAct => Downloading || Installed || !Busy;

    public string Detail =>
        Downloading ? $"{_progress * 100:0}%"
        : Installed || _sizeBytes <= 0 ? ""
        : $"{_sizeBytes / 1_000_000.0:0} MB";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Action)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAct)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
    }
}
