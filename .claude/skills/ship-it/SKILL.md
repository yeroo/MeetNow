---
name: ship-it
description: Use when the user asks to ship, release, or publish a new MeetNow version from a feature branch, or to upgrade the installed app to the latest code.
---

# Ship a MeetNow release

Releases the current feature branch as a new version: PR → independent
review → merge → tag → GitHub release with the Inno installer → upgrade
this machine from the *downloaded* release asset.

## Versioning

- Tags/releases are unprefixed `X.Y.Z` (no `v`). Feature branch → minor
  bump, `fix/*` → patch bump, over the latest `gh release list` version.
- The version lives in TWO files that must both be bumped, in the same
  commit on the feature branch:
  - `installer/MeetNow.iss`: `#define MyAppVersion "X.Y.Z"`
  - `native/app/app.rc`: `FILEVERSION`/`PRODUCTVERSION` (commas) and the
    two string `VALUE`s (dotted, 4th digit 0)
- Never touch `MeetNow/BuildInfo.cs` or `*.csproj` — legacy C# app.

## Steps

1. **Bump + commit** the two version files on the feature branch
   (`git add` the two files EXPLICITLY — never a bare `git add`/`-A`;
   the user often has unrelated staged/unstaged C# changes).
2. **Push and raise the PR**: `git push -u origin <branch>`, then
   `gh pr create --base main` with a summary and test-plan body.
3. **Independent review — required, before merging.** Dispatch a fresh
   subagent (it must not share this session's authoring context) to
   review `gh pr diff <N>` against the PR description; use
   superpowers:requesting-code-review if available. Fix real findings
   and push before merging; record "reviewed, N findings" in the PR.
4. **Merge**: `gh pr merge <N> --merge` (merge commit, keep branch).
5. **Sync + tag the merge commit**:
   `git checkout main && git pull`, then
   `git tag X.Y.Z origin/main && git push origin X.Y.Z`.
6. **Build from main** — kill the app first or the link step fails with
   LNK1104 on a locked exe:
   `Stop-Process -Name MeetNow -Force` (ignore errors), then the
   vswhere/MSBuild command from `native/README.md` (needs
   `-products *`; toolset v145 = VS 2026 Build Tools). Confirm
   `(Get-Item native\x64\Release\MeetNow.exe).VersionInfo.FileVersion`
   equals the new version.
7. **Compile installer**:
   `& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /Qp installer\MeetNow.iss`
   → `installer/Output/MeetNow-Setup-X.Y.Z.exe`.
8. **Release**: `gh release create X.Y.Z <installer exe> --title
   "MeetNow X.Y.Z" --notes "<short user-facing notes>"`.
9. **Install the DOWNLOADED asset, not the local file** — this verifies
   what users actually get: `gh release download X.Y.Z --pattern
   "MeetNow-Setup-*.exe"` to a temp dir, compare `Get-FileHash` with the
   local build, then run it with
   `/VERYSILENT /SUPPRESSMSGBOXES /TASKS=startup`
   (startup task keeps the sign-in autostart shortcut).
10. **Verify**: setup exit code 0; installed
    `%LOCALAPPDATA%\Programs\MeetNow\MeetNow.exe` FileVersion is X.Y.Z;
    `Startup\MeetNow.lnk` targets it; start it and confirm exactly ONE
    MeetNow.exe process from the installed path.

## Common mistakes

| Mistake | Consequence |
|---|---|
| Bumping only the .iss, not app.rc | exe reports the old version |
| Building while MeetNow.exe runs | LNK1104: cannot open MeetNow.exe |
| Tagging before `git pull` on main | tag lands on a stale commit |
| Installing the locally built setup | the published asset goes unverified |
| Skipping the review step | the user asked for it every time — do not |
| `git add -A` for the bump commit | drags the user's unrelated changes in |
