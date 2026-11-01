# Compatibility probe

`FlaxAssetManager.Compat.csproj` compiles **every** source of this plugin (game module, editor module,
and the vendored `EKEditorUIKit`) against the C# API assembly of an **official Flax release**.

Why this exists: the plugin builds against whatever engine is on the machine, and an engine checkout can
be newer, older, or locally patched. A plugin that only compiles against a patched engine installs
broken for everyone else. This probe answers in seconds, without the editor:

```bash
dotnet build compat/FlaxAssetManager.Compat.csproj -c Development -p:EngineDir=/path/to/flax
```

`EngineDir` is an extracted official **editor** package containing:

* `Binaries/Editor/Win64/Development/FlaxEngine.CSharp.dll` — the managed API assembly, and
* `Source/Platforms/DotNet/Newtonsoft.Json.dll` — the engine's bundled JSON library.

`.github/workflows/compat.yml` runs exactly this against the current public release on every push and
once a week, so an engine bump shows up as a red build here rather than as a user bug report.

Scope and honest limits: this is a **compile** check against public API. It does not run the plugin, does
not test behaviour, and does not replace building the plugin through Flax in a real project:

```bash
FLAX=/path/to/FlaxEngine
"$FLAX/Binaries/Tools/Flax.Build" -project=<HostProject>.flaxproj -build -platform=Linux -configuration=Development
```

That second command compiles the plugin as the engine loads it (see `Cache/Intermediate/Log.txt` of the
host project for the compiled plugin assemblies). Note that `-targets=<PluginTarget>` selects targets for
project generation only, and `-buildtargets=<PluginTarget>` reports *Missing target* — plugin targets are
built as dependencies of the host project's targets, not as build targets of their own.
