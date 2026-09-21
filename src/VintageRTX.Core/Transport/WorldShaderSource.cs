namespace VintageRTX.Core.Transport;

public readonly record struct WorldShaderPair(string Vertex, string Fragment);

/// <summary>
/// Contract-checked insertion into the supported native shader, not a replacement renderer copy.
/// Native vertex/animation/SSBO/alpha/depth/fog code remains the owner of surface rasterization.
/// All candidates are built before the caller changes any asset. Unknown contracts fail closed.
/// </summary>
public static class WorldShaderSource
{
    public const string Marker = "// VintageRTX R03 native world bridge";
    public static WorldShaderPair Build(string name, string vertex, string fragment,
        string fog, string scene, string lights, string material, string world)
    {
        if (name is not ("chunkopaque" or "entityanimated")) throw new ArgumentOutOfRangeException(nameof(name));
        vertex = Normalize(vertex); fragment = Normalize(fragment); fog = Normalize(fog);
        if (vertex.Contains(Marker, StringComparison.Ordinal) || fragment.Contains(Marker, StringComparison.Ordinal))
            throw new InvalidDataException("Native shader was already transformed; use the retained original asset.");
        string vertexDeclarations = Marker + "\nout vec3 vrtxSkyLight;\nout vec4 vrtxSurfaceTint;\nout vec3 vrtxOutgoing;\n";
        string fragmentDeclarations = Marker + "\nin vec3 vrtxSkyLight;\nin vec4 vrtxSurfaceTint;\nin vec3 vrtxOutgoing;\n";
        // Use the actual installed shadow filter with its light-dependent floor removed for the
        // environmental carrier. A local lamp must not unshadow sunlight in the replacement path.
        string sky = ExtractFunction(fog, "float getBrightnessFromShadowMap()");
        sky = Once(sky, "getBrightnessFromShadowMap()", "vrtxSkyBrightness()");
        sky = Once(sky, "b = clamp(b + blockBrightness, 0, 1);", "b = clamp(b, 0, 1);");
        string helpers = fragmentDeclarations + "\n" + scene + "\n" + lights + "\n" + material + "\n" + sky + "\n" + world + "\n";
        vertex = BeforeMain(vertex, vertexDeclarations);
        fragment = BeforeMain(fragment, helpers);
        if (name == "chunkopaque")
        {
            vertex = Once(vertex, "rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, camPos);", """
                rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, camPos);
                vrtxSkyLight = max(rgbaLightIn.a, 0.0) * rgbaAmbientIn;
                vrtxSurfaceTint = vec4(1.0);
                vrtxOutgoing = transpose(mat3(modelViewMatrix)) * -camPos.xyz;
                """);
            fragment = Once(fragment, "vec4 texColor = getColorMapped(terrainTexLinear, texture(terrainTex, uv)) * rgba;", """
                vec4 vrtxRawColor = getColorMapped(terrainTexLinear, texture(terrainTex, uv));
                vec4 texColor = vrtxRawColor * rgba;
                """);
            string original = "outColor = applyFogAndShadowFromBrightness(texColor, clamp(fogAmount - 50*murkiness, 0, 1), min(b, nb), worldPos.xyz);";
            fragment = Once(fragment, original, original + "\n" + """
                vec3 vrtxLit;
                if (vrtxResolveWorld(vrtxRawColor.rgb, worldPos.xyz, normal, vrtxOutgoing,
                    min(vrtxSkyBrightness(), nb), renderFlags, glowLevel, vrtxLit)) {
                    float vrtxFog = clamp(fogAmount - 50*murkiness, 0, 1);
                    outColor = applySpheresFog(applyFog(vec4(vrtxLit, outColor.a), vrtxFog), vrtxFog, worldPos.xyz);
                }
                """);
        }
        else
        {
            vertex = Once(vertex, "color = renderColor * colorIn * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);", """
                color = renderColor * colorIn * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
                vrtxSkyLight = max(rgbaLightIn.a, 0.0) * rgbaAmbientIn;
                vrtxSurfaceTint = renderColor * colorIn;
                vrtxOutgoing = transpose(mat3(viewMatrix)) * -cameraPos.xyz;
                """);
            fragment = Once(fragment, "texColor *= color;", "vec3 vrtxRawColor = texColor.rgb * vrtxSurfaceTint.rgb * b;\n\ttexColor *= color;");
            fragment = Once(fragment, "if (glitchFlicker >0 && glitchEffectStrength > 0)", """
                // The first-person depth-offset projection and OIT retain their native path.
                #if USEOIT == 0 && (!defined(ALLOWDEPTHOFFSET) || ALLOWDEPTHOFFSET == 0)
                vec3 vrtxLit;
                if (vrtxResolveWorld(vrtxRawColor, worldPos.xyz, normal, vrtxOutgoing,
                    min(vrtxSkyBrightness(), getBrightnessFromNormal(normal, 1.0, intensity)),
                    renderFlags, glowLevel, vrtxLit)) {
                    float vrtxFog = murkiness > 0.0 ? 0.0 : fogAmount;
                    outColor = applySpheresFog(applyFog(vec4(vrtxLit, outColor.a), vrtxFog), vrtxFog, worldPos.xyz);
                    if (murkiness > 0.0) outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
                }
                #endif
                if (glitchFlicker >0 && glitchEffectStrength > 0)
                """);
        }
        return new(vertex, fragment);
    }
    private static string Normalize(string value)
    { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value.Replace("\r\n", "\n", StringComparison.Ordinal); }
    private static string BeforeMain(string source, string insert)
    {
        int at = source.IndexOf("void main(", StringComparison.Ordinal);
        if (at < 0 || source.IndexOf("void main(", at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Expected one native shader main function.");
        return source.Insert(at, insert + "\n");
    }
    private static string Once(string text, string anchor, string replacement)
    {
        int at = text.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0 || text.IndexOf(anchor, at + anchor.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Unsupported native shader contract: " + anchor);
        return text[..at] + replacement + text[(at + anchor.Length)..];
    }
    private static string ExtractFunction(string text, string signature)
    {
        int start = text.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("Missing native function: " + signature);
        int brace = text.IndexOf('{', start), depth = 0;
        if (brace < 0) throw new InvalidDataException("Missing native function body.");
        for (int i = brace; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            if (text[i] == '}' && --depth == 0) return text[start..(i + 1)];
        }
        throw new InvalidDataException("Unterminated native function.");
    }
}
