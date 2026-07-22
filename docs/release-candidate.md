# Release candidate

Current candidate versions:

- Plugin: `0.4.4`
- Rule set: `0.3.1`
- HDT: `1.53.11`
- Runtime: `.NET Framework 4.7.2`, x64

Build the default no-display Shadow candidate on Windows after preparing the
pinned HDT reference:

```powershell
.\scripts\build-release-candidate.ps1
```

The script verifies that the source constants match `profiles\release.json`,
builds the HDT plugin, and writes a ZIP under
`.artifacts\release-candidate`. The archive contains only the required plugin
assemblies, a Shadow `settings.json`, the release manifest, installation notes,
and a SHA-256 package manifest. PDB files and local evidence are not included.

An experimental visible-test package is deliberately unavailable without a
verified final evidence archive:

```powershell
.\scripts\build-release-candidate.ps1 `
  -PresentationMode experimental `
  -EvidencePath .\.artifacts\validation-evidence\<final-archive>
```

That command runs `verify-validation-evidence.ps1` with
`-RequireVisiblePrerequisites` before building. A fixture-only, incomplete,
tampered, mixed-version, or below-threshold archive is rejected.

## Current limitations

- This is a Shadow release candidate until the real 200-annotation and
  50-qualified-game evidence gates pass.
- Exact behavior is limited to the pinned deck, Hearthstone build, CardDefs,
  HearthDb, and HDT compatibility profile.
- Generated one-cost minions and external cards with unsupported triggers,
  auras, replacements, or transformations explicitly degrade instead of being
  approximated.
- The full unsupported boundary list is maintained in
  `docs\rules\wild-discard-warlock.md`.
- The package does not install or start HDT automatically; copy paths are listed
  in its `README.txt`.
