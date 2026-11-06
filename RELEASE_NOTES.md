# UISI Document Constructor 1.3.2

This maintenance release improves bilingual document export and lets translators correct Ukrainian source values alongside their English translations.

## Clean Word documents

- Remove UISI field frames from both Ukrainian and English sides when creating an ordinary DOCX, including English fields that were not filled by the author.
- Preserve the existing English text and character formatting while removing its field frames, including repeated fields, bilingual tables, and headers/footers.
- Keep field anchors in the original template and in working translation packages so future filling and translation continue to work.
- Leave independent Word forms unchanged.

## Editable Ukrainian text during translation

- Edit Ukrainian source text directly in the left-hand column of the translation window. Text selection, Ctrl+C, and the one-click copy button remain available.
- Save source corrections in the portable translation package and apply them to every linked occurrence in the final DOCX.
- Keep rows that share a Ukrainian source synchronized and update linked same-value English fields automatically.
- Allow unfinished Ukrainian corrections to be saved as a draft, but require non-empty Ukrainian and English values before final DOCX export.
- Preserve the previous package revision and working document if saving fails. Final export validates a temporary document before replacing an existing destination.

## Interface fixes

- Let the template import and final DOCX buttons size themselves to their full labels instead of clipping the text.
- Keep translation actions visible at the minimum window size and wrap long Ukrainian field names.
- Explain whether Ukrainian source editing is available in the opened package.

## Compatibility

- Existing templates and saved fillings do not need to be recreated or migrated.
- New translation packages use format 2, retaining Ukrainian field anchors until final export. Both computers exchanging these packages must use version 1.3.2 or newer.
- Format-1 translation packages still open, save, and generate final documents. Their Ukrainian reference text remains read-only because those source anchors were already removed; create a new package from the existing template to enable source corrections.
- The existing update channel is unchanged so installed versions can discover this release.

## Verification

- Added regression checks for frame-free ordinary DOCX export, preserved English text and formatting, tables, repeated fields, headers/footers, and unchanged templates.
- Verified editable Ukrainian bindings, same-value pairs, saved drafts, package reopening, legacy read-only behavior, and safe failure paths.
- Checked full button labels, minimum window sizes, and 100%, 150%, and 200% render scales.
