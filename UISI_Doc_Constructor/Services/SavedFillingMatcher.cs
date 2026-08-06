using System.Text;
using UISI_Doc_Constructor.Models;

namespace UISI_Doc_Constructor.Services;

public sealed class SavedFillingMatcher
{
    public SavedFillingMatchResult Match(
        SavedFilling filling,
        Guid targetTemplateId,
        IEnumerable<TemplateField> targetFields)
    {
        var targets = targetFields.ToList();
        var matches = new List<SavedFillingFieldMatch>();
        var matchedSourceTags = new HashSet<string>(StringComparer.Ordinal);
        var matchedTargetTags = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in filling.Fields)
        {
            var target = targets.FirstOrDefault(candidate =>
                !matchedTargetTags.Contains(candidate.Tag) &&
                string.Equals(candidate.Tag, source.Tag, StringComparison.Ordinal));
            AddMatch(source, target, SavedFillingMatchKind.FieldIdentifier);
        }

        foreach (var source in filling.Fields.Where(source => !matchedSourceTags.Contains(source.Tag)))
        {
            var remembered = filling.ManualMappings.LastOrDefault(mapping =>
                mapping.TargetTemplateId == targetTemplateId &&
                string.Equals(mapping.SourceFieldTag, source.Tag, StringComparison.Ordinal));
            var target = remembered is null
                ? null
                : targets.FirstOrDefault(candidate =>
                    !matchedTargetTags.Contains(candidate.Tag) &&
                    string.Equals(candidate.Tag, remembered.TargetFieldTag, StringComparison.Ordinal));
            AddMatch(source, target, SavedFillingMatchKind.RememberedMapping);
        }

        var remainingSources = filling.Fields
            .Where(source => !matchedSourceTags.Contains(source.Tag))
            .ToList();
        var uniqueSourceNames = remainingSources
            .GroupBy(source => NormalizeName(source.DisplayName), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var uniqueTargetNames = targets
            .Where(target => !matchedTargetTags.Contains(target.Tag))
            .GroupBy(target => NormalizeName(target.DisplayName), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        foreach (var pair in uniqueSourceNames)
        {
            if (uniqueTargetNames.TryGetValue(pair.Key, out var target))
            {
                AddMatch(pair.Value, target, SavedFillingMatchKind.ExactName);
            }
        }

        return new SavedFillingMatchResult
        {
            AutomaticMatches = matches,
            UnmatchedSourceFields = filling.Fields
                .Where(source => !matchedSourceTags.Contains(source.Tag))
                .ToList(),
            UnmatchedTargetFields = targets
                .Where(target => !matchedTargetTags.Contains(target.Tag))
                .ToList()
        };

        void AddMatch(
            SavedFillingField source,
            TemplateField? target,
            SavedFillingMatchKind kind)
        {
            if (target is null || matchedSourceTags.Contains(source.Tag))
            {
                return;
            }

            matches.Add(new SavedFillingFieldMatch(source, target, kind));
            matchedSourceTags.Add(source.Tag);
            matchedTargetTags.Add(target.Tag);
        }
    }

    public TemplateField? FindSuggestion(
        SavedFillingField source,
        IEnumerable<TemplateField> availableTargets)
    {
        var sourceName = NormalizeForSimilarity(source.DisplayName);
        if (sourceName.Length < 3)
        {
            return null;
        }

        var candidates = availableTargets
            .Select(target => new
            {
                Target = target,
                Score = CalculateSimilarity(
                    sourceName,
                    NormalizeForSimilarity(target.DisplayName))
            })
            .OrderByDescending(candidate => candidate.Score)
            .ToList();

        return candidates.Count > 0 && candidates[0].Score >= 0.58
            ? candidates[0].Target
            : null;
    }

    private static string NormalizeName(string value) =>
        string.Join(
                ' ',
                value.Trim().Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();

    private static string NormalizeForSimilarity(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in NormalizeName(value))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static double CalculateSimilarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitutionCost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(
                        current[rightIndex - 1] + 1,
                        previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        var distance = previous[right.Length];
        return 1d - (double)distance / Math.Max(left.Length, right.Length);
    }
}
