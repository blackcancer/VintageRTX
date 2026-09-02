# VintageRTX

VintageRTX is a client-side rendering research mod for Vintage Story 1.22.x.

The public [Vintage Story API documentation](https://apidocs.vintagestory.at/) is the source of truth for engine integration. VintageRTX uses the documented API wherever it exposes the required lifecycle and data. A small, version-gated Harmony compatibility layer is nevertheless required for 1.22.7 where no public callback exists: excluding reserved PBR sidecars from albedo atlas discovery, replaying world/entity geometry into the reflection source, suppressing the incompatible first-person replay, and observing projectile/liquid contacts. These hooks are isolated, fail closed, and are covered by runtime tests; see [docs/api-contract.md](docs/api-contract.md).

The project is being rebuilt from scratch around a hybrid renderer:

1. a reliable full-screen render pass and image-quality controls;
2. screen-space reflections and indirect lighting using Vintage Story's G-buffer;
3. temporal accumulation and denoising;
4. voxel-aware ray traversal for off-screen lighting;
5. an optional progressive path-traced photo mode.

The current milestone is a validated hybrid vertical slice. It combines coherent screen-space indirect lighting, temporal reconstruction with variance clipping and cross-bilateral filtering, a nearby voxel scene for sun/emissive visibility, an occlusion-aware linearly filtered irradiance cache plus bounded detail bounce, texture-aware non-cubic casters, denoised soft point shadows, authored PBR/normal-map manifests, dynamic held lights, conductor-aware BRDF response, and screen-space plus off-screen voxel reflections with a spatially classified water fallback. Sky-visible daylight preserves the engine's exposed albedo and applies the traced sun-shadow delta separately, avoiding double-lit foliage. If the G-buffer contract is unavailable, the renderer safely falls back to display grading.

See [docs/api-contract.md](docs/api-contract.md) for the supported API surface and the rule governing direct OpenGL calls.

## Requirements

- Vintage Story 1.22.7
- .NET 10 SDK
- Visual Studio 2026 or another .NET 10-compatible IDE

Set `VINTAGE_STORY` to the game installation directory. The build also tolerates the existing local setup where this variable points to the installation's `assets` directory.

## Build

```powershell
dotnet build .\VintageRTX.sln -c Debug
```

The development mod is emitted to `src/VintageRTX/bin/<Configuration>/Mods/vintagertx`.

The complete offline-generated Vintage Story 1.22.7 material set is committed as normal assets under `src/VintageRTX/assets/game/textures`: 9,582 `_n`, 9,582 `_r`, 9,582 `_m`, and 9,582 `_e` sidecars, with no copied albedo. It covers the universal, Creative, and Survival texture origins after applying the vanilla `game -> creative -> survival` overlay order, including blocks, entities, items, environment, and legacy textures. A Release build only copies this asset tree and its manifest into the mod; it does not regenerate textures or deploy a private PBR archive. Exact suffix matching filters reserved sidecars immediately after Vintage Story expands `CompositeTexture` wildcards and before block, item, entity, or shape albedo registration. The files remain ordinary assets available to VintageRTX and to later mods through the same adjacent asset paths.

The default Visual Studio launch profile opens the representative `foggy village world` save directly with the documented `--openWorld` client argument. The automated harness applies `/gamemode 2`, freezes the requested hour, clears the weather, and captures deterministic A/B/debug views. `Vintage Story Client (quick creative)` remains available for fast smoke tests, and `Vintage Story Client (menu)` keeps the normal main-menu startup.

## In-game commands

- `/vrtx status`
- `/vrtx toggle`
- `/vrtx reload`
- `/vrtx lighting`
- `/vrtx voxel`
- `/vrtx capture`
- `/vrtx debug final|normal|position|lighting|reflection|voxelreflection|voxel|bounce|visibility|shadowmask|components`
- `/vrtx preset neutral|cinematic|vivid`
- `/vrtx profile performance|balanced|quality|ultra|extreme|cinematic|custom`

The persistent hardware profile is independent from the artistic `preset`: `performance` locks the minimum full-frame transport tier, `balanced` may move between balanced and performance, `quality` retains the complete adaptive range, and `ultra` fixes the original high-end spatial quality. `extreme` targets recent flagship GPUs with six scene-space rays, denser soft shadows, three diffuse bounce rays and longer off-screen reflections. `cinematic` exposes the native shader maxima—eight scene-space rays, four diffuse bounce rays, eight point-shadow samples, 48-block reflections and eight simultaneous voxel lights—and deliberately prioritizes image fidelity over interactive frame rate. The sun range remains 96 blocks in all three fixed profiles because it is bounded by the current conservative clipmap rather than by GPU speed. Every authored profile keeps PBR materials, liquids, temporal accumulation, multiple-source shadows, screen-space reflections and voxel reflections enabled. `custom` preserves manually edited values. The generated schema-v13 `vintagertx.json` also exposes the selected profile, GPU-time budget, slow hysteretic adaptive quality, screen-space and voxel ray budgets, off-screen voxel reflections, sun-shadow range, point-light softness/bounce, reflection distance/strength, transport-dominant relighting, indirect/sky/emissive strengths, and contact-shadow strength. Changes can be applied with `/vrtx reload`.

## Automated validation

For shader and material iteration without loading Vintage Story at all, use the standalone OpenGL laboratory:

```powershell
dotnet run --project .\tools\VintageRTX.RenderLab\VintageRTX.RenderLab.csproj -c Release
```

`VintageRTX.RenderLab` creates an invisible OpenGL 4.3 context and runs the production `DisplayShaderSource` against a synthetic G-buffer plus the exact 2D/3D texture contract used in game. Its fixed fixture contains a lantern with an opaque cage and transparent glass opening, a non-cubic anvil, crossed-plane vegetation, polished/brick receivers, a fluid surface, a long-range sun clipmap and authored PBR normals. It writes source/final/normal/material/reflection/voxel-reflection/water/shadow images, explicit `pbr-response` and `pbr-classes` diagnostics, quantitative per-material separation, and per-frame GPU timing under `tests/artifacts/standalone`. It does not launch `Vintagestory.exe`, load mods, create a server, generate chunks or touch a save.

This autonomous scene is the primary shader-development loop. The in-game `render-lab` below remains the integration gate for Vintage Story's real G-buffer, tessellation, texture atlas and public API lifecycle.

`VintageRTX.Test` runs a preflight suite before every real client scenario and stores logs plus A/B/debug captures under `tests/artifacts/runtime`. Debug test binaries load the Debug mod and Release test binaries load the Release mod, so a scenario cannot silently validate a stale configuration.

```powershell
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -- list
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -- runtime render-lab
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -- runtime render-lab-performance
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -- runtime render-lab-balanced
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -- runtime render-lab-quality
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -- runtime render-lab-ultra
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -- runtime render-lab-extreme
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -- runtime render-lab-cinematic
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -- runtime lantern-night
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -- runtime water-reflection
dotnet run --project .\tests\VintageRTX.Test\VintageRTX.Test.csproj -- runtime cave-interior
```

`render-lab` creates a fresh normal `preset-surviveandbuild` world under a disposable `%TEMP%` data path, immediately applies `/gamemode 2`, locks a clear noon reference, then builds an elevated fixed room with a lantern, anvil, crossed-plane grass, water and exposed/sheltered PBR receivers. Using a normal world preset avoids the special always-bright creative-building environment; the cleared camera corridor and neutral stage prevent generated vegetation from contaminating comparisons. The covered half keeps the local lantern and cage shadow measurable while the exposed half provides stable PBR, normal and water references. The visual run records final/normal/material/reflection/voxel-reflection/water/shadow evidence under the `Quality` profile and skips the long benchmark. The four performance-oriented suffixed scenarios rebuild the exact same scene and run a shortened stabilized A/B/A window (15 s minimum world warm-up, 90-frame transitions and 300-frame samples) to gate average FPS, 1 % low and GPU time against the archived `Performance`, `Balanced`, `Quality`, or `Ultra` configuration. `render-lab-extreme`, `render-lab-cinematic`, and the representative full-world `water-reflection` scenario are capture-only visual gates: low FPS is accepted, but shader compilation, temporal stability, geometry, PBR, shadows and both reflection paths must still pass. `water-reflection` therefore runs the `Cinematic` profile, waits through the 1750-tick fixed-entity checkpoint, then injects a real `game:thrownitem` stone ricochet and a real flint-arrow entry through `IProjectile`; completion requires the final and liquid-surface-field PNG pairs saved immediately after both events, all three physically scaled dropped-item impacts, and the final entity-mirror diagnostic, without applying any FPS, 1 % low, jitter, or GPU-time threshold. The benchmark cannot begin until every diagnostic PNG has reached disk and the GPU has settled, so capture encoding never contaminates an interactive-profile effect window. No mode copies user saves or the data-path Mods directory. Vintage Story's built-in install/global mod search still applies because the documented `--addModPath` option is additive; full-world and stress scenarios remain the final representativeness gates.

Every profiled runtime artifact preserves `Config/Seeded/vintagertx.json`, `Config/Final/vintagertx.json`, and `Config/profile-evidence.json`. The manifest records the requested profile, the effective tier used by the native final capture and benchmark, plus canonical configuration fingerprints; the scenario fails if any profile-owned budget changes or an impermissible adaptive tier is observed.

The scenario catalog also covers the reference room, lantern night, held light, a dark cave with off-screen diffuse bounce, many-light stress, long exterior roof shadows, sunrise, moving camera, water reflections, non-standard geometry, third-party PBR, and resize/shader reload.

## Historical prototype

The 2025 prototype is preserved read-only in `legacy/prototype-2025`. It is not referenced by the new solution or build.
