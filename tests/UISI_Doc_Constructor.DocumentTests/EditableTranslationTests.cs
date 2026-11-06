using System.IO;
using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

internal static class EditableTranslationTests
{
    private const string Source = "uisi-field:editable-source";
    private const string Target = "uisi-field:editable-target";
    private const string SecondTarget = "uisi-field:second-target";
    private const string Mirror = "uisi-field:mirror";
    private const string Shared = "uisi-field:shared";
    private const string Original = "ТОВ Українська компанія";
    private const string Corrected = "ТОВ Виправлена компанія — Київ, № 156";

    public static void Run(string root)
    {
        var fixture = Path.Combine(root, "editable-template.docx");
        using (var document = WordprocessingDocument.Create(fixture, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text(Original))), // Identical, unrelated text must never be replaced.
                new Paragraph(Field(Source, 101, "Author")),
                new Table(new TableProperties(), new TableGrid(new GridColumn(), new GridColumn()),
                    new TableRow(new TableCell(new Paragraph(Field(Source, 102, "Author again"))),
                    new TableCell(new Paragraph(Field(Target, 103, "English"))))),
                new Paragraph(Field(SecondTarget, 104, "English repeated")),
                new Paragraph(Field(Mirror, 105, "Same value")),
                new Paragraph(Field(Shared, 106, "Shared number"))));
            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(new Paragraph(Field(Source, 107, "Author in header")));
            var footer = main.AddNewPart<FooterPart>();
            footer.Footer = new Footer(new Paragraph(Field(Source, 108, "Author in footer")));
            main.Document.Body!.Append(new SectionProperties(
                new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
                new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) }));
        }
        var originalHash = Hash(fixture);
        var service = new TranslationPackageService(sessionRoot: Path.Combine(root, "EditableSessions"));
        var package = Path.Combine(root, "editable.uisi-translation");
        service.CreatePackage(new DocumentTemplate
        {
            Name = "Editable bilingual fixture", HasTranslation = true,
            TranslationFields =
            [
                new() { Tag = Source, Role = TranslationFieldRole.Source },
                new() { Tag = Target, Role = TranslationFieldRole.Target, SourceFieldTag = Source },
                new() { Tag = SecondTarget, Role = TranslationFieldRole.Target, SourceFieldTag = Source },
                new() { Tag = Mirror, Role = TranslationFieldRole.Target, SourceFieldTag = Source, PairMode = TranslationPairMode.SameValue },
                new() { Tag = Shared, Role = TranslationFieldRole.Shared }
            ]
        }, fixture,
        [
            new() { Tag = Source, DisplayName = "Название организации", Value = Original },
            new() { Tag = Target, DisplayName = "Название организации — EN" },
            new() { Tag = SecondTarget, DisplayName = "Повторный перевод — EN" },
            new() { Tag = Mirror, DisplayName = "Одинаковое значение — EN" },
            new() { Tag = Shared, DisplayName = "Номер договора", Value = "26-156" }
        ], package);
        using var session = service.OpenPackage(package);
        Assert(session.Manifest.FormatVersion == 2, "editable package has an explicitly versioned format");
        var values = service.CreateTargetItems(session);
        Assert(values.All(value => value.CanEditSource && !value.IsSourceReadOnly), "new linked sources are editable");
        Assert(ReferenceEquals(values[0].Source, values[1].Source), "rows linked to one source share one value");
        var notified = false;
        values[1].PropertyChanged += (_, args) => notified |= args.PropertyName == nameof(TranslationFieldValueItem.SourceValue);
        values[0].SourceValue = Corrected;
        Assert(values[1].SourceValue == Corrected && notified, "editing one row updates its sibling and notifies the UI");
        values[0].Value = "Corrected company";
        values[1].Value = "Second translation";
        Assert(session.Manifest.Fields.Single(field => field.Tag == Source).Value == Original, "unsaved edits do not mutate the manifest");
        var workingHash = Hash(session.WorkingDocumentPath);
        var revision = session.Manifest.Revision;
        try
        {
            service.SavePackage(session, values, Path.Combine(root, "missing-directory", "failure.uisi-translation"));
            throw new Exception("Saving to a missing directory unexpectedly succeeded.");
        }
        catch (DirectoryNotFoundException) { }
        Assert(session.Manifest.Revision == revision && Hash(session.WorkingDocumentPath) == workingHash,
            "failed save preserves revision and working document");
        var saved = Path.Combine(root, "edited.uisi-translation");
        service.SavePackage(session, values, saved);
        using var reopened = service.OpenPackage(saved);
        Assert(reopened.Manifest.Fields.Single(field => field.Tag == Source).Value == Corrected, "source correction survives reopening");
        Assert(reopened.Manifest.Fields.Single(field => field.Tag == Mirror).Value == Corrected, "same-value sibling tracks the correction");
        CheckDocument(reopened.WorkingDocumentPath, final: false);
        var reopenedValues = service.CreateTargetItems(reopened);
        Assert(reopenedValues[0].Value == "Corrected company" && reopenedValues[0].SourceValue == Corrected,
            "both languages survive portable package saving");
        var final = Path.Combine(root, "editable-final.docx");
        Assert(service.GenerateFinalDocument(reopened, reopenedValues, final) == 7,
            "final document fills four source occurrences, two translations and one mirrored value");
        CheckDocument(final, final: true);
        Assert(Hash(fixture) == originalHash, "editing translation never changes the original template");
        var finalHash = Hash(final);
        reopenedValues[0].SourceValue = " ";
        try
        {
            service.GenerateFinalDocument(reopened, reopenedValues, final);
            throw new Exception("Blank Ukrainian text was accepted for a final document.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("украинское поле")) { }
        Assert(Hash(final) == finalHash && reopened.Manifest.Fields.Single(field => field.Tag == Source).Value == Corrected,
            "failed final export preserves the existing DOCX and session values");
        // Saving an unfinished draft is allowed even when its Ukrainian correction is temporarily empty.
        var draft = Path.Combine(root, "draft.uisi-translation");
        service.SavePackage(reopened, reopenedValues, draft);
        using var draftSession = service.OpenPackage(draft);
        Assert(service.CreateTargetItems(draftSession)[0].SourceValue == " ", "unfinished Ukrainian edits can be saved as a draft");
        ExpectInvalid(() => service.SavePackage(session, [values[0]], saved));
        ExpectInvalid(() => service.SavePackage(session, [values[0], values[0]], saved));
        ExpectInvalid(() => service.SavePackage(session,
        [
            new() { Tag = Target, DisplayName = "EN", SourceValue = "Conflicting correction", Value = "First" },
            new() { Tag = SecondTarget, DisplayName = "EN 2", SourceValue = "Different correction", Value = "Second" }
        ], saved));
        Console.WriteLine("Editable Ukrainian translation: shared source bindings, repeated fields, same-value pairs, headers/footers, draft persistence, legacy compatibility and safe output passed.");
    }

    private static void CheckDocument(string path, bool final)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var main = document.MainDocumentPart!;
        Assert(main.Document!.Body!.Elements<Paragraph>().First().InnerText == Original, "unrelated identical text is unchanged");
        Assert(main.Document.InnerText.Contains(Corrected) && main.Document.InnerText.Contains("26-156"), "body contains corrected source and shared values");
        Assert(main.HeaderParts.Single().Header!.InnerText == Corrected && main.FooterParts.Single().Footer!.InnerText == Corrected,
            "source corrections reach header and footer occurrences");
        var controls = main.Document.Descendants<SdtElement>().Concat(main.HeaderParts.SelectMany(part => part.Header!.Descendants<SdtElement>()))
            .Concat(main.FooterParts.SelectMany(part => part.Footer!.Descendants<SdtElement>())).ToList();
        Assert(final ? controls.Count == 0 : controls.Count == 7, "working anchors retained until final DOCX export");
        Assert(main.Document.Descendants<Bold>().Any(), "original character formatting is retained");
        if (final) Assert(main.Document.InnerText.Contains("Corrected company") && main.Document.InnerText.Contains("Second translation"), "English translations reach final DOCX");
        var errors = new OpenXmlValidator().Validate(document).ToList();
        Assert(errors.Count == 0, "edited document passes Open XML validation: " + string.Join("; ", errors.Select(error => error.Description)));
    }

    private static SdtRun Field(string tag, int id, string text) => new(
        new SdtProperties(new SdtAlias { Val = tag }, new SdtId { Val = id }, new Tag { Val = tag }, new SdtContentRichText()),
        new SdtContentRun(new Run(new RunProperties(new Bold()), new Text(text))));
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void ExpectInvalid(Action action)
    {
        try { action(); throw new Exception("An invalid set of translation values was accepted."); }
        catch (InvalidDataException) { }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Editable translation regression failed: " + message);
    }
}
