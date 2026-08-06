# UISI Document Constructor 1.2.0

- Added a local library of saved document fillings with search, filtering, rename, single-delete, and delete-all actions.
- Automatically save field values only after a document is created successfully, while suppressing duplicate value sets.
- Reload saved values safely by stable field identifier and normalized field name.
- Added a guided mapping screen for renamed or missing fields, including non-destructive suggestions and remembered manual mappings.
- Keep unmatched fields empty or explicitly ignore saved values without blocking document creation.
- Convert UISI content controls to ordinary formatted text in generated DOCX files while preserving editable controls in templates.
- Added polished Material Design dialogs for saved fillings, field mapping, renaming, and confirmation flows.
- Store saved fillings separately in the user's local application data so templates and documents remain intact across updates.
