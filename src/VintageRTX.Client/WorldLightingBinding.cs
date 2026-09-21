using OpenTK.Graphics.OpenGL4;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;

namespace VintageRTX.Client;

internal readonly record struct WorldGpuFrame(CellSceneFrame Scene, LightFrame Lights, CellId Anchor,
    int Regions, int Cells, int Geometry, int Emission)
{
    internal bool Valid => Scene is not null && Lights is not null && Scene.World == Lights.World
        && Regions > 0 && Cells > 0 && Geometry > 0 && Emission > 0;
}

/// <summary>
/// Borrows immutable published tables for a bounded native rendering interval. Never owns the native
/// program or its framebuffer. A scope must end before another stage reuses these programs/units.
/// </summary>
internal sealed class WorldLightingBinding : IDisposable
{
    private static readonly string[] Samplers = ["regionData", "cellData", "geometryData", "lightData"];
    private readonly int[] programs, textures = new int[4], samplers = new int[4];
    private readonly int firstUnit;
    private bool disposed;
    internal static bool HasBridge(int program) => program > 0 && GL.IsProgram(program)
        && GL.GetUniformLocation(program, "vrtxWorldEnabled") >= 0;
    // Even a disabled GLSL branch retains active sampler types. Assign distinct units before
    // ANY native draw so integer region samplers can never alias the terrain's float sampler 0.
    internal static void Prime(int program)
    {
        if (!HasBridge(program)) return;
        int first = GL.GetInteger(GetPName.MaxTextureImageUnits) - 4;
        if (first < 7) throw new NotSupportedException("Insufficient independent native and world samplers.");
        int previous = GL.GetInteger(GetPName.CurrentProgram);
        try
        {
            GL.UseProgram(program);
            for (int i = 0; i < 4; i++) GL.Uniform1(Required(program, Samplers[i]), first + i);
            GL.Uniform1(Required(program, "vrtxWorldEnabled"), 0);
        }
        finally { GL.UseProgram(previous); }
    }
    internal WorldLightingBinding(int[] programIds, WorldGpuFrame frame, DVec3 reference,
        int mode, int finiteSamples = 4, float exposure = 0)
    {
        if (!frame.Valid || !reference.IsFinite || mode is < 1 or > 2 || finiteSamples is < 1 or > 64
            || !float.IsFinite(exposure) || Math.Abs(exposure) > 16)
            throw new ArgumentException("Invalid world frame or lighting options.");
        if (programIds.Length != 2 || programIds.Distinct().Count() != 2 || programIds.Any(p => !HasBridge(p)))
            throw new InvalidOperationException("Both native opaque programs must carry the world bridge.");
        int limit = GL.GetInteger(GetPName.MaxTextureImageUnits);
        if (limit < 16) throw new NotSupportedException("World lighting requires 16 fragment texture units.");
        programs = (int[])programIds.Clone(); firstUnit = limit - 4;
        int active = GL.GetInteger(GetPName.ActiveTexture), current = GL.GetInteger(GetPName.CurrentProgram);
        int[] inputs = [frame.Regions, frame.Cells, frame.Geometry, frame.Emission];
        for (int i = 0; i < 4; i++)
        {
            GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + firstUnit + i));
            textures[i] = GL.GetInteger(GetPName.TextureBinding2D); samplers[i] = GL.GetInteger(GetPName.SamplerBinding);
        }
        try
        {
            for (int i = 0; i < 4; i++)
            {
                GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + firstUnit + i));
                GL.BindTexture(TextureTarget.Texture2D, inputs[i]); GL.BindSampler(firstUnit + i, 0);
            }
            DVec3 offset = reference - frame.Anchor.Position;
            if (Math.Max(Math.Abs(offset.X), Math.Max(Math.Abs(offset.Y), Math.Abs(offset.Z))) > 4096)
                throw new InvalidOperationException("Native reference does not match the local light frame.");
            foreach (int p in programs)
            {
                GL.UseProgram(p);
                for (int i = 0; i < 4; i++) GL.Uniform1(Required(p, Samplers[i]), firstUnit + i);
                GL.Uniform3(Required(p, "sceneAnchor"), frame.Anchor.X, frame.Anchor.Y, frame.Anchor.Z);
                GL.Uniform3(Required(p, "vrtxReferenceOffset"), (float)offset.X, (float)offset.Y, (float)offset.Z);
                GL.Uniform1(Required(p, "lightCount"), frame.Lights.Samples.Length);
                GL.Uniform1(Required(p, "vrtxFiniteSamples"), finiteSamples);
                GL.Uniform1(Required(p, "vrtxMaximumCells"), 256);
                GL.Uniform1(Required(p, "vrtxWorldExposure"), exposure);
                GL.Uniform1(Required(p, "vrtxWorldEnabled"), mode);
            }
            ErrorCode error = GL.GetError();
            if (error != ErrorCode.NoError) throw new InvalidOperationException("World lighting binding: " + error);
        }
        catch { Dispose(); throw; }
        finally { GL.ActiveTexture((TextureUnit)active); GL.UseProgram(current); }
    }
    private static int Required(int p, string name)
    {
        int location = GL.GetUniformLocation(p, name);
        return location >= 0 ? location : throw new InvalidOperationException("Missing world bridge uniform: " + name);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        int active = GL.GetInteger(GetPName.ActiveTexture), current = GL.GetInteger(GetPName.CurrentProgram);
        foreach (int p in programs)
            if (GL.IsProgram(p)) { GL.UseProgram(p); GL.Uniform1(GL.GetUniformLocation(p, "vrtxWorldEnabled"), 0); }
        for (int i = 0; i < 4; i++)
        {
            GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + firstUnit + i));
            GL.BindTexture(TextureTarget.Texture2D, textures[i]); GL.BindSampler(firstUnit + i, samplers[i]);
        }
        GL.ActiveTexture((TextureUnit)active); GL.UseProgram(current);
    }
}
