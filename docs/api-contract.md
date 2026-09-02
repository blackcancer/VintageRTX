# Vintage Story API contract

## Source of truth

All Vintage Story integration must be designed and reviewed against the public documentation at <https://apidocs.vintagestory.at/>.

The DLL, XML documentation, and shipped shaders from the installed Vintage Story 1.22.7 build verify the exact local binary contract. Public interfaces remain the default boundary. Where 1.22.7 exposes no callback for an essential rendering operation, the implementation may use a narrowly scoped, version-gated compatibility hook; every such exception is listed below and must fail closed.

## Approved public surface

| Responsibility | Public API |
| --- | --- |
| Client entry point | [`ICoreClientAPI`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.ICoreClientAPI.html) |
| Per-frame registration | [`IClientEventAPI.RegisterRenderer`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IClientEventAPI.html) |
| Renderer lifecycle | [`IRenderer`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IRenderer.html) |
| Render stages | [`EnumRenderStage`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.EnumRenderStage.html) |
| Frame size and game framebuffers | [`IRenderAPI`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IRenderAPI.html) |
| Deterministic test camera | [`IClientPlayer`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IClientPlayer.html) |
| Named engine framebuffer slots | [`EnumFrameBuffer`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.EnumFrameBuffer.html) |
| Framebuffer texture identifiers | [`FrameBufferRef`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.FrameBufferRef.html) |
| Shader creation and registration | [`IShaderAPI`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IShaderAPI.html) |
| Shader uniforms and textures | [`IShaderProgram`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IShaderProgram.html) |
| Chunk/block sampling | [`IBlockAccessor`](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IBlockAccessor.html) |
| Dynamic light interface | [`IPointLight`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IPointLight.html) |
| Engine light/camera uniforms | [`DefaultShaderUniforms`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.DefaultShaderUniforms.html) |
| Configuration persistence | [`ICoreAPICommon`](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.ICoreAPICommon.html) |

## Direct OpenGL rule

Vintage Story exposes rendering as OpenGL, but the public API does not wrap every GPU command. A direct OpenGL call is acceptable only when all of the following are true:

1. no documented API operation provides the required behavior;
2. the call is isolated in a rendering backend;
3. it executes on the render thread through an `IRenderer` callback;
4. every modified OpenGL state is captured and restored;
5. capability support is checked through `IShaderAPI` where applicable;
6. failure disables only the affected effect and is logged without crashing the game.

The display, voxel, PBR-atlas, liquid-surface, and reflection backends use direct OpenGL for owned textures/framebuffers, copies, uploads, image bindings, synchronization, and full-screen draws that have no complete public wrapper. Registration, staging, dimensions, shader creation, shader compilation, uniforms, configuration, and lifecycle use the documented Vintage Story API. Every backend captures/restores the engine state it changes and disables itself when its versioned contract cannot be proven.

Attachment indices are checked against the shaders shipped with the supported game version and validated at runtime. They are not inferred from undocumented client implementation types.

## Version-gated compatibility exceptions

VintageRTX 1.22.7 currently owns four contained Harmony boundaries:

| Boundary | Missing public capability | Containment |
| --- | --- | --- |
| `PbrAssetDiscoveryPatch` | Filter `_n`, `_r`, `_m`, and `_e` sidecars before wildcard-expanded block/item/entity atlas registration | Exact 1.22.7 asset/atlas methods, guarded transpiler/postfix, suffix-only behavior, deterministic teardown |
| `EntityMirrorGeometryReplayPatch` | Replay terrain and entity geometry into the planar reflection source at the engine's real opaque stages | Named render-stage methods, re-entry guards, owned framebuffer validation, deterministic teardown |
| `FirstPersonReflectionCapturePatch` | Separate the local world-body replay from the first-person arm/held-item overlay | Exact player-renderer methods/fields, identity checks, re-entry guards, safe no-reflection fallback |
| `ProjectileLiquidCollisionPatch` | Observe pre/post-collision motion for every `IProjectile`, including arrows and ricochets | Base projectile callback only, detached samples, original exceptions and engine semantics preserved |

Type names such as `Vintagestory.Client.NoObf.SystemRenderEntities` are resolved by name through Harmony and are not compile-time assembly references. If a target or field no longer matches, that feature is disabled and logged instead of patching an approximate method.

The following remain excluded:

- an unlisted or untested private-client hook;
- a patch that changes gameplay or multiplayer protocol semantics;
- persistent modification of global OpenGL state;
- framebuffer assumptions that are not checked against both the installed shaders and runtime attachments.

Any future exception requires a documented API-first alternative analysis, a contained compatibility layer, unit coverage, and an in-game failure-mode test.
