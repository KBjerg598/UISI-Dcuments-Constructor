using System.Windows;

namespace UISI_Doc_Constructor.Models;

public sealed class OcrWord
{
    public required string Text { get; init; }
    public required Rect Bounds { get; init; }
    public int Line { get; init; }
    public float Confidence { get; init; }
    public bool IsDictionaryWord { get; init; }
    public bool Included { get; set; } = true;
}

public static class OcrTextComposer
{
    public static string Compose(IEnumerable<OcrWord> words) => string.Join(Environment.NewLine,
        words.Where(word => word.Included)
            .GroupBy(word => word.Line)
            .Select(line => string.Join(" ", line.Select(word => word.Text))));

    public static string Insert(string? existing, string recognized, bool append)
    {
        if (!append || string.IsNullOrEmpty(existing))
            return recognized;
        return existing + (char.IsWhiteSpace(existing[^1]) ? string.Empty : Environment.NewLine) + recognized;
    }
}
