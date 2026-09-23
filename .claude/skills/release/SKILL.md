---
name: release
description: Cut a Pickle release — bump the version, tag, let the Release workflow build binaries/MSIs/manifests, and publish to Scoop and winget. Use when asked to release, publish, ship a version, or update package managers.
---

# Releasing Pickle

1. **Check green**: `scripts/check.sh --e2e` locally and CI green on the default branch.
2. **Version**: update `<Version>` in `Directory.Build.props` (SemVer, e.g. `0.2.0`). Commit: `Release 0.2.0`.
3. **Tag**: `git tag v0.2.0 && git push origin v0.2.0` (or run the Release workflow manually with `version: 0.2.0`).
4. The **Release workflow** (`.github/workflows/release.yml`):
   - publishes single-file binaries for win-x64, win-arm64, linux-x64, osx-arm64 (`packaging/publish.ps1`)
   - builds MSIs with WiX 5.0.2 (`packaging/wix/build-msi.ps1`, MS-RL; WiX 6+ needs the OSMF EULA accepted — don't upgrade without the maintainer's decision)
   - writes `SHA256SUMS.txt` and manifests (`packaging/make_manifests.py`): Scoop + winget (portable exe + MSI)
   - creates the GitHub Release with all assets
   - commits `bucket/pickle.json` to the default branch (this repo is a Scoop bucket)
   - submits to winget-pkgs with `wingetcreate update` **only if** the `WINGET_TOKEN` secret exists
5. **winget, first time only**: the package `Daolyap.Pickle` must exist in `microsoft/winget-pkgs` before `update` works.
   Submit the generated manifests from the release (`manifests/winget/*.yaml`) with
   `wingetcreate submit <folder>` or a PR, then add a classic PAT with `public_repo` scope as the `WINGET_TOKEN` secret.
6. **Verify**: download the MSI and portable exe on Windows and run through `docs/manual-test-windows.md`.

Notes
- Binaries are unsigned; SmartScreen will warn. Code signing (e.g. SignPath's free OSS program) is the next step.
- NativeAOT is not possible (PowerShell SDK); expect ~100 MB binaries.
