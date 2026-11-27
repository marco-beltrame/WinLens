using System;
using System.Collections.Generic;
using System.Windows;
using WinLens.Services;

namespace WinLens.Models;

/// <summary>
/// One piece of text that is translated and drawn as a unit: a single OCR line, or a
/// paragraph of several lines that wrap. Coordinates are in source-image pixels (same
/// coordinate space as the captured screenshot).
/// </summary>
public sealed class OcrBlock
{
    public required string OriginalText { get; init; }
    public string TranslatedText { get; set; } = "";

    /// <summary>The whole block; for a paragraph, the box around all of its lines.</summary>
    public required Rect BoundingRect { get; init; }

    /// <summary>Where each line of the original sat, top to bottom. One entry for a single line.</summary>
    public IReadOnlyList<Rect> LineRects { get; init; } = Array.Empty<Rect>();

    /// <summary>How the lines of a paragraph line up; meaningless for a single line.</summary>
    public ParagraphAlignment Alignment { get; init; }

    public string? DetectedLanguage { get; init; }
}
