# UISI Document Constructor 1.2.2

- Fixed translation packages being hidden by the file picker after transfer to another computer.
- Allow valid `.uisi-translation` packages renamed or wrapped as `.zip` files to be selected and opened.
- Added an All Files option so packages with an altered extension can still be selected manually.
- Continue validating the package manifest and embedded document before opening, regardless of the file extension.
- Centralized translation package filters to keep create, save, and open dialogs consistent.
- Exclude local `.uisi-translation` work packages from Git tracking.
