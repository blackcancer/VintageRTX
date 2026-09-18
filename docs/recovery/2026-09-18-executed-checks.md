# Executed checks — first renderer recovery milestone

Renderer commit: `7e180ad4efc8d569c543d40367fc492afd90639a`.
Source preparation parent: `5cc02c0c286dfef05c48a88e5884dbe84551ff6f`.

The source publication job applied the hash-checked changes, executed tests, compiled the resulting source, then committed those same ordinary C#/GLSL files.

Evidence: https://github.com/blackcancer/VintageRTX/actions/runs/35400222171
Job: `105778186858`.

## Successful checks

- Complete production `display.vert` / `display.frag` OpenGL link under Mesa EGL.
- Production hierarchical ray traversal compared against exhaustive double-precision ray/AABB intersections for 256 reproducible random rays.
- Axis-parallel ray into a single thin occupied cell: distance and entry normal.
- Separate clear-interval, out-of-coverage and exhausted-budget outcomes.
- `dotnet build src/VintageRTX/VintageRTX.csproj -c Release` with .NET 10.0.401 and official Vintage Story 1.22.7 Linux client references.

The GLSL suite contains four test methods; the 256 rays are cases within one method, not 256 additional test methods.

## Not executed here

No in-world Vintage Story session, visual acceptance, hardware-GPU performance measurement, GUI interaction or full legacy MSTest/preflight suite was certified by these checks. Follow `2026-09-18-validation.md` on the development game installation.

The previous server-only CI build failed during test-package restoration; it did not constitute an in-game failure. The continuing CI now explicitly checks the mod project and new GLSL tests with client references. Full solution tests remain a separate qualification step.

The write-enabled one-shot publisher workflow was removed after successful source publication. Normal CI has read-only repository permissions and does not modify source. The recipe remains solely as an audit record, never as a build/runtime dependency.
