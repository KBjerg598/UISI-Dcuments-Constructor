# Local OCR regression checks

Run on Windows x64 from the repository root:

```powershell
dotnet run --project tests/UISI_Doc_Constructor.OcrTests -c Release
```

The checks use synthetic images, not personal documents. They cover Ukrainian, Russian, English and mixed-language recognition, word bounds, excluded/restored words, masks, blank images, cancellation, append/replace behavior, display scaling, and mouse hit testing across the selection overlay. A short-lived modal dialog also verifies that cancelling screen capture preserves the dialog's modality and original opacity.

For a manual review-window check:

```powershell
dotnet run --project tests/UISI_Doc_Constructor.OcrTests -c Release -- --preview
```

Use `--source` instead to display a synthetic two-line image for screen-selection checks. OCR models and native runtime files are copied from the main project; recognition requires no network connection.

## Word field regression checks

```powershell
dotnet run --project tests/UISI_Doc_Constructor.DocumentTests -c Release
```

These synthetic-document checks cover redundant nested field identifiers, rich-text normalization, preserved text and run formatting, independent repeated fields, headers/footers, partial author filling, final translation filling, backups, idempotence, overlapping selection ranges, and rejected ambiguous nesting. Failed generation must leave an existing destination unchanged.

Ordinary DOCX export removes all UISI field frames, including English fields that were not filled by the author. Their existing text and formatting are preserved in body paragraphs, bilingual tables, and headers/footers. Independent Word forms and the original template remain unchanged. Partial working documents and portable translation packages retain their field anchors for the translator.

Editable-translation checks also cover Ukrainian source corrections, synchronized rows sharing a source, same-value pairs, draft saving, portable package reopening, and unchanged unrelated text. New packages use format 2 to retain source anchors until final DOCX export. Format-1 packages remain readable and translatable, but their flattened Ukrainian text is read-only; regenerate a package from the existing template to enable corrections. Computers exchanging format-2 packages must use a version that supports this format.

Short-lived UI fixtures reuse the actual application styles without opening the user's library. They check full import/final button labels, minimum window size, two-way Ukrainian text bindings, and legacy read-only fields. Preview PNGs at 100%, 150%, and 200% render scales are saved in the ignored test output directory for visual inspection.

To verify a real template without modifying it, append `-- --template 'C:\path\to\template.docx'`. The check works only on copies in the ignored test output directory; do not commit personal templates or generated documents.
