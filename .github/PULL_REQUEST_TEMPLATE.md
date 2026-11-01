<!--
Keep this short: what changed, why, and how you checked it. Delete this comment block.
The full rules are in CONTRIBUTING.md; the short version is below.
-->

## What & why

One or two paragraphs. What is the defect or the gap, and why this shape of fix?

## How it was checked

- [ ] Compat build, zero warnings (CI runs this):
      `dotnet build compat/FlaxAssetManager.Compat.csproj -c Development -p:EngineDir=/path/to/flax -warnaserror`
- [ ] Opened the window in the editor: sources load, list filters, install, remove.
- [ ] Material generation (`FLAX_AM_E2E=1` runs the scripted install + material test in
      `Source/FlaxAssetManagerEditor/Core/Tests/VariantE2ETest.cs`), and the generated material
      opens in the material editor with the expected nodes.
- [ ] Closed the window mid-install / mid-preview: no console spam afterwards, no leaks.
- [ ] Docs in the same commit (README, CHANGELOG, docs/*, AGENTS.md file map).

## Notes for the reviewer

- Which of the safety rules this touches: path containment, install-record ownership, HTTPS-only,
  the archive entry-name rule, the clone staging/ownership rule, main-thread marshalling.
- Any new hard-coded Visject id? It needs a case in `MaterialGraphBuilder.ValidateRootInputBoxes` -
  CI proves ids compile, not that they still mean the same thing.
- User-visible behaviour change, and the migration story for existing installs (`Sources.json`,
  `.installed.json`, generated `M_<packId>.flax` assets).

## Decisions

Does this settle, narrow, or contradict an entry in `docs/DECISIONS.md` (D1-D15)? If it adds a
constraint the code depends on, it needs a D entry - name it in the PR body, then link the PR.

Closes #
