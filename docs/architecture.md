# Rendering architecture

## Product direction

VintageRTX targets a substantial but coherent evolution of Vintage Story's image rather than a collection of unrelated filters. The renderer remains client-only and preserves the game simulation, assets, and multiplayer protocol.

The visual reference is Minecraft RTX-class rendering: physically credible sun and emissive lighting, soft ray-traced shadows, diffuse color bounce, reflective and rough materials, water/glass transmission, and stable temporal reconstruction. Matching that class of image requires scene-space voxel tracing; screen-space effects alone are explicitly insufficient.

## Why a hybrid renderer

Vintage Story 1.22 uses an OpenGL raster pipeline and already exposes color, view-space position, normal, depth, glow, SSAO, and shadow data through its render pipeline. Those buffers make screen-space ray effects practical today. Full path tracing needs additional scene data that the game does not expose as a ready-to-trace acceleration structure.

The implementation is therefore staged:

1. **Display foundation** — full-screen pass, configuration, capability reporting, resize-safe GPU resources.
2. **Diagnostic screen-space lighting** — proves G-buffer access, ray projection, capture and performance measurement; this is a fallback, not the final renderer.
3. **Voxel scene bridge** — a bounded GPU representation of nearby chunks and PBR material properties, updated incrementally.
4. **Hybrid ray tracing** — voxel DDA rays for sun visibility, emissive lighting, diffuse bounce and specular reflection, with the raster scene as primary visibility.
5. **Temporal reconstruction** — motion-aware history, rejection, variance clipping and edge-aware denoising.
6. **Progressive path-traced photo mode** — multiple diffuse/specular bounces when the camera is stationary.

## Final acceptance gates

- Outdoor, indoor, cave, water, emissive, sunrise/sunset and moving-camera reference captures.
- A visibly material improvement over vanilla rather than a global color-grade difference.
- Stable frame pacing with reported average FPS, 1% low, jitter and isolated VintageRTX GPU time.
- A configurable GPU-time budget with slow hysteretic quality adaptation and bounded average/1%-low regressions.
- Public-API-first integration; every unavoidable private compatibility hook is version-gated, fail-closed and covered by an in-game regression test. No persistent OpenGL state corruption.

## Non-goals

- Marketing ordinary post-processing as hardware ray tracing.
- Replacing the entire renderer before a measurable vertical slice works in-game.
- Depending on private engine internals when the public API provides a stable path, or adding an undocumented private hook without a contained compatibility contract.
- Mutating global OpenGL state without restoring it.

## API boundary

The public API at <https://apidocs.vintagestory.at/> is authoritative for game integration. `ICoreClientAPI`, `IClientEventAPI`, `IRenderAPI`, `IShaderAPI`, `IShaderProgram`, and documented framebuffer types form the primary engine boundary.

Direct OpenGL is isolated inside rendering backends and is permitted only when the public API does not expose the required GPU operation. Vintage Story 1.22.7 also lacks public interception points for reserved sidecar discovery, exact world/entity reflection replay, first-person/world-body separation, and projectile pre/post-collision motion. Those four operations use the explicitly documented, version-gated Harmony boundaries in [api-contract.md](api-contract.md); they resolve private types by name, retain engine semantics, unpatch deterministically, and disable the affected feature when the exact contract is unavailable.

## Current vertical slice

`FilmicDisplayRenderer` runs at `EnumRenderStage.AfterBlit`, after the world image reaches the default framebuffer and before orthographic UI rendering. It copies the scene into an owned texture, reads the normal and camera-space position attachments from the public `Primary` framebuffer reference, and renders a full-screen triangle.

The fragment shader casts a coherent, globally rotated hemisphere pattern rather than independent per-pixel noise. Valid stationary history is accumulated over eight phases, clipped to local variance, and reconstructed with a depth/normal/source-guided cross-bilateral filter. Camera movement rejects history and falls back to a coherent single-frame result, avoiding the former point-cloud appearance without smearing silhouettes.

`VoxelScene` incrementally maintains a nearby 64 x 48 x 64 material volume plus 4x sub-voxel occupancy. It uses default tesselated meshes, collision/selection fallbacks, texture alpha tests, and per-light 16-cubed caster masks. This lets lantern cages cast through their cut-outs while transparent glass is ignored, preserves non-standard silhouettes such as anvils, extends sun visibility to 64 blocks, and includes emissive or held point lights. Public `PointLights3` data is transformed from the engine's camera space through the inverse `CameraMatrixOrigin` before any world-space shadow or bounce trace.

Static emitters also seed an RGB irradiance cache in the same 64 x 48 x 64 clipmap. Eighteen bounded propagation iterations diffuse energy only through empty/fluid cells; ordinary and metallic voxels stop propagation, so walls cannot leak light. Square-root HDR encoding preserves dark precision, and one linearly filtered 3D lookup reconstructs smooth low-frequency multi-bounce energy in the fragment pass. The existing coherent voxel ray remains as a lower-energy detail path and covers dynamic held lights; blocked direct energy is diagnostic only and is never recycled as ambient illumination.

Point-light penumbrae use a coherent Vogel disk. The sampling phase stays fixed whenever camera motion invalidates temporal history, eliminating unfiltered point-cloud sparkle; only valid stationary history rotates through the global phases. High quality uses four spatial samples, while the performance tier retains one stable sample and sheds secondary full-screen traces before sacrificing voxel shadows.

A bounded voxel diffuse pass traces up to two coherent secondary rays, finds off-screen material, checks its orientation/range and traces visibility back to the closest point light. The result supplies high-frequency and dynamic-light detail on top of the stable irradiance cache and remains exposed separately through the `bounce` diagnostic. The performance tier preserves one real bounce ray in normal gameplay; surface/visibility traversal is bounded to 5/4 steps there, 7/6 in balanced mode and 8/8 in high mode.

Screen-space reflections march against the position buffer for local hits. A bounded voxel DDA fallback continues specular rays into off-screen scene data and shades the first material hit from sun, ambient and nearby point lights. Transparent water, which does not write its own G-buffer position, is recognized by a 64 x 64 fluid-surface-height map built alongside the material volume. One spatial lookup identifies the actual X/Z water column instead of probing 7 to 15 3D voxels for every matte pixel; unrelated fluid elsewhere in the clipmap cannot activate the expensive water path. Missing sky/distant hits use a spatially filtered planar fallback mirrored about the projected horizontal horizon rather than the framebuffer centre. Coplanar self-intersections are rejected, and a water-specific dielectric response makes the reflection readable without making rough solid blocks glossy.

The pass exposes final, normal, position, screen-space lighting, combined reflection, off-screen voxel reflection, voxel-albedo, voxel-bounce radiance, visibility, and shadow-mask diagnostics. If the expected attachments do not exist, ray lighting is disabled while the display grade remains active.

## Measured acceptance status

- Non-standard geometry/lantern Release acceptance: the continuous cage/non-cubic shadow covers 21.7% of the frame (mean 0.393, spatial deviation 0.241); isolated highlights and thin leaks are both 0.001%. The stabilized 60 Hz A/B/A pass measured 60.0 FPS and 58.9 FPS 1% low with VintageRTX versus 60.0/58.0 baseline, at 1.78 ms VintageRTX GPU time.
- Water reflection Release acceptance: the combined diagnostic covers 5.3% of the real lake view and off-screen voxel reflection covers 16.7%. The calibrated exterior changes 10.0% of pixels upward and rejects double daylight; the stabilized pass measured 60.0/59.3 FPS versus 60.0/54.5 baseline, at 1.53 ms GPU time.
- Cave diffuse-bounce Release acceptance with the same one-ray performance path used in normal gameplay: 24.2% localized coverage, 0.012 spatial deviation and 24.2% warm contribution. The stabilized pass measured 60.0/46.9 FPS versus 60.0/48.8 baseline, at 1.76 ms GPU time.
- All three acceptance runs explicitly loaded `bin/Release/Mods`; the runtime harness now selects the mod build configuration that matches its own binary instead of always loading Debug.
- Moving-camera, sunrise, exterior-roof, resize/reload, PBR manifest, held-light and geometry scenarios are automated in `VintageRTX.Test`.
- Progressive multi-bounce photo mode remains a future gate; the current real-time renderer is hybrid raster/irradiance-cache/voxel/SSR, not hardware path tracing.

## Versioned G-buffer contract

For Vintage Story 1.22.7, the shipped world fragment shaders write `outColor`, `outGlow`, `outGNormal`, and `outGPosition` to locations 0 through 3 of `EnumFrameBuffer.Primary`. VintageRTX resolves only the documented `FrameBufferRef.ColorTextureIds` array and validates the attachment count and texture identifiers at runtime.

The attachment meaning is therefore a versioned game-asset contract rather than a promise made by `FrameBufferRef` itself. On an incompatible game version, the safe fallback is intentional.
