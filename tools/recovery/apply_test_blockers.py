"""Exact-base repair of reported startup/mirror/test-contract blockers.
One-shot development tool, never executed by MSBuild or by the game.
"""
from pathlib import Path
import hashlib
import re

ROOT = Path(__file__).resolve().parents[2]
BASE = {
 'src/VintageRTX/Rendering/EntityMirrorProjection.cs': '742518d6839bafe60a80173a48ec745b3b63dd6a',
 'src/VintageRTX/Rendering/FilmicDisplayRenderer.cs': '9a5131ba147c7c7271a07439f5177027c4964b2b',
 'src/VintageRTX/VintageRtxModSystem.cs': 'dc439963c22a1f0c6eeb28005af3203ba4b3e064',
 'src/VintageRTX/Configuration/GuiDialogVintageRtxSettings.cs': '8841fc5f92e06aafa4105aaf7501279330a92bae',
 'tests/VintageRTX.Test/RuntimeCoverageModSystemTests.cs': 'b899bb99f4dfefdbc37a6ea7ef84e93820701dc0',
 'tests/VintageRTX.Test/ShadowFilterPipelineTests.cs': 'a3afceed8f3dcf8da3e2ffbb3881c651630051e6',
 'tests/VintageRTX.Test/VegetationSunShadowRegressionTests.cs': '8c59ceca28b4aaf127b1615b0091eeefebbbc0bc',
 'tests/VintageRTX.Test/PreflightSuite.cs': '00f2adb4ced11d612220b74f1072745cf50d0ef1',
 'tests/VintageRTX.Test/VintageRTX.Test.csproj': 'd5aab882fd91a9cf2e313f6bef8a1097ee6d50b1',
}

def blob(data):
    return hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()

def once(s, old, new):
    if s.count(old) != 1: raise ValueError(f'Expected one anchor, found {s.count(old)}: {old[:120]}')
    return s.replace(old, new, 1)

def function_span(s, signature):
    a = s.index(signature)
    opening = s.index('{', a)
    depth, b = 1, opening + 1
    while depth:
        depth += (s[b] == '{') - (s[b] == '}')
        b += 1
    return a, b

sources = {}
for path, sha in BASE.items():
    data = (ROOT / path).read_bytes()
    if blob(data) != sha: raise ValueError('Unexpected source revision: ' + path)
    sources[path] = data.decode('utf-8').replace('\r\n', '\n')

p = 'src/VintageRTX/Rendering/EntityMirrorProjection.cs'
s = sources[p]
s = once(s, '''        return TryWriteObliqueProjection(
            projectionMatrix,
            clipX,
            clipY,
            clipZ,
            clipW,
            destination);''', '''        if (!TryWriteObliqueProjection(
            projectionMatrix, clipX, clipY, clipZ, clipW, destination)
            || Mat4d.Invert(inverseProjection, destination) is null
            || !IsFiniteMatrix(inverseProjection))
        {
            // Finite coefficients alone do not prove invertibility. In particular,
            // a non-perspective fixture can produce two linearly dependent rows.
            Array.Clear(destination);
            return false;
        }
        return true;''')
a, b = function_span(s, '    internal void Render(')
method = s[a:b]
method = once(method, 'internal void Render(', 'internal bool Render(')
gl_start = method.index('        EnsureSize(')
projection_start = method.index('        BuildReflectedViewMatrix(', gl_start)
gl_setup = method[gl_start:projection_start]
method = method[:gl_start] + method[projection_start:]
inv_start = method.index('        if (Mat4d.Invert(inverseProjectionScratch, obliqueProjectionMatrix) is null)')
inv_end = method.index('        if (mirrorProjectionReady)', inv_start)
method = method[:inv_start] + '''        if (!TryCreateDepthProjectionPair(obliqueProjectionMatrix,
            mirrorDepthProjectionMatrix, inverseMirrorDepthProjectionMatrix,
            inverseMirrorViewScratch, inverseProjectionScratch))
        {
            if (!clipProjectionFailureLogged)
            {
                api.Logger.Warning("[VintageRTX] Mirror skipped for this frame: no finite invertible depth projection pair.");
                clipProjectionFailureLogged = true;
            }
            return false;
        }
        // Replay and shader must use the same float-representable coefficients.
        Array.Copy(inverseMirrorViewScratch, obliqueProjectionMatrix, 16);
''' + gl_setup + method[inv_end:]
method = method[:-1] + '    return true;\n    }'
s = s[:a] + method + s[b:]
helper = '''    /// <summary>
    /// Pairs the actual float-representable projection with its inverse before any GL work.
    /// Invalid inputs clear both outputs; an identity inverse is never fabricated on failure.
    /// </summary>
    /// <param name="source">Read-only candidate projection.</param>
    /// <param name="forward">Published float projection, cleared on failure.</param>
    /// <param name="inverse">Published inverse, cleared on failure.</param>
    /// <param name="roundedScratch">Detached storage for the float-rounded projection.</param>
    /// <param name="inverseScratch">Detached inverse storage.</param>
    /// <returns>True only for a finite invertible float projection/inverse pair.</returns>
    internal static bool TryCreateDepthProjectionPair(double[] source, float[] forward, float[] inverse,
        double[] roundedScratch, double[] inverseScratch)
    {
        Array.Clear(forward);
        Array.Clear(inverse);
        if (source.Length < 16 || forward.Length < 16 || inverse.Length < 16
            || roundedScratch.Length < 16 || inverseScratch.Length < 16) return false;
        for (int index = 0; index < 16; index++)
        {
            float value = (float)source[index];
            if (!float.IsFinite(value)) return false;
            roundedScratch[index] = value;
        }
        if (Mat4d.Invert(inverseScratch, roundedScratch) is null) return false;
        for (int index = 0; index < 16; index++)
            if (!float.IsFinite((float)inverseScratch[index])) return false;
        for (int index = 0; index < 16; index++)
        {
            forward[index] = (float)roundedScratch[index];
            inverse[index] = (float)inverseScratch[index];
        }
        return true;
    }

'''
s = once(s, '    /// <summary>Projects the clean late-opaque entity delta', helper + '    /// <summary>Projects the clean late-opaque entity delta')
sources[p] = s

p = 'src/VintageRTX/Rendering/FilmicDisplayRenderer.cs'
s = sources[p]
a, b = function_span(s, '    private bool RenderEntityMirror(')
method = s[a:b]
method = once(method, '            entityMirrorProjection.Render(', '            return entityMirrorProjection.Render(')
method = once(method, '            return true;\n', '')
s = s[:a] + method + s[b:]
sources[p] = s

p = 'src/VintageRTX/VintageRtxModSystem.cs'
s = sources[p]
s = once(s, '''        settingsDialog = new GuiDialogVintageRtxSettings(clientApi, configStore, () => renderer.ResetFault());
        clientApi.Input.RegisterHotKey("vintagertx-settings", "VintageRTX settings",
            GlKeys.R, HotkeyType.GUIOrOtherControls, altPressed: true, ctrlPressed: true);
        clientApi.Input.SetHotKeyHandler("vintagertx-settings", _ =>
        {
            settingsDialog?.Toggle();
            return true;
        });''', '''        // The panel is created only on first use, not during renderer bootstrap.
        if (clientApi.Input is { } input)
        {
            input.RegisterHotKey("vintagertx-settings", "VintageRTX settings",
                GlKeys.R, HotkeyType.GUIOrOtherControls, altPressed: true, ctrlPressed: true);
            input.SetHotKeyHandler("vintagertx-settings", _ =>
            {
                ToggleSettings();
                return true;
            });
        }''')
s = once(s, '''                .HandleWith(_ =>
                {
                    settingsDialog?.Toggle();
                    return TextCommandResult.Success();
                })''', '''                .HandleWith(_ => ToggleSettings())''')
s = once(s, '        api?.Input.SetHotKeyHandler(', '        api?.Input?.SetHotKeyHandler(')
helper = '''    /// <summary>Opens settings only after the client GUI and renderer are available.</summary>
    /// <returns>A command error rather than a bootstrap fault when no GUI exists.</returns>
    private TextCommandResult ToggleSettings()
    {
        if (api?.Gui is null || configStore is null || renderer is null)
            return TextCommandResult.Error("VintageRTX settings require an initialized client GUI.");
        settingsDialog ??= new GuiDialogVintageRtxSettings(api, configStore, () => renderer?.ResetFault());
        settingsDialog.Toggle();
        return TextCommandResult.Success();
    }

'''
s = once(s, '    /// <summary>Builds one operational snapshot', helper + '    /// <summary>Builds one operational snapshot')
sources[p] = s
p = 'src/VintageRTX/Configuration/GuiDialogVintageRtxSettings.cs'
sources[p] = once(sources[p], '    internal void Toggle()', '    public override void Toggle()')

p = 'tests/VintageRTX.Test/RuntimeCoverageModSystemTests.cs'
s = sources[p]
s = once(s, '                "status", "toggle", "reload", "lighting", "voxel", "capture", "debug", "preset", "profile"',
    '                "settings", "status", "toggle", "reload", "lighting", "voxel", "capture", "debug", "preset", "profile"')
s = once(s, '            Assert.AreEqual(1, fallbackCaptureCalls);', '''            Assert.AreEqual(1, fallbackCaptureCalls);
            Assert.IsNull(GetPrivateField<object?>(system, "settingsDialog"), "Bootstrap must not allocate the GUI.");
            CollectionAssert.Contains(eventCalls, "RegisterHotKey");
            CollectionAssert.Contains(eventCalls, "SetHotKeyHandler");
            // This coordinator fixture deliberately has no GUI/GL context.
            AssertCommandFailed(commands["settings"](CreateCommandArguments()));''')
a, b = function_span(s, '    private static ICoreClientAPI CreateBootstrapApi(')
method = s[a:b]
method = once(method, '        ICoreClientAPI? api = null;', '''        IInputAPI input = RuntimeCoverageDispatchProxy.Create<IInputAPI>((method, _) =>
        {
            eventCalls.Add(method.Name);
            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        ICoreClientAPI? api = null;''')
method = once(method, '            "get_Event" => eventApi,', '            "get_Input" => input,\n            "get_Event" => eventApi,')
s = s[:a] + method + s[b:]
sources[p] = s

p = 'tests/VintageRTX.Test/VegetationSunShadowRegressionTests.cs'
sources[p] = once(sources[p], '            "display.frag"));', '            "display.frag")).ReplaceLineEndings("\\n");')
p = 'tests/VintageRTX.Test/PreflightSuite.cs'
s = sources[p]
a, b = function_span(s, '    private static void TestDisplayShaderContracts()')
method = s[a:b]
method = once(method, '            "VintageRTX")).Fragment;', '            "VintageRTX")).Fragment.ReplaceLineEndings("\\n");')
sources[p] = s[:a] + method + s[b:]

p = 'tests/VintageRTX.Test/ShadowFilterPipelineTests.cs'
s = sources[p]
a, b = function_span(s, '    public void VoxelShadowTraversalAdvancesEveryTiedAxis()')
s = s[:a] + '''    public void VoxelShadowTraversalAdvancesEveryTiedAxis()
    {
        string shader = ReadFragmentShader();
        // Inspect each live kernel, not the global number of copies of one old DDA spelling.
        foreach (string name in new[] { "float traceLightCasterVisibility(", "float traceVoxelVisibility(", "float traceSunClipmapVisibility(" })
        {
            string body = ShaderKernelBody(shader, name);
            foreach (string axis in new[] { "x", "y", "z" })
                StringAssert.Contains(body, $"if (sideDistance.{axis} <= traveled + 0.00001)");
            Assert.IsFalse(body.Contains("else if", StringComparison.Ordinal), name);
        }
        foreach (string name in new[] { "bool traceFineBlockHit(", "int traceVoxelSurface(" })
        {
            string body = ShaderKernelBody(shader, name);
            foreach (string axis in new[] { "x", "y", "z" })
                StringAssert.Contains(body, $"if (boundary.{axis} <= cellExit) cell.{axis} += stepDirection.{axis};");
            Assert.IsFalse(body.Contains("else if", StringComparison.Ordinal), name);
        }
    }

    /// <summary>Extracts a defined GLSL kernel, skipping a possible forward declaration.</summary>
    /// <param name="source">Shader source.</param><param name="signature">Exact function signature prefix.</param>
    /// <returns>The complete function body.</returns>
    private static string ShaderKernelBody(string source, string signature)
    {
        int start = -1;
        int opening;
        do
        {
            start = source.IndexOf(signature, start + 1, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, signature);
            opening = source.IndexOf('{', start);
            int semicolon = source.IndexOf(';', start);
            if (opening >= 0 && (semicolon < 0 || opening < semicolon)) break;
        } while (true);
        int depth = 1, end = opening + 1;
        while (depth > 0 && end < source.Length)
        {
            if (source[end] == '{') depth++;
            if (source[end] == '}') depth--;
            end++;
        }
        Assert.AreEqual(0, depth, signature);
        return source[opening..end];
    }''' + s[b:]
sources[p] = s

p = 'tests/VintageRTX.Test/VintageRTX.Test.csproj'
s = sources[p]
for name in ['libSkiaSharp.dll', 'glfw3.dll', 'e_sqlite3.dll']:
    old = f'    <None Include="$(VintageStoryPath)\\Lib\\{name}"'
    new = old + f' Condition="Exists(\'$(VintageStoryPath)/Lib/{name}\')"'
    s = once(s, old, new)
s = once(s, '</Project>', '''  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('Linux'))">
    <None Include="$(VintageStoryPath)/Lib/*.so*" Link="%(Filename)%(Extension)"
          CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>''')
sources[p] = s

for path, content in sources.items():
    (ROOT / path).write_text(content, encoding='utf-8', newline='\n')
    print('UPDATED', path, blob(content.encode()))
