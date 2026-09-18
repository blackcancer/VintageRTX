# Renderer recovery — developer test branch

This is a source integration milestone, **not** a certified complete RTX release.
Base renderer: `ae9eb2b797b106a458850e441e56b457ed726c31`.
Branch: `dev/renderer-recovery-20260918`.

## Changes

- Mirror depth writers share one active projection and its matching inverse.
- Hierarchical voxel reflection/bounce returns the fine-cell entry distance and normal instead of the coarse block boundary and dominant ray axis.
- Hit, clear interval, unknown coverage and exhausted traversal budget are distinguished.
- SSGI directions 5–8 no longer repeat the first four. Samples are decoded before weighting; composition no longer decodes again.
- Reflected surfaces check sunlight and selected local-light visibility at the hit point.
- Metal F0 is bounded; legacy specular gains and incomplete albedo reconstruction remain.
- Native reflection/bounce diagnostics no longer increase their sampling budget.
- Non-finite configurable floats fall back to declared defaults.
- `/vrtx settings` or remappable **Ctrl+Alt+R** opens a native FR/EN settings dialog.
  It edits a detached draft; Apply persists a clone then publishes it. Cancel does not alter live settings.
  This panel is **not yet embedded in the original Graphics menu**.

## Compile against your actual game installation

```powershell
$env:VINTAGE_STORY = 'C:\Path\To\Vintagestory'
dotnet build .\src\VintageRTX\VintageRTX.csproj -c Release
```

Use Vintage Story **1.22.7**, .NET **10**, and the existing game references.
Output: `src/VintageRTX/bin/Release/Mods/vintagertx`. Avoid duplicate mod installations.
The full test solution needs its existing MSTest packages; repository NuGet.Config clears sources:

```powershell
dotnet restore .\VintageRTX.sln --source https://api.nuget.org/v3/index.json
dotnet build .\VintageRTX.sln -c Release --no-restore
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -c Release -- preflight
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -c Release -- runtime render-lab
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -c Release -- runtime water-reflection
```

The Linux/EGL tests in `tests/recovery` run actual production GLSL against an exhaustive ray/AABB oracle and link the full display shader. They are not game captures or GPU performance certification.

## Manual qualification

1. Separate test world; preserve old configuration. Verify GUI Cancel, Apply, reopening, persistence and GUI scale.
2. Start with Quality, adaptation off. Inspect still-water reflections with objects above and crossing the interface, oblique camera angles and both first-/third-person views.
3. Inspect anvil/fence/lantern reflections during lateral camera movement; geometry should follow occupied subcells rather than full block faces.
4. Hide a selected lamp behind a wall relative to the reflected object. Its direct reflection lighting must be occluded. Measure the cost of extra visibility rays.
5. Inspect a neutral room with SSGI on/off and eight directions. Linear accumulation changes brightness; do not hide differences through blanket material retuning.
6. Compare native diagnostic and final profile evidence. Old tests that freeze specific shader strings or inflated debug coverage require independent contract review, not arbitrary relaxed thresholds.
7. Resize, reload shaders and toggle the mod. Record commit, configuration, GPU/driver, build output and client logs with failures.

## Not completed by this milestone

Stable entity-emitter ownership, fully animated off-screen geometry, scalable multi-light integration, full albedo/BSDF replacement, moving-camera temporal reprojection, multiple mirror planes, native Graphics-menu embedding and progressive path tracing remain separate milestones. The former engine point-light path remains active; this is not complete entity-lighting support.

`tools/recovery` is a hash-checked one-shot source publication recipe. It is not used by the game, MSBuild or the shader loader. The published commit contains ordinary modified C#/GLSL files.
