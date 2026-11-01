# EKEditorUIKit

A small **editor-only UI toolkit** for Flax Engine custom tool windows: `FlatButton`, `TextLabel`,
`KeyValueRow`, `LabeledCheckBox`, `ListRow`, `SectionHeader`, `StatusBar`, and a shared `Theme`
(colors, spacing, fonts) that those widgets read from. Scrolling and plain layout panels are the
engine's own `FlaxEngine.GUI.Panel` (with `ScrollBars`), not part of this kit.

It exists because Flax's editor GUI API is a low-level immediate-mode surface — building a real tool
window means writing your own widgets on top of it. This kit is that layer.

## License

MIT — Copyright (c) 2026 envision3d. See the repository root [`LICENSE`](../LICENSE).

## How it is used

It is a **sibling Flax plugin project** (`EKEditorUIKit.flaxproj`, with its own
`EKEditorUIKitTarget` / `EKEditorUIKitEditorTarget`) vendored inside the
[FlaxAssetManager](https://github.com/envision3d/FlaxAssetManager) plugin folder and referenced from
`FlaxAssetManager.flaxproj`:

```json
{ "Name": "$(ProjectPath)/EKEditorUIKit/EKEditorUIKit.flaxproj" }
```

`$(ProjectPath)` resolves relative to the referencing plugin's own folder, so cloning
FlaxAssetManager gives you a self-contained tree — no separate checkout, no path setup.

`FlaxAssetManager` is currently its only consumer, and the kit is evolving with that window's needs
(no public API stability promises, no theming system beyond `Theme`). Extracting it into a standalone
UI library plugin is a plausible future step; nothing in the plugin depends on that happening.
