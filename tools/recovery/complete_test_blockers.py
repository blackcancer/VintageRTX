"""Complete blocker repairs after the first targeted MSTest execution.
Uses exact post-transform identities; does not alter real fixture pixels or rendering thresholds.
"""
from pathlib import Path
import hashlib

ROOT = Path(__file__).resolve().parents[2]
BASE = {
 'tests/VintageRTX.Test/PreflightSuite.cs':'3dfd8527f3c3c78c3b03dcd78398d4672625da7c',
 'tests/VintageRTX.Test/TestPaths.cs':'c913ea0b268b46f551c3b2958dfce6934768e4ca',
 'src/VintageRTX/Testing/RuntimeScenarioProbe.cs':'19d0cf336a8528bb4df5973f3ce817b94e56fcd0',
 'tests/VintageRTX.Test/RuntimeCoverageProbeWorldTests.cs':'304c2def532b98cbc3608aebe79c49fb79a2a61d',
 'tests/VintageRTX.Test/RuntimeImageValidatorReflectionWitnessTests.cs':'171cbc7e31220b49735f3fd6bd41cf2d0356b719',
}
def blob(data):
    return hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
def once(text, old, new):
    if text.count(old)!=1: raise ValueError(f'Expected one anchor ({text.count(old)} found): {old[:150]}')
    return text.replace(old,new,1)
sources={}
for path, expected in BASE.items():
    data=(ROOT/path).read_bytes()
    if blob(data)!=expected: raise ValueError('Unexpected source revision: '+path)
    sources[path]=data.decode('utf-8').replace('\r\n','\n')

p='tests/VintageRTX.Test/PreflightSuite.cs'
s=sources[p]
s=once(s, '''        Assert(shader.Contains("consistentNeighbours >= 2", StringComparison.Ordinal)
            && shader.Contains("conservative G-buffer dilation", StringComparison.Ordinal),
            "one-pixel opaque G-buffer seam repair missing");''', '''        // The old two-neighbour dilation invented receivers at silhouettes. The replacement
        // preserves missing geometry and resolves shadow visibility against the true receiver.
        // Executable GPU witnesses live in tests/feedback/test_feedback_contracts.py.
        Assert(!shader.Contains("consistentNeighbours >= 2", StringComparison.Ordinal)
            && !shader.Contains("geometryWasRepaired = true", StringComparison.Ordinal)
            && shader.Contains("readGBufferTexel(gPosition, uv)", StringComparison.Ordinal)
            && shader.Contains("readGBufferTexel(gNormal, uv)", StringComparison.Ordinal)
            && shader.Contains("resolveSurfaceShadow(position, normal,", StringComparison.Ordinal)
            && shader.Contains("float planeError = abs(dot(geometricViewNormal, position - centerPosition))", StringComparison.Ordinal)
            && shader.Contains("traceRawPointShadowVisibilities(worldPosition, worldNormal, pointA, pointB)", StringComparison.Ordinal),
            "exact receiver geometry or depth-guided shadow reconstruction missing");''')
s=once(s, '''            && displayShader.Contains("if (!hasGeometry && deferredGeometryRequired && shadowPass == 0)", StringComparison.Ordinal),''', '''            && displayShader.Contains("position = readGBufferTexel(gPosition, uv).xyz", StringComparison.Ordinal)
            && displayShader.Contains("encodedNormalRoughness = readGBufferTexel(gNormal, uv)", StringComparison.Ordinal)
            && displayShader.Contains("if (debugView == 0 && !hasGeometry)", StringComparison.Ordinal),''')
sources[p]=s

p='tests/VintageRTX.Test/TestPaths.cs'
s=sources[p]
s=once(s, '''        if (!File.Exists(Path.Combine(root, "Vintagestory.exe")))
        {
            throw new FileNotFoundException("Vintagestory.exe was not found.", Path.Combine(root, "Vintagestory.exe"));
        }''', '''        // Asset/assembly tests use the actual client on both Windows and Linux. Requiring
        // a Windows .exe on a Linux client prevented language/bootstrap tests from executing.
        bool hasClient = OperatingSystem.IsWindows()
            ? File.Exists(Path.Combine(root, "Vintagestory.exe"))
            : File.Exists(Path.Combine(root, "Vintagestory"))
                || File.Exists(Path.Combine(root, "Vintagestory.dll"));
        if (!hasClient
            || !File.Exists(Path.Combine(root, "VintagestoryAPI.dll"))
            || !Directory.Exists(Path.Combine(root, "assets")))
        {
            throw new DirectoryNotFoundException(
                $"A complete Vintage Story client (launcher, API and assets) was not found below '{root}'.");
        }''')
sources[p]=s

p='src/VintageRTX/Testing/RuntimeScenarioProbe.cs'
s=sources[p]
s=once(s, '''        double shadowHorizontal = Math.Sqrt(shadowX * shadowX + shadowZ * shadowZ);
        if (shadowHorizontal > 0.001)''', '''        double shadowHorizontal = Math.Sqrt(shadowX * shadowX + shadowZ * shadowZ);
        if (shadowHorizontal <= 0.000001)
        {
            // The exterior-roof scenario requires an exposed, horizontally projected witness.
            // A vertical sun casts underneath the roof, not onto that exterior witness. Reject
            // this unsuitable scenario setup rather than inventing a distant shadow target.
            api.Logger.Error("[VintageRTX.Test] Exterior roof camera rejected: vertical sun has no exposed horizontal shadow witness.");
            return false;
        }
        if (shadowHorizontal > 0.001)''')
sources[p]=s

p='tests/VintageRTX.Test/RuntimeCoverageProbeWorldTests.cs'
s=sources[p]
s=once(s, '''        Assert.IsTrue((bool)Invoke(verticalProbe, "TryApplyExteriorRoofCamera")!);
        verticalProbe.Dispose();''', '''        Assert.IsFalse((bool)Invoke(verticalProbe, "TryApplyExteriorRoofCamera")!,
            "A vertical solar ray cannot supply the exposed horizontal witness required by this scenario.");
        Assert.IsFalse(GetField<bool>(verticalProbe, "exteriorPositionLocked"));
        Assert.IsFalse(verticalSun.ChatMessages.Any(static text => text.StartsWith("/tp =", StringComparison.Ordinal)));
        Assert.IsTrue(verticalSun.Logs.Any(static entry => entry.Message.Contains(
            "vertical sun has no exposed horizontal shadow witness", StringComparison.Ordinal)));
        Assert.AreEqual(0.0, RuntimeScenarioProbe.CalculateProjectedSunShadowDistance(
            83.0, 79.0, verticalSun.SunDirection), 0.0);
        verticalProbe.Dispose();''')
s=once(s, '''    /// Verifies the exterior Roof Camera Covers Low Sun No Candidate And Open Ground Success regression contract against deterministic fixture data.''', '''    /// Verifies usable exterior shadow witnesses and rejects low/vertical sun or missing terrain.
    /// The historical vertical-sun success assertion contradicted the exposed-ground contract.''')
sources[p]=s

p='tests/VintageRTX.Test/RuntimeImageValidatorReflectionWitnessTests.cs'
s=sources[p]
s=once(s, '''        using SKBitmap bitmap = SKBitmap.Decode(path)
            ?? throw new InvalidDataException($"Could not decode real entity-mirror fixture '{path}'.");''', '''        using SKBitmap bitmap = LoadRealMirrorFixture(path);''')
anchor='    /// <summary>Accepts a compact lower-frame local body alongside the two remote witnesses.</summary>'
addition='''    /// <summary>Preserves the real fixture through Unicode filesystem paths without native path decoding.</summary>
    [TestMethod]
    public void RealFixtureSurvivesUnicodePathAndRejectsChangedBytes()
    {
        string source = Path.Combine(TestPaths.FindRepositoryRoot(), "tests", "VintageRTX.Test",
            "Fixtures", "entity-mirror-local-body-real.png");
        string directory = Path.Combine(Path.GetTempPath(), "VintageRTX-Développement-é-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] data = File.ReadAllBytes(source);
            string path = Path.Combine(directory, "réflexion-corps.png");
            File.WriteAllBytes(path, data);
            using (SKBitmap decoded = LoadRealMirrorFixture(path))
            {
                Assert.AreEqual(960, decoded.Width);
                Assert.AreEqual(505, decoded.Height);
                var components = RuntimeImageValidator.AssessEntityMirrorComponents(decoded);
                Assert.AreEqual(3, components.Count);
                Assert.AreEqual(2404, components[0].Area);
            }
            data[0] ^= 1;
            File.WriteAllBytes(path, data);
            InvalidDataException error = Assert.ThrowsException<InvalidDataException>(() =>
            {
                using SKBitmap rejected = LoadRealMirrorFixture(path);
            });
            StringAssert.Contains(error.Message, "SHA256");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Loads verified real capture bytes via managed Unicode file IO before Skia decoding.</summary>
    /// <param name="path">Path of the unchanged real mirror PNG.</param>
    /// <returns>Owned bitmap with the exact captured pixels.</returns>
    private static SKBitmap LoadRealMirrorFixture(string path)
    {
        const string expected = "40CBFF450C6F9A8D8B8082F2CC712A0D4F168E9AB5A08A364F59E8FA8AA8DD3E";
        byte[] data = File.ReadAllBytes(path);
        string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Real mirror fixture changed: '{path}', bytes={data.Length}, SHA256={actual}, expected={expected}.");
        return SKBitmap.Decode(data)
            ?? throw new InvalidDataException($"Could not decode verified real mirror PNG bytes: '{path}', SHA256={actual}.");
    }

'''
s=once(s,anchor,addition+anchor)
sources[p]=s

for path, content in sources.items():
    (ROOT/path).write_text(content,encoding='utf-8',newline='\n')
    print('UPDATED',path,blob(content.encode()))
