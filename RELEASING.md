# Releasing UISI Document Constructor

UISI uses Velopack for its Windows installer and in-app updates. The application reads releases from GitHub and does not embed a personal access token.

## Repository visibility

The repository that hosts update releases must be public so installed clients can download releases safely without a shared secret. If the source repository stays private, create a separate public repository that contains release assets only and change `source` in `UISI_Doc_Constructor/update-config.json`.

Never commit or bundle a GitHub token. `UISI_GITHUB_TOKEN` is supported only for temporary developer testing against a private repository.

## Local build

Run the release script from the repository root:

```powershell
.\scripts\Build-Release.ps1 -Version 1.2.0 -Channel test
```

The script restores the pinned Velopack CLI, publishes a self-contained Windows build, and writes the installer and update feed to `artifacts/releases`.

Use `test` for preview releases and `stable` for public production releases. The chosen channel is embedded in the packaged `update-config.json`.

## GitHub release

Open the **Publish Windows release** workflow in GitHub Actions and provide a semantic version and channel. The workflow builds the installer and uploads the complete Velopack release to GitHub Releases. Test-channel releases are marked as GitHub pre-releases.

Before publishing:

1. Update `RELEASE_NOTES.md` in English.
2. Confirm that the version has not already been published.
3. Run a local Release build and smoke test.
4. Publish to `test` first, verify an update from an installed older version, then publish `stable`.

## Local update test

An installed build can use a local feed without changing source code:

```powershell
$env:UISI_UPDATE_SOURCE = 'C:\path\to\artifacts\releases'
$env:UISI_UPDATE_CHANNEL = 'test'
```

These variables are intended only for development and test automation.
