using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UISI_Doc_Constructor.Models;

namespace UISI_Doc_Constructor.Services;

public sealed class OpenXmlDocumentService
{
    private const string FieldTagPrefix = "uisi-field:";

    public IReadOnlyList<TemplateField> ReadFields(string documentPath)
    {
        using var document = WordprocessingDocument.Open(documentPath, false);

        return EnumerateRoots(document)
            .SelectMany(root => root.Descendants<SdtElement>())
            .Where(control => !HasSameTagAncestor(control))
            .Select(ReadField)
            .Where(field => field is not null)
            .Cast<TemplateField>()
            .GroupBy(field => field.Tag, StringComparer.Ordinal)
            .Select(group => new TemplateField
            {
                Tag = group.Key,
                DisplayName = group
                    .Select(field => field.DisplayName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                    ?? "Без названия",
                Occurrences = group.Count()
            })
            .ToList();
    }

    public int RepairTemplateFields(string documentPath, bool createBackup = true)
    {
        var fullPath = Path.GetFullPath(documentPath);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(fullPath, temporaryPath, overwrite: false);
            int changes;
            using (var document = WordprocessingDocument.Open(temporaryPath, true))
            {
                var roots = EnumerateRoots(document).ToList();
                foreach (var root in roots) ValidateFieldNesting(root);
                changes = roots.Sum(NormalizeFields);
                if (changes > 0) foreach (var root in roots) root.Save();
            }
            if (changes == 0) return 0;
            if (createBackup)
                File.Copy(fullPath, fullPath + $".fields-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak",
                    overwrite: false);
            File.Move(temporaryPath, fullPath, overwrite: true);
            return changes;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public int RenameField(string documentPath, string fieldTag, string displayName)
    {
        var normalizedName = displayName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Введите название поля.", nameof(displayName));
        }

        using var document = WordprocessingDocument.Open(documentPath, true);
        var renamedCount = 0;

        foreach (var root in EnumerateRoots(document))
        {
            var matchingControls = root
                .Descendants<SdtElement>()
                .Where(control => string.Equals(GetTag(control), fieldTag, StringComparison.Ordinal))
                .ToList();

            foreach (var control in matchingControls)
            {
                var properties = control.GetFirstChild<SdtProperties>();
                if (properties is null)
                {
                    properties = new SdtProperties();
                    control.PrependChild(properties);
                }

                var alias = properties.GetFirstChild<SdtAlias>();
                if (alias is null)
                {
                    properties.AppendChild(new SdtAlias { Val = normalizedName });
                }
                else
                {
                    alias.Val = normalizedName;
                }

                renamedCount++;
            }

            if (matchingControls.Count > 0)
            {
                root.Save();
            }
        }

        if (renamedCount == 0)
        {
            throw new InvalidOperationException(
                "Поле больше не найдено в документе. Обновите список полей и повторите попытку.");
        }

        return renamedCount;
    }

    public int RemoveField(string documentPath, string fieldTag)
    {
        using var document = WordprocessingDocument.Open(documentPath, true);
        var removedCount = 0;

        foreach (var root in EnumerateRoots(document))
        {
            var matchingControls = root
                .Descendants<SdtElement>()
                .Where(control => string.Equals(GetTag(control), fieldTag, StringComparison.Ordinal))
                .OrderByDescending(control => control.Ancestors().Count())
                .ToList();

            foreach (var control in matchingControls)
            {
                UnwrapContentControl(
                    control,
                    "Одно из полей имеет повреждённую структуру и не может быть удалено.");
                removedCount++;
            }

            if (matchingControls.Count > 0)
            {
                root.Save();
            }
        }

        if (removedCount == 0)
        {
            throw new InvalidOperationException(
                "Поле больше не найдено в документе. Обновите список полей и повторите попытку.");
        }

        return removedCount;
    }

    public int GenerateDocument(
        string templatePath,
        string destinationPath,
        IReadOnlyDictionary<string, string> values)
    {
        var fullTemplatePath = Path.GetFullPath(templatePath);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (string.Equals(fullTemplatePath, fullDestinationPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Сохраните готовый документ отдельно от шаблона.");
        var temporaryPath = fullDestinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(fullTemplatePath, temporaryPath, overwrite: false);
            var replacements = FillDocument(temporaryPath, values);
            if (replacements > 0) File.Move(temporaryPath, fullDestinationPath, overwrite: true);
            return replacements;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static int FillDocument(string documentPath, IReadOnlyDictionary<string, string> values)
    {
        using var document = WordprocessingDocument.Open(documentPath, true);
        var replacementCount = 0;

        var roots = EnumerateRoots(document).ToList();
        foreach (var root in roots) ValidateFieldNesting(root);

        foreach (var root in roots)
        {
            NormalizeFields(root);
            var controls = root
                .Descendants<SdtElement>()
                .Where(control =>
                {
                    var tag = GetTag(control);
                    return tag is not null && values.ContainsKey(tag);
                })
                .OrderByDescending(control => control.Ancestors().Count())
                .ToList();

            foreach (var control in controls)
            {
                var tag = GetTag(control)!;
                var value = values[tag];

                ReplaceText(control, value);
                UnwrapContentControl(
                    control,
                    "Одно из полей имеет повреждённую структуру и не может быть преобразовано в обычный текст.");
                replacementCount++;
            }

            root.Save();
        }

        return replacementCount;
    }

    private static void UnwrapContentControl(SdtElement control, string invalidStructureMessage)
    {
        var content = control.ChildElements.FirstOrDefault(
            element => string.Equals(
                element.LocalName,
                "sdtContent",
                StringComparison.Ordinal));
        var parent = control.Parent;

        if (content is null || parent is null)
        {
            throw new InvalidDataException(invalidStructureMessage);
        }

        foreach (var child in content.ChildElements.ToList())
        {
            // Move, rather than clone: callers may still hold references to nested fields.
            child.Remove();
            parent.InsertBefore(child, control);
        }

        control.Remove();
    }

    private static bool HasSameTagAncestor(SdtElement control)
    {
        var tag = GetTag(control);
        return tag is not null && control.Ancestors<SdtElement>()
            .Any(ancestor => string.Equals(GetTag(ancestor), tag, StringComparison.Ordinal));
    }

    private static void ValidateFieldNesting(OpenXmlPartRootElement root)
    {
        foreach (var control in root.Descendants<SdtElement>())
        {
            var tag = GetTag(control);
            if (tag is null) continue;
            if (control.Ancestors<SdtElement>().Any(ancestor => GetTag(ancestor) is { } ancestorTag && ancestorTag != tag))
                throw new InvalidDataException(
                    $"Поле «{ReadField(control)!.DisplayName}» вложено в другое поле с отдельным идентификатором. " +
                    "В Word уберите лишнюю рамку поля, сохранив текст, и создайте поля в непересекающихся местах. " +
                    "Программа не изменяет такие поля автоматически, чтобы не потерять данные.");
        }
    }

    private static int NormalizeFields(OpenXmlPartRootElement root)
    {
        var changes = 0;
        var duplicates = root.Descendants<SdtElement>().Where(HasSameTagAncestor)
            .OrderByDescending(control => control.Ancestors().Count()).ToList();
        foreach (var duplicate in duplicates)
        {
            UnwrapContentControl(duplicate, "Вложенное поле имеет повреждённую структуру.");
            changes++;
        }
        foreach (var control in root.Descendants<SdtElement>().Where(control => GetTag(control) is not null))
        {
            var properties = control.GetFirstChild<SdtProperties>();
            var plainText = properties?.GetFirstChild<SdtContentText>();
            if (plainText is null) continue;
            // Plain-text SDTs prohibit nested controls and mixed run formatting.
            // Rich text retains the existing runs, styling, identifiers and language links.
            properties!.ReplaceChild(new SdtContentRichText(), plainText);
            changes++;
        }
        return changes;
    }

    private static TemplateField? ReadField(SdtElement control)
    {
        var tag = GetTag(control);
        if (tag is null)
        {
            return null;
        }

        var properties = control.GetFirstChild<SdtProperties>();
        var displayName = properties?
            .GetFirstChild<SdtAlias>()?
            .Val?
            .Value;

        return new TemplateField
        {
            Tag = tag,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Без названия" : displayName,
            Occurrences = 1
        };
    }

    private static string? GetTag(SdtElement control)
    {
        var tag = control
            .GetFirstChild<SdtProperties>()?
            .GetFirstChild<Tag>()?
            .Val?
            .Value;

        return tag is not null && tag.StartsWith(FieldTagPrefix, StringComparison.Ordinal)
            ? tag
            : null;
    }

    private static void ReplaceText(SdtElement control, string value)
    {
        var textElements = control.Descendants<Text>().ToList();
        if (textElements.Count == 0)
        {
            throw new InvalidDataException(
                "В шаблоне найдено поле без текстового содержимого. Пересоздайте это поле в Word.");
        }

        textElements[0].Text = value;
        textElements[0].Space = SpaceProcessingModeValues.Preserve;

        foreach (var extraText in textElements.Skip(1))
        {
            extraText.Text = string.Empty;
        }
    }

    private static IEnumerable<OpenXmlPartRootElement> EnumerateRoots(
        WordprocessingDocument document)
    {
        var mainPart = document.MainDocumentPart
            ?? throw new InvalidDataException("В DOCX отсутствует основная часть документа.");

        if (mainPart.Document is not null)
        {
            yield return mainPart.Document;
        }

        foreach (var headerPart in mainPart.HeaderParts)
        {
            if (headerPart.Header is not null)
            {
                yield return headerPart.Header;
            }
        }

        foreach (var footerPart in mainPart.FooterParts)
        {
            if (footerPart.Footer is not null)
            {
                yield return footerPart.Footer;
            }
        }
    }
}
