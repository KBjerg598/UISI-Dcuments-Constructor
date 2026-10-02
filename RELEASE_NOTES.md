# UISI Document Constructor 1.3.1

This release adds faster field entry with one-click copying and private, on-device screen text recognition. The changes below cover everything added since version 1.2.2.

## Word document reliability

- Prevent creating a field inside, around, or partially overlapping an existing content control, including English translation fields. Adjacent non-overlapping text remains selectable.
- Create new fields as rich-text controls so Word can preserve mixed formatting without violating plain-text control restrictions.
- Remove redundant nested UISI controls with the same field identifier, preserving text, formatting, the surviving identifier, and translation links.
- Normalize legacy UISI plain-text controls to rich text before generating a document, importing a template, or opening the stored template in Word.
- Keep a timestamped backup before repairing an existing stored template. Imports and translation packages are repaired only in their app-managed copies; original source files are unchanged.
- Repair extracted legacy translation documents only after checking their original package checksum.
- Reject nested fields with different identifiers with a clear explanation instead of silently losing one field's data.
- Generate documents through a temporary copy and replace the destination only after success, so a rejected or failed fill cannot overwrite a previously saved document.
- Added regression checks using synthetic nested fields and a read-only verification of an existing memorandum template; text, formatting, stable field identifiers, and independent repeated occurrences are preserved.

## One-click field copying

- Added a compact copy button beside each document-filling field and each editable English translation field.
- Added the same shortcut beside the translator's read-only Ukrainian reference text.
- Copy the complete value, including Unicode characters and line breaks, without selecting text first.
- Empty values disable the copy button. Successful copies show a temporary checkmark; a busy clipboard is retried and reported without interrupting the form.
- Added field-specific accessibility labels and tooltips for copy and recognition buttons.

## Local screen text recognition

- Added an OCR button beside document-filling fields and editable translation fields.
- Select a screen region by dragging the mouse over a darkened desktop, then review the recognized text before inserting it. Press Esc to cancel selection without changing the field.
- Support Ukrainian, Russian, English, and automatic mixed-language recognition. Automatic mode compares aligned word candidates to reduce Latin/Cyrillic look-alike errors.
- Recognition runs locally using bundled Tesseract and language models. Screenshots are processed in memory and are not uploaded or saved as temporary image files.
- Bundled the required native runtime libraries, language models, and third-party license notices with the Windows x64 installer and portable build. No separate OCR installation or model download is required.

## Review and correct recognized text

- Added a Material Design review window with the captured image, editable text, language selection, zoom, and fit-to-window controls.
- Click individual recognized words to exclude them from the result or restore them.
- Mask unwanted image regions, undo masks, and run recognition again to omit those areas.
- Replace the current field value or append recognized text with a line break. The original value remains unchanged until insertion is confirmed.
- Warn before a new recognition pass or word selection would overwrite manual corrections.
- Run recognition in the background with progress feedback and cancellation when the review window closes. Empty results cannot be inserted, and uncertain words are flagged for review.

## Fixes and layout improvements

- Fixed screen selection ignoring mouse clicks and drags by making the full overlay receive mouse input.
- Preserve modal filling dialogs and their original opacity during screen capture, including cancellation and errors.
- Account for desktop coordinates, display scaling, and cropped image bounds when selecting a region.
- Adjusted document-filling and translation-field layouts to accommodate the new actions without squeezing the text areas.
- Removed duplicate OCR asset entries from the project configuration.

## Compatibility and verification

- Existing templates, saved fillings, and translation packages remain compatible; this release does not change their formats or storage locations.
- Added automated OCR checks for Ukrainian, Russian, English, mixed-language text, word bounds, masks, blank images, cancellation, append/replace behavior, display scaling, and selection hit testing.
- OCR is available in the Windows x64 build. Always check recognized names, dates, and numbers before creating a document.
