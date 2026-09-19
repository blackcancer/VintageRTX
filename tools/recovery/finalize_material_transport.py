"""Finish the material migration with resource packaging and explicit replacement contracts.
Run only after implement_material_transport.py on its exact base. No visual threshold is edited.
"""
from pathlib import Path
import hashlib
ROOT = Path(__file__).resolve().parents[2]

def edit(path, old, new):
    p = ROOT / path
    s = p.read_text(encoding='utf-8')
    if s.count(old) != 1:
        raise ValueError(f'Expected one reviewed anchor in {path}: {old[:140]} ({s.count(old)})')
    p.write_text(s.replace(old, new, 1), encoding='utf-8', newline='\n')

fixture = ROOT / 'tests/VintageRTX.Test/Fixtures/entity-mirror-local-body-real.png'
expected = '40cbff450c6f9a8d8b8082f2cc712a0d4f168e9ab5a08a364f59e8fa8aa8dd3e'
if hashlib.sha256(fixture.read_bytes()).hexdigest() != expected:
    raise ValueError('Original real-game fixture is missing or changed; do not generate a substitute.')
p = 'tests/VintageRTX.Test/VintageRTX.Test.csproj'
edit(p, '</Project>', '''  <ItemGroup>
    <EmbeddedResource Include="Fixtures/entity-mirror-local-body-real.png"
                      LogicalName="VintageRTX.Test.Fixtures.entity-mirror-local-body-real.png" />
  </ItemGroup>
  <Target Name="RequireRealMirrorFixture" BeforeTargets="PrepareForBuild">
    <Error Condition="!Exists('$(MSBuildProjectDirectory)/Fixtures/entity-mirror-local-body-real.png')"
           Text="The original real-game mirror fixture is missing. Restore tests/VintageRTX.Test/Fixtures/entity-mirror-local-body-real.png from Git before building. No synthetic replacement is accepted." />
  </Target>
</Project>''')
p = 'tests/VintageRTX.Test/RuntimeImageValidatorReflectionWitnessTests.cs'
edit(p, '''        string path = Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "tests",
            "VintageRTX.Test",
            "Fixtures",
            "entity-mirror-local-body-real.png");
        using SKBitmap bitmap = LoadRealMirrorFixture(path);''', '''        using SKBitmap bitmap = DecodeRealMirrorFixture(ReadRealMirrorFixtureBytes(), "embedded real mirror");''')
edit(p, '''        string source = Path.Combine(TestPaths.FindRepositoryRoot(), "tests", "VintageRTX.Test",
            "Fixtures", "entity-mirror-local-body-real.png");
''', '')
edit(p, '            byte[] data = File.ReadAllBytes(source);', '            byte[] data = ReadRealMirrorFixtureBytes();')
edit(p, '''    private static SKBitmap LoadRealMirrorFixture(string path)
    {
        const string expected = "40CBFF450C6F9A8D8B8082F2CC712A0D4F168E9AB5A08A364F59E8FA8AA8DD3E";
        byte[] data = File.ReadAllBytes(path);''', '''    private static SKBitmap LoadRealMirrorFixture(string path) =>
        DecodeRealMirrorFixture(File.ReadAllBytes(path), path);

    /// <summary>Reads the original PNG from this exact test assembly, independent of deployment path.</summary>
    /// <returns>Detached original capture bytes; never synthesizes or downloads a substitute.</returns>
    private static byte[] ReadRealMirrorFixtureBytes()
    {
        const string name = "VintageRTX.Test.Fixtures.entity-mirror-local-body-real.png";
        using Stream stream = typeof(RuntimeImageValidatorReflectionWitnessTests).Assembly
            .GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Missing embedded real mirror fixture '{name}'. Rebuild the test project from a complete checkout.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Rejects changed capture bytes before native image decoding.</summary>
    /// <param name="data">Original embedded or managed-file bytes.</param>
    /// <param name="path">Identity used in actionable integrity diagnostics.</param>
    /// <returns>Owned bitmap containing the exact original capture.</returns>
    private static SKBitmap DecodeRealMirrorFixture(byte[] data, string path)
    {
        const string expected = "40CBFF450C6F9A8D8B8082F2CC712A0D4F168E9AB5A08A364F59E8FA8AA8DD3E";''')

p = 'tests/VintageRTX.Test/RuntimeCoverageModSystemTests.cs'
edit(p, '            Assert.AreEqual(10, eventCalls.Count(static name => name == "RegisterRenderer"));',
     '            Assert.AreEqual(11, eventCalls.Count(static name => name == "RegisterRenderer"));')
edit(p, '            Assert.AreEqual(10, eventCalls.Count(static name => name == "UnregisterRenderer"));',
     '            Assert.AreEqual(11, eventCalls.Count(static name => name == "UnregisterRenderer"));')
edit(p, '                "RegisterRenderer:vintagertx-pbr-terrain:Opaque",', '''                "RegisterRenderer:vintagertx-raw-albedo:Opaque",
                "RegisterRenderer:vintagertx-pbr-terrain:Opaque",''')

p = 'tests/VintageRTX.Test/PreflightSuite.cs'
edit(p, '''        Assert(shader.Contains("environmentAlignment", StringComparison.Ordinal)
            && shader.Contains("metallic * 3.80", StringComparison.Ordinal)
            && shader.Contains("localEnvironmentSpecular", StringComparison.Ordinal)
            && shader.Contains("0.035 + metallic * 0.460", StringComparison.Ordinal),
            "localized conductor highlight response missing");''', '''        // MaterialTransportTests executes GGX against an independent Smith reference and
        // an integrated white furnace. This guard only checks its production wiring.
        Assert(shader.Contains("materialFresnel(f0, vh)", StringComparison.Ordinal)
            && shader.Contains("vec3 directSpecularRadiance = voxelLighting.directSpecular", StringComparison.Ordinal)
            && shader.Contains("materialF0) * emissiveLightStrength", StringComparison.Ordinal)
            && shader.Contains("materialF0) * sunLightStrength", StringComparison.Ordinal)
            && !shader.Contains("metallic * 3.80", StringComparison.Ordinal)
            && !shader.Contains("localEnvironmentSpecular", StringComparison.Ordinal),
            "direct conductor lighting must use material Fresnel, not post-hoc metal gains or a diffuse-cache spotlight");''')
edit(p, '''        Assert(displayShader.Contains("vec3 localEnvironmentSpecular = vec3(0.0)", StringComparison.Ordinal)
            && displayShader.Contains("localEnvironmentSpecular = voxelLighting.irradianceCache", StringComparison.Ordinal)
            && displayShader.Contains("environmentLobe", StringComparison.Ordinal)
            && displayShader.Contains("* fresnelReflectance", StringComparison.Ordinal)
            && displayShader.Contains("* resolvedSpecularEligibility", StringComparison.Ordinal)
            && displayShader.Contains("* (1.0 - resolvedPbrRoughness * 0.65)", StringComparison.Ordinal)
            && displayShader.Contains("* surfaceReliability", StringComparison.Ordinal)
            && displayShader.Contains("reflectedRadiance += localEnvironmentSpecular", StringComparison.Ordinal),
            "low-cost reflection tiers can silently make valid metallic/roughness maps appear matte");''', '''        Assert(displayShader.Contains("materialFresnel(baseReflectance, normalView)", StringComparison.Ordinal)
            && displayShader.Contains("float reflectionAddWeight = 1.0", StringComparison.Ordinal)
            && displayShader.Contains("rawAlbedoMatchesSurface", StringComparison.Ordinal)
            && displayShader.Contains("* fresnelReflectance", StringComparison.Ordinal)
            && !displayShader.Contains("reflectedRadiance += localEnvironmentSpecular", StringComparison.Ordinal)
            && !displayShader.Contains("metallic * 3.00", StringComparison.Ordinal),
            "specular transport must preserve material reflectance without inventing unresolved reflection energy");''')
print('Embedded original fixture unchanged; resource-independent loading and material contracts updated.')
