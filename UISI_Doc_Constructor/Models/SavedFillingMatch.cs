namespace UISI_Doc_Constructor.Models;

public enum SavedFillingMatchKind
{
    FieldIdentifier,
    RememberedMapping,
    ExactName,
    Manual
}

public sealed record SavedFillingFieldMatch(
    SavedFillingField Source,
    TemplateField Target,
    SavedFillingMatchKind Kind);

public sealed class SavedFillingMatchResult
{
    public required IReadOnlyList<SavedFillingFieldMatch> AutomaticMatches { get; init; }

    public required IReadOnlyList<SavedFillingField> UnmatchedSourceFields { get; init; }

    public required IReadOnlyList<TemplateField> UnmatchedTargetFields { get; init; }
}

public sealed class FieldMappingTargetOption
{
    public TemplateField? Field { get; init; }

    public required string Label { get; init; }
}

public sealed class SavedFieldMappingItem
{
    public required SavedFillingField Source { get; init; }

    public required IReadOnlyList<FieldMappingTargetOption> TargetOptions { get; init; }

    public FieldMappingTargetOption? SelectedTarget { get; set; }

    public string SuggestionText { get; init; } = string.Empty;

    public string ValuePreview => Source.Value.Length <= 100
        ? Source.Value
        : $"{Source.Value[..97]}…";
}
