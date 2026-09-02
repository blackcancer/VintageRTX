using System.Numerics;
using VintageRTX.Rendering;

namespace VintageRTX.RenderLab;

/// <summary>
/// Identifies the lab Material variants used to drive deterministic renderer assertions.
/// </summary>
internal enum LabMaterial
{
    /// <summary>
    /// Classifies the synthetic sample as air for material-separation assertions.
    /// </summary>
    Air,
    /// <summary>
    /// Classifies the synthetic sample as brick for material-separation assertions.
    /// </summary>
    Brick,
    /// <summary>
    /// Classifies the synthetic sample as polished for material-separation assertions.
    /// </summary>
    Polished,
    /// <summary>
    /// Classifies the synthetic sample as anvil metal for material-separation assertions.
    /// </summary>
    AnvilMetal,
    /// <summary>
    /// Classifies the synthetic sample as lantern cage metal for material-separation assertions.
    /// </summary>
    LanternCageMetal,
    /// <summary>
    /// Classifies the synthetic sample as emissive for material-separation assertions.
    /// </summary>
    Emissive,
    /// <summary>
    /// Classifies the synthetic sample as vegetation for material-separation assertions.
    /// </summary>
    Vegetation,
    /// <summary>
    /// Classifies the synthetic sample as water for material-separation assertions.
    /// </summary>
    Water,
    /// <summary>
    /// Classifies the synthetic sample as honey for material-separation assertions.
    /// </summary>
    Honey,
    /// <summary>
    /// Classifies the synthetic sample as lava for material-separation assertions.
    /// </summary>
    Lava
}

/// <summary>
/// Identifies the lab Liquid variants used to drive deterministic renderer assertions.
/// </summary>
internal enum LabLiquid : byte
{
    /// <summary>
    /// Selects none as the synthetic liquid profile under test.
    /// </summary>
    None,
    /// <summary>
    /// Selects water Shallow as the synthetic liquid profile under test.
    /// </summary>
    WaterShallow,
    /// <summary>
    /// Selects water Deep as the synthetic liquid profile under test.
    /// </summary>
    WaterDeep,
    /// <summary>
    /// Selects honey Shallow as the synthetic liquid profile under test.
    /// </summary>
    HoneyShallow,
    /// <summary>
    /// Selects honey Deep as the synthetic liquid profile under test.
    /// </summary>
    HoneyDeep,
    /// <summary>
    /// Selects lava Shallow as the synthetic liquid profile under test.
    /// </summary>
    LavaShallow,
    /// <summary>
    /// Selects lava Deep as the synthetic liquid profile under test.
    /// </summary>
    LavaDeep,
    /// <summary>
    /// Selects confined Water as the synthetic liquid profile under test.
    /// </summary>
    ConfinedWater
}

/// <summary>
/// Identifies authored synthetic surfaces whose renderer response must be measured independently.
/// </summary>
internal enum LabSurfaceIdentity : byte
{
    /// <summary>
    /// Selects geometry that does not require an object-specific RenderLab metric.
    /// </summary>
    None,
    /// <summary>
    /// Selects the standalone iron anvil reference.
    /// </summary>
    AnvilMetal,
    /// <summary>
    /// Selects the metallic cage surrounding the standalone lantern emitter.
    /// </summary>
    LanternCageMetal
}

/// <summary>
/// Supports surface Hit within the deterministic VintageRTX test infrastructure.
/// </summary>
internal readonly record struct SurfaceHit(
    float Distance,
    Vector3 Position,
    Vector3 GeometricNormal,
    Vector3 ShadingNormal,
    Vector3 Albedo,
    float Roughness,
    float Metallic,
    float Emissive,
    bool Vegetation,
    bool Water,
    LabSurfaceIdentity SurfaceIdentity,
    LabLiquid Liquid,
    float LiquidDepth,
    float LiquidPathLength,
    Vector3 LiquidTransmittance,
    Vector3 LiquidEmission);

/// <summary>
/// Supports synthetic Scene within the deterministic VintageRTX test infrastructure.
/// </summary>
internal sealed class SyntheticScene
{
    /// <summary>
    /// Defines the voxel Width constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    public const int VoxelWidth = 24;
    /// <summary>
    /// Defines the voxel Height constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    public const int VoxelHeight = 16;
    /// <summary>
    /// Defines the voxel Depth constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    public const int VoxelDepth = 24;
    /// <summary>
    /// Defines the occupancy Scale constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    public const int OccupancyScale = 4;
    /// <summary>
    /// Defines the sun Width constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    public const int SunWidth = 24;
    /// <summary>
    /// Defines the sun Height constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    public const int SunHeight = 40;
    /// <summary>
    /// Defines the sun Depth constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    public const int SunDepth = 24;
    /// <summary>
    /// Defines the light Caster Scale constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    public const int LightCasterScale = 16;
    /// <summary>
    /// Defines the maximum Lights constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    public const int MaximumLights = 8;
    /// <summary>
    /// Defines the water Optical Profile Id constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    public const byte WaterOpticalProfileId = 1;
    /// <summary>
    /// Defines the honey Optical Profile Id constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    public const byte HoneyOpticalProfileId = 2;
    /// <summary>
    /// Defines the lava Optical Profile Id constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    public const byte LavaOpticalProfileId = 3;
    /// <summary>
    /// Defines the fluid Layer Flag constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    private const byte FluidLayerFlag = 1;
    /// <summary>
    /// Defines the contained Liquid Flag constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    private const byte ContainedLiquidFlag = 2;
    /// <summary>
    /// Defines the visible Liquid Surface Flag constant shared by the fixture and its assertions; changes must remain synchronized with the tested contract.
    /// </summary>
    private const byte VisibleLiquidSurfaceFlag = 4;

    /// <summary>
    /// Exposes the camera Position state recorded by the test double for subsequent assertions.
    /// </summary>
    public static readonly Vector3 CameraPosition = new(12.5f, 4.6f, 5.5f);
    /// <summary>
    /// Exposes the camera Target state recorded by the test double for subsequent assertions.
    /// </summary>
    public static readonly Vector3 CameraTarget = new(12.0f, 3.2f, 14.5f);
    /// <summary>
    /// Exposes the light Position state recorded by the test double for subsequent assertions.
    /// </summary>
    public static readonly Vector3 LightPosition = new(9.5f, 5.394f, 15.5f);
    /// <summary>
    /// Exposes the light Color state recorded by the test double for subsequent assertions.
    /// </summary>
    public static readonly Vector3 LightColor = new(0.92f, 0.63f, 0.35f);
    /// <summary>
    /// Exposes the sun Direction state recorded by the test double for subsequent assertions.
    /// </summary>
    public static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(0.348f, 0.870f, 0.348f));
    private static readonly Vector3 LanternEnvelopeMinimum = new(9.08f, 4.95f, 15.08f);
    private static readonly Vector3 LanternEnvelopeMaximum = new(9.92f, 6.10f, 15.92f);
    private static readonly Vector3 GrassEnvelopeMinimum = new(7.0f, 2.0f, 12.0f);
    private static readonly Vector3 GrassEnvelopeMaximum = new(8.0f, 3.0f, 13.0f);

    /// <summary>
    /// Initializes a new synthetic Scene fixture with the dependencies required for isolated execution.
    /// </summary>
    /// <param name="width">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <param name="height">Fixture extent in cells or pixels, as defined by the tested API.</param>
    public SyntheticScene(int width, int height)
    {
        Width = width;
        Height = height;
        SourceColor = new byte[checked(width * height * 4)];
        OpaqueReferenceColor = new byte[checked(width * height * 4)];
        NormalRoughness = new float[checked(width * height * 4)];
        ViewPosition = new float[checked(width * height * 4)];
        Material = new byte[checked(width * height * 4)];
        SurfaceIdentities = new LabSurfaceIdentity[checked(width * height)];
        LiquidKind = new byte[checked(width * height)];
        LiquidDepth = new float[checked(width * height)];
        LiquidPathLength = new float[checked(width * height)];
        LiquidTransmittance = new float[checked(width * height * 3)];
        LiquidEmission = new float[checked(width * height * 3)];
        ConfinedLiquidCandidate = new byte[checked(width * height)];
        BuildCameraBasis();
        BuildGBuffer();
        BuildVoxelData();
    }

    /// <summary>
    /// Gets the width measurement consumed by the associated assertion; its unit follows the containing report contract.
    /// </summary>
    public int Width { get; }
    /// <summary>
    /// Gets the height measurement consumed by the associated assertion; its unit follows the containing report contract.
    /// </summary>
    public int Height { get; }
    /// <summary>
    /// Gets the source Color value exposed to the deterministic fixture.
    /// </summary>
    public byte[] SourceColor { get; }
    /// <summary>
    /// Gets the opaque Reference Color value exposed to the deterministic fixture.
    /// </summary>
    public byte[] OpaqueReferenceColor { get; }
    /// <summary>
    /// Gets the normal Roughness value exposed to the deterministic fixture.
    /// </summary>
    public float[] NormalRoughness { get; }
    /// <summary>
    /// Gets the view Position value exposed to the deterministic fixture.
    /// </summary>
    public float[] ViewPosition { get; }
    /// <summary>
    /// Gets the material value exposed to the deterministic fixture.
    /// </summary>
    public byte[] Material { get; }
    /// <summary>
    /// Gets the explicit authored surface identity for each synthetic framebuffer pixel.
    /// </summary>
    public LabSurfaceIdentity[] SurfaceIdentities { get; }
    /// <summary>
    /// Gets the liquid Kind value exposed to the deterministic fixture.
    /// </summary>
    public byte[] LiquidKind { get; }
    /// <summary>
    /// Gets the liquid Depth value exposed to the deterministic fixture.
    /// </summary>
    public float[] LiquidDepth { get; }
    /// <summary>
    /// Gets the liquid Path Length used by the isolated fixture; callers must not escape the sandbox boundary.
    /// </summary>
    public float[] LiquidPathLength { get; }
    /// <summary>
    /// Gets the liquid Transmittance value exposed to the deterministic fixture.
    /// </summary>
    public float[] LiquidTransmittance { get; }
    /// <summary>
    /// Gets the liquid Emission value exposed to the deterministic fixture.
    /// </summary>
    public float[] LiquidEmission { get; }
    /// <summary>
    /// Gets the confined Liquid Candidate value exposed to the deterministic fixture.
    /// </summary>
    public byte[] ConfinedLiquidCandidate { get; }
    /// <summary>
    /// Gets or sets the voxels value exposed to the deterministic fixture.
    /// </summary>
    public byte[] Voxels { get; private set; } = [];
    /// <summary>
    /// Gets or sets the occupancy value exposed to the deterministic fixture.
    /// </summary>
    public byte[] Occupancy { get; private set; } = [];
    /// <summary>
    /// Gets or sets the irradiance value exposed to the deterministic fixture.
    /// </summary>
    public byte[] Irradiance { get; private set; } = [];
    /// <summary>
    /// Gets or sets the irradiance Direction value exposed to the deterministic fixture.
    /// </summary>
    public byte[] IrradianceDirection { get; private set; } = [];
    /// <summary>
    /// Gets or sets the fluid Surface value exposed to the deterministic fixture.
    /// </summary>
    public byte[] FluidSurface { get; private set; } = [];
    /// <summary>
    /// Gets or sets the liquid Metadata value exposed to the deterministic fixture.
    /// </summary>
    public byte[] LiquidMetadata { get; private set; } = [];
    /// <summary>
    /// Gets or sets the liquid Optical Profiles value exposed to the deterministic fixture.
    /// </summary>
    public float[] LiquidOpticalProfiles { get; private set; } = [];
    /// <summary>
    /// Gets or sets the rain Surface value exposed to the deterministic fixture.
    /// </summary>
    public float[] RainSurface { get; private set; } = [];
    /// <summary>
    /// Gets or sets the sun Occupancy value exposed to the deterministic fixture.
    /// </summary>
    public ulong[] SunOccupancy { get; private set; } = [];
    /// <summary>
    /// Gets or sets the light Caster Masks value exposed to the deterministic fixture.
    /// </summary>
    public byte[] LightCasterMasks { get; private set; } = [];
    /// <summary>
    /// Gets or sets the camera Right value exposed to the deterministic fixture.
    /// </summary>
    public Vector3 CameraRight { get; private set; }
    /// <summary>
    /// Gets or sets the camera Up value exposed to the deterministic fixture.
    /// </summary>
    public Vector3 CameraUp { get; private set; }
    /// <summary>
    /// Gets or sets the camera Forward value exposed to the deterministic fixture.
    /// </summary>
    public Vector3 CameraForward { get; private set; }

    /// <summary>
    /// Executes the build Camera Basis step used by the deterministic synthetic Scene fixture.
    /// </summary>
    private void BuildCameraBasis()
    {
        CameraForward = Vector3.Normalize(CameraTarget - CameraPosition);
        CameraRight = Vector3.Normalize(Vector3.Cross(CameraForward, Vector3.UnitY));
        CameraUp = Vector3.Normalize(Vector3.Cross(CameraRight, CameraForward));
    }

    /// <summary>
    /// Executes the build G Buffer step used by the deterministic synthetic Scene fixture.
    /// </summary>
    private void BuildGBuffer()
    {
        float aspect = Width / (float)Height;
        float tangent = MathF.Tan(MathF.PI / 6.0f);
        Parallel.For(0, Height, y =>
        {
            float ndcY = ((y + 0.5f) / Height) * 2.0f - 1.0f;
            for (int x = 0; x < Width; x++)
            {
                float ndcX = ((x + 0.5f) / Width) * 2.0f - 1.0f;
                Vector3 viewRay = Vector3.Normalize(new Vector3(
                    ndcX * aspect * tangent,
                    ndcY * tangent,
                    -1.0f));
                Vector3 worldRay = Vector3.Normalize(
                    CameraRight * viewRay.X
                    + CameraUp * viewRay.Y
                    - CameraForward * viewRay.Z);
                int pixel = (y * Width + x) * 4;
                ConfinedLiquidCandidate[pixel / 4] =
                    TryConfinedLiquidCandidate(CameraPosition, worldRay) ? (byte)255 : (byte)0;
                if (!TraceScene(CameraPosition, worldRay, out SurfaceHit hit))
                {
                    float sky = Math.Clamp(worldRay.Y * 0.5f + 0.5f, 0.0f, 1.0f);
                    Vector3 skyColor = Vector3.Lerp(
                        new Vector3(0.16f, 0.22f, 0.34f),
                        new Vector3(0.48f, 0.66f, 0.92f),
                        sky);
                    WriteColor(SourceColor, pixel, skyColor);
                    WriteColor(OpaqueReferenceColor, pixel, skyColor);
                    continue;
                }

                Vector3 toLight = LightPosition - hit.Position;
                float lightDistance = Math.Max(toLight.Length(), 0.01f);
                Vector3 lightDirection = toLight / lightDistance;
                float sun = Math.Max(Vector3.Dot(hit.ShadingNormal, SunDirection), 0.0f);
                float lamp = Math.Max(Vector3.Dot(hit.ShadingNormal, lightDirection), 0.0f)
                    * 10.0f / (1.0f + 0.11f * lightDistance * lightDistance);
                Vector3 shaded = hit.Albedo * (0.16f + sun * 0.70f)
                    + hit.Albedo * LightColor * lamp
                    + LightColor * hit.Emissive * 1.8f;
                WriteColor(OpaqueReferenceColor, pixel, ToneSource(shaded));
                if (hit.Liquid != LabLiquid.None)
                {
                    Vector3 inscattering = hit.Liquid switch
                    {
                        LabLiquid.WaterShallow or LabLiquid.WaterDeep or LabLiquid.ConfinedWater =>
                            new Vector3(0.008f, 0.030f, 0.042f) * (Vector3.One - hit.LiquidTransmittance),
                        LabLiquid.HoneyShallow or LabLiquid.HoneyDeep =>
                            new Vector3(0.16f, 0.075f, 0.008f) * (Vector3.One - hit.LiquidTransmittance),
                        _ => Vector3.Zero
                    };
                    shaded = shaded * hit.LiquidTransmittance + inscattering + hit.LiquidEmission;

                    int sample = pixel / 4;
                    int optical = sample * 3;
                    LiquidKind[sample] = (byte)hit.Liquid;
                    LiquidDepth[sample] = hit.LiquidDepth;
                    LiquidPathLength[sample] = hit.LiquidPathLength;
                    LiquidTransmittance[optical] = hit.LiquidTransmittance.X;
                    LiquidTransmittance[optical + 1] = hit.LiquidTransmittance.Y;
                    LiquidTransmittance[optical + 2] = hit.LiquidTransmittance.Z;
                    LiquidEmission[optical] = hit.LiquidEmission.X;
                    LiquidEmission[optical + 1] = hit.LiquidEmission.Y;
                    LiquidEmission[optical + 2] = hit.LiquidEmission.Z;
                }
                WriteColor(SourceColor, pixel, ToneSource(shaded));
                SurfaceIdentities[pixel / 4] = hit.SurfaceIdentity;

                Vector3 relative = hit.Position - CameraPosition;
                Vector3 viewPosition = new(
                    Vector3.Dot(relative, CameraRight),
                    Vector3.Dot(relative, CameraUp),
                    -Vector3.Dot(relative, CameraForward));
                Vector3 viewNormal = Vector3.Normalize(new Vector3(
                    Vector3.Dot(hit.ShadingNormal, CameraRight),
                    Vector3.Dot(hit.ShadingNormal, CameraUp),
                    -Vector3.Dot(hit.ShadingNormal, CameraForward)));
                ViewPosition[pixel] = viewPosition.X;
                ViewPosition[pixel + 1] = viewPosition.Y;
                ViewPosition[pixel + 2] = viewPosition.Z;
                ViewPosition[pixel + 3] = 1.0f;
                NormalRoughness[pixel] = viewNormal.X;
                NormalRoughness[pixel + 1] = viewNormal.Y;
                NormalRoughness[pixel + 2] = viewNormal.Z;
                NormalRoughness[pixel + 3] = PackSurfaceAlpha(hit.Roughness, Luminance(hit.Albedo));

                int metallicBits = Math.Clamp((int)MathF.Round(hit.Metallic * 7.0f), 0, 7);
                int emissiveBits = Math.Clamp((int)MathF.Round(hit.Emissive * 7.0f), 0, 7);
                int flags = 64 | metallicBits | emissiveBits << 3;
                if (hit.Vegetation)
                {
                    flags |= 128;
                }
                Material[pixel] = (byte)Math.Clamp((int)MathF.Round(hit.Emissive * 255.0f), 0, 255);
                Material[pixel + 2] = (byte)flags;
                Material[pixel + 3] = 255;
            }
        });
    }

    /// <summary>
    /// Executes the build Voxel Data step used by the deterministic synthetic Scene fixture.
    /// </summary>
    private void BuildVoxelData()
    {
        Voxels = new byte[VoxelWidth * VoxelHeight * VoxelDepth * 4];
        int fineWidth = VoxelWidth * OccupancyScale;
        int fineHeight = VoxelHeight * OccupancyScale;
        int fineDepth = VoxelDepth * OccupancyScale;
        Occupancy = new byte[fineWidth * fineHeight * fineDepth];
        Irradiance = new byte[VoxelWidth * VoxelHeight * VoxelDepth * 3];
        IrradianceDirection = new byte[Irradiance.Length];
        FluidSurface = new byte[VoxelWidth * VoxelDepth * 4];
        LiquidMetadata = new byte[VoxelWidth * VoxelHeight * VoxelDepth * 4];
        LiquidOpticalProfiles = LiquidOpticalRegistry.BuildGpuLookupForProfiles(
            BuildLiquidOpticalProfiles());
        RainSurface = new float[VoxelWidth * VoxelDepth];
        SunOccupancy = new ulong[SunWidth * SunHeight * SunDepth];
        LightCasterMasks = new byte[LightCasterScale * LightCasterScale * LightCasterScale * MaximumLights];

        for (int z = 0; z < VoxelDepth; z++)
        {
            for (int y = 0; y < VoxelHeight; y++)
            {
                for (int x = 0; x < VoxelWidth; x++)
                {
                    LabMaterial dominant = LiquidMaterialAt(x, y, z);
                    int occupied = 0;
                    for (int subZ = 0; subZ < OccupancyScale; subZ++)
                    {
                        for (int subY = 0; subY < OccupancyScale; subY++)
                        {
                            for (int subX = 0; subX < OccupancyScale; subX++)
                            {
                                Vector3 point = new(
                                    x + (subX + 0.5f) / OccupancyScale,
                                    y + (subY + 0.5f) / OccupancyScale,
                                    z + (subZ + 0.5f) / OccupancyScale);
                                LabMaterial material = SolidMaterialAt(point);
                                if (material == LabMaterial.Air)
                                {
                                    continue;
                                }
                                dominant = MoreImportant(dominant, material);
                                occupied++;
                                int fineIndex = ((z * OccupancyScale + subZ) * fineHeight
                                    + y * OccupancyScale + subY) * fineWidth
                                    + x * OccupancyScale + subX;
                                Occupancy[fineIndex] = 255;
                            }
                        }
                    }

                    int voxel = ((z * VoxelHeight + y) * VoxelWidth + x) * 4;
                    WriteVoxelMaterial(voxel, dominant, occupied > 0);
                    WriteIrradiance(x, y, z, dominant == LabMaterial.Air);
                    WriteLiquidMetadata(voxel, x, y, z, dominant);
                }
            }
        }

        for (int z = 0; z < VoxelDepth; z++)
        {
            for (int x = 0; x < VoxelWidth; x++)
            {
                int column = z * VoxelWidth + x;
                WriteFluidSurface(column * 4, x, z);
                float highest = 0.0f;
                for (int y = 0; y < VoxelHeight; y++)
                {
                    if (SolidMaterialAt(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f)) != LabMaterial.Air)
                    {
                        highest = y + 1.0f;
                    }
                }
                RainSurface[column] = highest;
            }
        }

        BuildSunOccupancy();
        BuildLanternCaster();
        ValidateSyntheticGeometry();
    }

    /// <summary>
    /// Executes the build Sun Occupancy step used by the deterministic synthetic Scene fixture.
    /// </summary>
    private void BuildSunOccupancy()
    {
        for (int z = 0; z < SunDepth; z++)
        {
            for (int y = 0; y < SunHeight; y++)
            {
                for (int x = 0; x < SunWidth; x++)
                {
                    ulong mask = 0;
                    for (int subZ = 0; subZ < 4; subZ++)
                    {
                        for (int subY = 0; subY < 4; subY++)
                        {
                            for (int subX = 0; subX < 4; subX++)
                            {
                                bool occupied = false;
                                int fineBaseX = x * 2 * OccupancyScale + subX * 2;
                                int fineBaseY = y * 2 * OccupancyScale + subY * 2;
                                int fineBaseZ = z * 2 * OccupancyScale + subZ * 2;
                                for (int fineZ = 0; fineZ < 2 && !occupied; fineZ++)
                                {
                                    for (int fineY = 0; fineY < 2 && !occupied; fineY++)
                                    {
                                        for (int fineX = 0; fineX < 2; fineX++)
                                        {
                                            int sampleX = fineBaseX + fineX;
                                            int sampleY = fineBaseY + fineY;
                                            int sampleZ = fineBaseZ + fineZ;
                                            if ((uint)sampleX >= VoxelWidth * OccupancyScale
                                                || (uint)sampleY >= VoxelHeight * OccupancyScale
                                                || (uint)sampleZ >= VoxelDepth * OccupancyScale)
                                            {
                                                continue;
                                            }

                                            int occupancyIndex = (sampleZ
                                                    * VoxelHeight * OccupancyScale
                                                + sampleY)
                                                * VoxelWidth * OccupancyScale
                                                + sampleX;
                                            if (Occupancy[occupancyIndex] != 0)
                                            {
                                                occupied = true;
                                                break;
                                            }
                                        }
                                    }
                                }

                                if (occupied)
                                {
                                    int bitIndex = (subZ * 4 + subY) * 4 + subX;
                                    mask |= 1UL << bitIndex;
                                }
                            }
                        }
                    }
                    SunOccupancy[(z * SunHeight + y) * SunWidth + x] = mask;
                }
            }
        }
    }

    /// <summary>
    /// Executes the block Occupancy Coverage step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="blockX">Coordinate component in the space defined by the tested API.</param>
    /// <param name="blockY">Coordinate component in the space defined by the tested API.</param>
    /// <param name="blockZ">Coordinate component in the space defined by the tested API.</param>
    /// <returns>The block Occupancy Coverage result consumed by the caller&apos;s assertion.</returns>
    private float BlockOccupancyCoverage(int blockX, int blockY, int blockZ)
    {
        if ((uint)blockX >= VoxelWidth
            || (uint)blockY >= VoxelHeight
            || (uint)blockZ >= VoxelDepth)
        {
            return 0.0f;
        }

        int fineWidth = VoxelWidth * OccupancyScale;
        int fineHeight = VoxelHeight * OccupancyScale;
        int occupied = 0;
        for (int subZ = 0; subZ < OccupancyScale; subZ++)
        {
            for (int subY = 0; subY < OccupancyScale; subY++)
            {
                for (int subX = 0; subX < OccupancyScale; subX++)
                {
                    int index = ((blockZ * OccupancyScale + subZ) * fineHeight
                        + blockY * OccupancyScale + subY) * fineWidth
                        + blockX * OccupancyScale + subX;
                    occupied += Occupancy[index] == 0 ? 0 : 1;
                }
            }
        }
        return occupied / 64.0f;
    }

    /// <summary>
    /// Executes the build Lantern Caster step used by the deterministic synthetic Scene fixture.
    /// </summary>
    private void BuildLanternCaster()
    {
        for (int z = 0; z < 16; z++)
        {
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    bool basePlate = y <= 1 && x is >= 3 and <= 12 && z is >= 3 and <= 12;
                    bool bottomRing = y == 2 && IsRing(x, z, 2, 13);
                    bool upright = y is >= 3 and <= 12
                        && ((x <= 3 || x >= 12) && (z <= 3 || z >= 12));
                    bool middleRing = (y == 4 || y == 11) && IsRing(x, z, 2, 13);
                    bool upperRing = y == 13 && IsRing(x, z, 2, 13);
                    bool hood = y == 14 && x is >= 4 and <= 11 && z is >= 4 and <= 11;
                    bool top = y == 15 && x is >= 6 and <= 9 && z is >= 6 and <= 9;
                    bool flamePocket = x is >= 7 and <= 9 && y is >= 5 and <= 7 && z is >= 7 and <= 9;
                    if ((basePlate || bottomRing || upright || middleRing || upperRing || hood || top)
                        && !flamePocket)
                    {
                        LightCasterMasks[(z * 16 + y) * 16 + x] = 255;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Determines whether is Ring holds for the current synthetic fixture state.
    /// </summary>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <param name="minimum">The minimum input used to configure this deterministic test path.</param>
    /// <param name="maximum">The maximum input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool IsRing(int x, int z, int minimum, int maximum)
    {
        return x is >= 0 and < 16
            && z is >= 0 and < 16
            && x >= minimum && x <= maximum && z >= minimum && z <= maximum
            && (x == minimum || x == maximum || z == minimum || z == maximum);
    }

    /// <summary>
    /// Writes irradiance into caller-owned fixture storage without retaining the destination buffer.
    /// </summary>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="y">Coordinate component in the space defined by the tested API.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <param name="air">The air input used to configure this deterministic test path.</param>
    private void WriteIrradiance(int x, int y, int z, bool air)
    {
        int index = ((z * VoxelHeight + y) * VoxelWidth + x) * 3;
        Vector3 center = new(x + 0.5f, y + 0.5f, z + 0.5f);
        Vector3 toLight = LightPosition - center;
        float distance = Math.Max(toLight.Length(), 0.001f);
        Vector3 direction = toLight / distance;
        float energy = air ? 0.018f + 0.62f / (1.0f + 0.19f * distance * distance) : 0.0f;
        Vector3 radiance = LightColor * energy;
        Irradiance[index] = EncodeIrradiance(radiance.X);
        Irradiance[index + 1] = EncodeIrradiance(radiance.Y);
        Irradiance[index + 2] = EncodeIrradiance(radiance.Z);
        IrradianceDirection[index] = EncodeDirection(direction.X);
        IrradianceDirection[index + 1] = EncodeDirection(direction.Y);
        IrradianceDirection[index + 2] = EncodeDirection(direction.Z);
    }

    /// <summary>
    /// Executes the encode Irradiance step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <returns>The encode Irradiance result consumed by the caller&apos;s assertion.</returns>
    private static byte EncodeIrradiance(float value) =>
        (byte)Math.Clamp((int)MathF.Round(MathF.Sqrt(Math.Clamp(value / 2.0f, 0.0f, 1.0f)) * 255.0f), 0, 255);

    /// <summary>
    /// Executes the encode Direction step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <returns>The encode Direction result consumed by the caller&apos;s assertion.</returns>
    private static byte EncodeDirection(float value) =>
        (byte)Math.Clamp((int)MathF.Round((value * 0.5f + 0.5f) * 255.0f), 0, 255);

    /// <summary>
    /// Writes voxel Material into caller-owned fixture storage without retaining the destination buffer.
    /// </summary>
    /// <param name="index">Coordinate component in the space defined by the tested API.</param>
    /// <param name="material">The material input used to configure this deterministic test path.</param>
    /// <param name="occupied">The occupied input used to configure this deterministic test path.</param>
    private void WriteVoxelMaterial(int index, LabMaterial material, bool occupied)
    {
        Vector3 color = material switch
        {
            LabMaterial.Brick => new Vector3(0.43f, 0.39f, 0.35f),
            LabMaterial.Polished => new Vector3(0.66f, 0.58f, 0.49f),
            LabMaterial.AnvilMetal => new Vector3(0.56f, 0.57f, 0.58f),
            LabMaterial.LanternCageMetal => new Vector3(0.34f, 0.25f, 0.16f),
            LabMaterial.Emissive => LightColor,
            LabMaterial.Vegetation => new Vector3(0.25f, 0.49f, 0.18f),
            LabMaterial.Water => new Vector3(0.18f, 0.35f, 0.45f),
            LabMaterial.Honey => new Vector3(0.62f, 0.27f, 0.035f),
            LabMaterial.Lava => new Vector3(1.0f, 0.23f, 0.02f),
            _ => Vector3.Zero
        };
        byte alpha = material switch
        {
            LabMaterial.Water or LabMaterial.Honey or LabMaterial.Lava => 64,
            LabMaterial.AnvilMetal or LabMaterial.LanternCageMetal => 192,
            LabMaterial.Emissive => 192,
            _ when occupied => 128,
            _ => 0
        };
        Voxels[index] = ToByte(color.X);
        Voxels[index + 1] = ToByte(color.Y);
        Voxels[index + 2] = ToByte(color.Z);
        Voxels[index + 3] = alpha;
    }

    /// <summary>
    /// Executes the more Important step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="current">The current input used to configure this deterministic test path.</param>
    /// <param name="candidate">The candidate input used to configure this deterministic test path.</param>
    /// <returns>The more Important result consumed by the caller&apos;s assertion.</returns>
    private static LabMaterial MoreImportant(LabMaterial current, LabMaterial candidate)
    {
        static int Score(LabMaterial value) => value switch
        {
            LabMaterial.Emissive => 6,
            LabMaterial.AnvilMetal or LabMaterial.LanternCageMetal => 5,
            LabMaterial.Polished => 4,
            LabMaterial.Vegetation => 3,
            LabMaterial.Brick => 2,
            LabMaterial.Water => 1,
            LabMaterial.Honey => 1,
            LabMaterial.Lava => 1,
            _ => 0
        };
        return Score(candidate) > Score(current) ? candidate : current;
    }

    /// <summary>
    /// Executes the liquid Material At step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="y">Coordinate component in the space defined by the tested API.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <returns>The liquid Material At result consumed by the caller&apos;s assertion.</returns>
    private static LabMaterial LiquidMaterialAt(int x, int y, int z)
    {
        if (x is >= 14 and <= 18 && z is >= 7 and <= 10 && y <= 2)
            return LabMaterial.Water;
        if (x is >= 10 and <= 12 && z is >= 7 and <= 9 && y is >= 1 and <= 2)
            return LabMaterial.Honey;
        if (x is >= 4 and <= 6 && z is >= 8 and <= 10 && y <= 2)
            return LabMaterial.Lava;
        if (x == 18 && z == 13 && y == 2)
            return LabMaterial.Water;
        return LabMaterial.Air;
    }

    /// <summary>
    /// Writes fluid Surface into caller-owned fixture storage without retaining the destination buffer.
    /// </summary>
    /// <param name="offset">Bounded fixture count, index, or offset used to select the exercised case.</param>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    private void WriteFluidSurface(int offset, int x, int z)
    {
        byte profileId = LiquidProfileIdAt(x, z);
        if (profileId == LiquidOpticalRegistry.NoLiquidProfileId)
            return;

        bool contained = x == 18 && z == 13;
        FluidSurface[offset] = 3;
        FluidSurface[offset + 1] = profileId;
        FluidSurface[offset + 2] = contained ? (byte)64 : (byte)7;
        FluidSurface[offset + 3] = contained
            ? (byte)(ContainedLiquidFlag | VisibleLiquidSurfaceFlag)
            : (byte)(FluidLayerFlag | VisibleLiquidSurfaceFlag);
    }

    /// <summary>
    /// Writes liquid Metadata into caller-owned fixture storage without retaining the destination buffer.
    /// </summary>
    /// <param name="offset">Bounded fixture count, index, or offset used to select the exercised case.</param>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="y">Coordinate component in the space defined by the tested API.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <param name="material">The material input used to configure this deterministic test path.</param>
    private void WriteLiquidMetadata(
        int offset,
        int x,
        int y,
        int z,
        LabMaterial material)
    {
        byte profileId = material switch
        {
            LabMaterial.Water => WaterOpticalProfileId,
            LabMaterial.Honey => HoneyOpticalProfileId,
            LabMaterial.Lava => LavaOpticalProfileId,
            _ => LiquidOpticalRegistry.NoLiquidProfileId
        };
        if (profileId == LiquidOpticalRegistry.NoLiquidProfileId)
            return;

        bool contained = x == 18 && y == 2 && z == 13;
        LiquidMetadata[offset] = profileId;
        LiquidMetadata[offset + 1] = contained
            ? (byte)(ContainedLiquidFlag | VisibleLiquidSurfaceFlag)
            : (byte)(FluidLayerFlag | (y == 2 ? VisibleLiquidSurfaceFlag : 0));
        LiquidMetadata[offset + 2] = contained ? (byte)64 : (byte)7;
    }

    /// <summary>
    /// Executes the liquid Profile Id At step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <returns>The liquid Profile Id At result consumed by the caller&apos;s assertion.</returns>
    private static byte LiquidProfileIdAt(int x, int z)
    {
        if (x is >= 14 and <= 18 && z is >= 7 and <= 10 || x == 18 && z == 13)
            return WaterOpticalProfileId;
        if (x is >= 10 and <= 12 && z is >= 7 and <= 9)
            return HoneyOpticalProfileId;
        if (x is >= 4 and <= 6 && z is >= 8 and <= 10)
            return LavaOpticalProfileId;
        return LiquidOpticalRegistry.NoLiquidProfileId;
    }

    /// <summary>
    /// Executes the build Liquid Optical Profiles step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    private static IReadOnlyDictionary<byte, LiquidOpticalProfile> BuildLiquidOpticalProfiles()
    {
        LiquidSurfaceDynamics waterDynamics = new(
            0.85f, 0.025f, 4.8f, 1.1f, 0.82f, 0.75f, 0.72f,
            0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f);
        LiquidSurfaceDynamics honeyDynamics = new(
            0.10f, 0.006f, 2.6f, 0.18f, 0.96f, 0.20f, 1.35f,
            0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f);
        LiquidSurfaceDynamics lavaDynamics = new(
            0.03f, 0.012f, 2.2f, 0.12f, 0.98f, 0.35f, 1.70f,
            0.55f, 0.08f, 0.22f, 2.6f, 0.80f, 1.75f);
        LiquidPhysicalProperties waterPhysics = new(
            "pure-water-293k", 293.15f, 998.2f, 0.001002f, 0.07275f, 0.020f, 0.0f, 0.0f);
        LiquidPhysicalProperties honeyPhysics = new(
            "natural-honey-303k", 303.15f, 1496.0f, 7.85f, 0.0801f, 0.003f, 0.0f, 0.0f);
        LiquidPhysicalProperties lavaPhysics = new(
            "basaltic-melt-1473k", 1473.15f, 2800.0f, 10.0f, 0.4f, 0.005f, 1473.15f, 0.8f);
        return new Dictionary<byte, LiquidOpticalProfile>
        {
            [WaterOpticalProfileId] = new(
                "renderlab-water", 1.333f,
                new LiquidRgb(0.3594f, 0.0654f, 0.00922f),
                new LiquidRgb(0.0007f, 0.0015f, 0.0035f),
                1.0f, new LiquidRgb(0.0f, 0.0f, 0.0f), 0.0f,
                0.08f, 0.02f, 0.08f, 0.18f, waterDynamics, false, waterPhysics),
            [HoneyOpticalProfileId] = new(
                "renderlab-honey", 1.49f,
                new LiquidRgb(0.32f, 0.92f, 2.10f),
                new LiquidRgb(0.10f, 0.045f, 0.008f),
                0.82f, new LiquidRgb(0.0f, 0.0f, 0.0f), 0.0f,
                0.20f, 0.008f, 0.82f, 0.05f, honeyDynamics, false, honeyPhysics),
            [LavaOpticalProfileId] = new(
                "renderlab-lava", 1.55f,
                new LiquidRgb(0.95f, 1.85f, 3.60f),
                new LiquidRgb(0.08f, 0.03f, 0.005f),
                0.04f, new LiquidRgb(1.85f, 0.42f, 0.035f), 1.0f,
                0.34f, 0.015f, 0.95f, -0.10f, lavaDynamics, true, lavaPhysics)
        };
    }

    /// <summary>
    /// Executes the solid Material At step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="point">The point input used to configure this deterministic test path.</param>
    /// <returns>The solid Material At result consumed by the caller&apos;s assertion.</returns>
    private static LabMaterial SolidMaterialAt(Vector3 point)
    {
        if ((Inside(point, new Vector3(3, 1, 4), new Vector3(21, 2, 22))
                && !IsLiquidFootprint(point.X, point.Z))
            || Inside(point, new Vector3(3, 2, 21), new Vector3(21, 10, 22))
            || Inside(point, new Vector3(2.75f, 2, 4), new Vector3(3.25f, 10, 22))
            || Inside(point, new Vector3(20.0f, 2, 4), new Vector3(20.5f, 10, 22))
            || Inside(point, new Vector3(3, 9, 11), new Vector3(12, 9.5f, 22)))
        {
            return LabMaterial.Brick;
        }
        if (Inside(point, new Vector3(17, 2, 15), new Vector3(18.5f, 4, 16.5f))
            || Inside(point, new Vector3(9.05f, 2, 15.05f), new Vector3(9.95f, 4.8f, 15.95f)))
        {
            return LabMaterial.Polished;
        }
        if (InsideAnvil(point))
        {
            return LabMaterial.AnvilMetal;
        }
        if (InsideLanternCage(point))
        {
            return LabMaterial.LanternCageMetal;
        }
        if (Inside(point, new Vector3(9.34f, 5.25f, 15.34f), new Vector3(9.66f, 5.72f, 15.66f)))
        {
            return LabMaterial.Emissive;
        }
        if (InsideGrass(point))
        {
            return LabMaterial.Vegetation;
        }
        return LabMaterial.Air;
    }

    /// <summary>
    /// Executes the inside Anvil step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="point">The point input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool InsideAnvil(Vector3 point)
    {
        return Inside(point, new Vector3(12.5f, 2.0f, 13.6f), new Vector3(14.5f, 2.3f, 14.6f))
            || Inside(point, new Vector3(13.05f, 2.3f, 13.75f), new Vector3(13.95f, 2.95f, 14.45f))
            || Inside(point, new Vector3(12.35f, 2.95f, 13.55f), new Vector3(14.75f, 3.25f, 14.55f))
            || Inside(point, new Vector3(14.7f, 3.0f, 13.75f), new Vector3(15.35f, 3.2f, 14.35f));
    }

    /// <summary>
    /// Executes the inside Lantern Cage step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="point">The point input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool InsideLanternCage(Vector3 point)
    {
        // The occupancy volume and the visible proxy must describe the same
        // finite object. Without this envelope the corner predicate below
        // classified almost the complete room slice at lantern height as metal.
        if (!Inside(point, LanternEnvelopeMinimum, LanternEnvelopeMaximum))
        {
            return false;
        }

        bool lower = Inside(point, LanternEnvelopeMinimum, new Vector3(9.92f, 5.10f, 15.92f));
        bool upper = Inside(point, new Vector3(9.12f, 5.92f, 15.12f), new Vector3(9.88f, 6.10f, 15.88f));
        bool cornerPost = (point.X <= 9.18f || point.X >= 9.82f)
            && (point.Z <= 15.18f || point.Z >= 15.82f)
            && point.Y is >= 5.08f and <= 5.95f;
        return lower || upper || cornerPost;
    }

    /// <summary>
    /// Validates synthetic Geometry and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    private void ValidateSyntheticGeometry()
    {
        Vector3[] outsideCageProbes =
        [
            new(4.5f, 5.5f, 8.5f),
            new(18.5f, 5.5f, 18.5f),
            new(9.5f, 5.5f, 10.5f),
            new(12.5f, 5.5f, 15.5f)
        ];
        if (outsideCageProbes.Any(InsideLanternCage))
        {
            throw new InvalidOperationException(
                "Synthetic lantern occupancy escaped its finite cage envelope.");
        }
        if (!InsideLanternCage(new Vector3(9.14f, 5.5f, 15.14f))
            || InsideLanternCage(new Vector3(9.5f, 5.5f, 15.5f)))
        {
            throw new InvalidOperationException(
                "Synthetic lantern must keep opaque corner posts and transparent glass.");
        }

        int lower = 0;
        int middle = 0;
        int upper = 0;
        for (int z = 0; z < LightCasterScale; z++)
        {
            for (int y = 0; y < LightCasterScale; y++)
            {
                for (int x = 0; x < LightCasterScale; x++)
                {
                    if (LightCasterMasks[(z * LightCasterScale + y) * LightCasterScale + x] == 0)
                    {
                        continue;
                    }
                    if (y < LightCasterScale / 3)
                    {
                        lower++;
                    }
                    else if (y >= LightCasterScale - LightCasterScale / 3)
                    {
                        upper++;
                    }
                    else
                    {
                        middle++;
                    }
                }
            }
        }
        if (lower == 0 || middle == 0 || upper == 0
            || LightCasterMasks[(8 * LightCasterScale + 6) * LightCasterScale + 8] != 0)
        {
            throw new InvalidOperationException(
                "Synthetic lantern caster must cover every cage band without filling the flame/glass pocket.");
        }

        float grassCoverage = BlockOccupancyCoverage(7, 2, 12);
        if (grassCoverage <= 0.0f
            || grassCoverage >= 0.55f
            || BlockOccupancyCoverage(7, 3, 12) > 0.0f)
        {
            throw new InvalidOperationException(
                "Synthetic crossed vegetation must remain alpha-cut and confined to one block.");
        }
    }

    /// <summary>
    /// Executes the inside Grass step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="point">The point input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool InsideGrass(Vector3 point)
    {
        if (!Inside(point, GrassEnvelopeMinimum, GrassEnvelopeMaximum))
        {
            return false;
        }
        float x = point.X - 7.0f;
        float z = point.Z - 12.0f;
        float normalizedHeight = point.Y - 2.0f;
        float width = normalizedHeight > 0.68f ? 0.09f : 0.13f;
        bool crossedPlane = Math.Abs(x - z) < width
            || Math.Abs(x + z - 1.0f) < width;
        return crossedPlane && GrassTexelIsOpaque(point, normalizedHeight);
    }

    /// <summary>
    /// Executes the grass Texel Is Opaque step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="point">The point input used to configure this deterministic test path.</param>
    /// <param name="normalizedHeight">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool GrassTexelIsOpaque(Vector3 point, float normalizedHeight)
    {
        float blade = MathF.Sin(
            (point.X + point.Z) * 31.0f
            + normalizedHeight * 13.0f);
        return blade >= -0.35f + normalizedHeight * 0.20f;
    }

    /// <summary>
    /// Traces scene in the deterministic reference scene and returns the nearest valid synthetic hit.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <param name="hit">The hit input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool TraceScene(Vector3 origin, Vector3 direction, out SurfaceHit hit)
    {
        SurfaceHit selected = default;
        float nearest = float.PositiveInfinity;
        void Accept(float distance, Vector3 normal, Vector3 albedo, float roughness,
            float metallic = 0.0f, float emissive = 0.0f, bool vegetation = false, bool water = false,
            LabSurfaceIdentity surfaceIdentity = LabSurfaceIdentity.None)
        {
            if (distance <= 0.001f || distance >= nearest)
            {
                return;
            }
            Vector3 position = origin + direction * distance;
            Vector3 shading = PerturbNormal(position, normal, roughness, vegetation, water);
            nearest = distance;
            selected = new SurfaceHit(distance, position, normal, shading, albedo, roughness,
                metallic, emissive, vegetation, water, surfaceIdentity, LabLiquid.None, 0.0f, 0.0f,
                Vector3.One, Vector3.Zero);
        }

        if (PlaneY(origin, direction, 2.0f, 3, 21, 4, 22, out float floor))
        {
            Vector3 floorPoint = origin + direction * floor;
            if (!IsLiquidFootprint(floorPoint.X, floorPoint.Z))
            {
                Accept(floor, Vector3.UnitY, new Vector3(0.48f, 0.42f, 0.36f), 0.78f);
            }
        }

        // The transparent source pass is composited over these opaque receivers.
        // gPosition, normal and PBR payload deliberately remain those of the
        // receiver, matching the real deferred pipeline rather than inventing a
        // second opaque water surface.
        TraceLiquidBottoms(origin, direction, Accept);
        if (PlaneZ(origin, direction, 21.0f, 3, 21, 2, 10, out float back))
        {
            Accept(back, -Vector3.UnitZ, new Vector3(0.42f, 0.37f, 0.33f), 0.82f);
        }
        // Match the visible plane to the inner face of the half-block voxel
        // wall. Starting the shadow ray at x=3.0 left it inside occupancy and
        // created a synthetic self-shadow wedge unrelated to renderer quality.
        if (PlaneX(origin, direction, 3.25f, 2, 10, 4, 22, out float left))
        {
            Accept(left, Vector3.UnitX, new Vector3(0.40f, 0.36f, 0.32f), 0.82f);
        }
        if (PlaneX(origin, direction, 20.0f, 2, 10, 4, 22, out float right))
        {
            Accept(right, -Vector3.UnitX, new Vector3(0.48f, 0.43f, 0.38f), 0.80f);
        }
        if (PlaneY(origin, direction, 9.0f, 3, 12, 11, 22, out float ceiling))
        {
            Accept(ceiling, -Vector3.UnitY, new Vector3(0.36f, 0.32f, 0.29f), 0.86f);
        }
        TraceBox(origin, direction, new Vector3(17, 2, 15), new Vector3(18.5f, 4, 16.5f),
            new Vector3(0.72f, 0.61f, 0.50f), 0.14f, 0.0f, Accept);
        TraceBox(origin, direction, new Vector3(9.05f, 2, 15.05f), new Vector3(9.95f, 4.8f, 15.95f),
            new Vector3(0.56f, 0.48f, 0.40f), 0.22f, 0.0f, Accept);
        TraceAnvil(origin, direction, Accept);
        TraceLantern(origin, direction, Accept);
        TraceGrass(origin, direction, Accept);
        if (nearest < float.PositiveInfinity
            && TryResolveLiquid(origin, direction, nearest, out LabLiquid liquid,
                out float depth, out float pathLength, out Vector3 transmittance,
                out Vector3 emission))
        {
            selected = selected with
            {
                Liquid = liquid,
                LiquidDepth = depth,
                LiquidPathLength = pathLength,
                LiquidTransmittance = transmittance,
                LiquidEmission = emission
            };
        }
        hit = selected;
        return nearest < float.PositiveInfinity;
    }

    /// <summary>
    /// Traces liquid Bottoms in the deterministic reference scene and returns the nearest valid synthetic hit.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <param name="accept">The accept input used to configure this deterministic test path.</param>
    private static void TraceLiquidBottoms(
        Vector3 origin,
        Vector3 direction,
        HitConsumer accept)
    {
        Vector3 masonry = new(0.48f, 0.42f, 0.36f);
        if (PlaneY(origin, direction, 2.55f, 14.0f, 16.5f, 7.0f, 11.0f, out float waterShallow))
            accept(waterShallow, Vector3.UnitY, masonry, 0.78f, 0.0f, 0.0f, false, false, LabSurfaceIdentity.None);
        if (PlaneY(origin, direction, 0.80f, 16.5f, 19.0f, 7.0f, 11.0f, out float waterDeep))
            accept(waterDeep, Vector3.UnitY, masonry, 0.78f, 0.0f, 0.0f, false, false, LabSurfaceIdentity.None);

        Vector3 honeyReceiver = new(0.54f, 0.45f, 0.31f);
        if (PlaneY(origin, direction, 2.45f, 10.0f, 11.25f, 7.0f, 10.0f, out float honeyShallow))
            accept(honeyShallow, Vector3.UnitY, honeyReceiver, 0.72f, 0.0f, 0.0f, false, false, LabSurfaceIdentity.None);
        if (PlaneY(origin, direction, 1.25f, 11.25f, 12.5f, 7.0f, 10.0f, out float honeyDeep))
            accept(honeyDeep, Vector3.UnitY, honeyReceiver, 0.72f, 0.0f, 0.0f, false, false, LabSurfaceIdentity.None);

        Vector3 basalt = new(0.20f, 0.17f, 0.15f);
        if (PlaneY(origin, direction, 2.35f, 8.0f, 9.0f, 10.25f, 12.5f, out float lavaShallow))
            accept(lavaShallow, Vector3.UnitY, basalt, 0.88f, 0.0f, 0.0f, false, false, LabSurfaceIdentity.None);
        if (PlaneY(origin, direction, 1.00f, 9.0f, 10.0f, 10.25f, 12.5f, out float lavaDeep))
            accept(lavaDeep, Vector3.UnitY, basalt, 0.88f, 0.0f, 0.0f, false, false, LabSurfaceIdentity.None);

        if (PlaneY(origin, direction, 2.85f, 18.0f, 19.0f, 12.5f, 13.5f, out float confined))
            accept(confined, Vector3.UnitY, masonry, 0.74f, 0.0f, 0.0f, false, false, LabSurfaceIdentity.None);
    }

    /// <summary>
    /// Attempts to resolve Liquid without throwing when fixture data cannot satisfy the requested path.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <param name="opaqueDistance">The opaque Distance input used to configure this deterministic test path.</param>
    /// <param name="liquid">The liquid input used to configure this deterministic test path.</param>
    /// <param name="depth">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <param name="pathLength">Filesystem location constrained to the isolated test sandbox.</param>
    /// <param name="transmittance">The transmittance input used to configure this deterministic test path.</param>
    /// <param name="emission">The emission input used to configure this deterministic test path.</param>
    /// <returns>True when the requested fixture operation succeeds; otherwise false.</returns>
    private static bool TryResolveLiquid(
        Vector3 origin,
        Vector3 direction,
        float opaqueDistance,
        out LabLiquid liquid,
        out float depth,
        out float pathLength,
        out Vector3 transmittance,
        out Vector3 emission)
    {
        liquid = LabLiquid.None;
        depth = 0.0f;
        pathLength = 0.0f;
        transmittance = Vector3.One;
        emission = Vector3.Zero;
        if (direction.Y >= -0.001f)
            return false;

        LabLiquid resolvedLiquid = LabLiquid.None;
        float resolvedDepth = 0.0f;
        float resolvedPathLength = 0.0f;
        Vector3 resolvedTransmittance = Vector3.One;
        Vector3 resolvedEmission = Vector3.Zero;

        bool Resolve(float minX, float maxX, float minZ, float maxZ, float surfaceY,
            float bottomY, LabLiquid kind, Vector3 absorption, Vector3 emittedRadiance)
        {
            if (!PlaneY(origin, direction, surfaceY, minX, maxX, minZ, maxZ, out float surfaceDistance)
                || surfaceDistance >= opaqueDistance - 0.001f)
                return false;

            resolvedLiquid = kind;
            resolvedDepth = surfaceY - bottomY;
            resolvedPathLength = Math.Clamp(resolvedDepth / -direction.Y, resolvedDepth, 8.0f);
            resolvedTransmittance = new Vector3(
                MathF.Exp(-absorption.X * resolvedPathLength),
                MathF.Exp(-absorption.Y * resolvedPathLength),
                MathF.Exp(-absorption.Z * resolvedPathLength));
            float emissionBuildUp = 1.0f - MathF.Exp(-1.35f * resolvedPathLength);
            resolvedEmission = emittedRadiance * emissionBuildUp;
            return true;
        }

        Vector3 waterAbsorption = new(0.46f, 0.13f, 0.045f);
        if (Resolve(14.0f, 16.5f, 7.0f, 11.0f, 3.0f, 2.55f,
                LabLiquid.WaterShallow, waterAbsorption, Vector3.Zero)
            || Resolve(16.5f, 19.0f, 7.0f, 11.0f, 3.0f, 0.80f,
                LabLiquid.WaterDeep, waterAbsorption, Vector3.Zero))
        {
            liquid = resolvedLiquid;
            depth = resolvedDepth;
            pathLength = resolvedPathLength;
            transmittance = resolvedTransmittance;
            emission = resolvedEmission;
            return true;
        }

        // Honey is optically distinct from water: it retains red, absorbs blue
        // aggressively and reaches a much lower broadband transmission.
        Vector3 honeyAbsorption = new(0.32f, 0.92f, 2.10f);
        if (Resolve(10.0f, 11.25f, 7.0f, 10.0f, 3.0f, 2.45f,
                LabLiquid.HoneyShallow, honeyAbsorption, Vector3.Zero)
            || Resolve(11.25f, 12.5f, 7.0f, 10.0f, 3.0f, 1.25f,
                LabLiquid.HoneyDeep, honeyAbsorption, Vector3.Zero))
        {
            liquid = resolvedLiquid;
            depth = resolvedDepth;
            pathLength = resolvedPathLength;
            transmittance = resolvedTransmittance;
            emission = resolvedEmission;
            return true;
        }

        Vector3 lavaAbsorption = new(0.95f, 1.85f, 3.60f);
        Vector3 lavaEmission = new(1.85f, 0.42f, 0.035f);
        if (Resolve(8.0f, 9.0f, 10.25f, 12.5f, 3.0f, 2.35f,
                LabLiquid.LavaShallow, lavaAbsorption, lavaEmission)
            || Resolve(9.0f, 10.0f, 10.25f, 12.5f, 3.0f, 1.00f,
                LabLiquid.LavaDeep, lavaAbsorption, lavaEmission))
        {
            liquid = resolvedLiquid;
            depth = resolvedDepth;
            pathLength = resolvedPathLength;
            transmittance = resolvedTransmittance;
            emission = resolvedEmission;
            return true;
        }

        bool confined = Resolve(18.0f, 19.0f, 12.5f, 13.5f, 3.25f, 2.85f,
            LabLiquid.ConfinedWater, waterAbsorption, Vector3.Zero);
        if (confined)
        {
            liquid = resolvedLiquid;
            depth = resolvedDepth;
            pathLength = resolvedPathLength;
            transmittance = resolvedTransmittance;
            emission = resolvedEmission;
        }
        return confined;
    }

    /// <summary>
    /// Attempts to confined Liquid Candidate without throwing when fixture data cannot satisfy the requested path.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <returns>True when the requested fixture operation succeeds; otherwise false.</returns>
    private static bool TryConfinedLiquidCandidate(Vector3 origin, Vector3 direction) =>
        PlaneY(origin, direction, 3.25f, 17.75f, 19.25f, 12.25f, 13.75f, out _);

    /// <summary>
    /// Determines whether is Liquid Footprint holds for the current synthetic fixture state.
    /// </summary>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool IsLiquidFootprint(float x, float z) =>
        x is >= 14.0f and < 19.0f && z is >= 7.0f and < 11.0f
        || x is >= 10.0f and < 12.5f && z is >= 7.0f and < 10.0f
        || x is >= 8.0f and < 10.0f && z is >= 10.25f and < 12.5f
        || x is >= 18.0f and < 19.0f && z is >= 12.5f and < 13.5f;

    /// <summary>
    /// Represents the hit Consumer callback used to isolate the test from live runtime services.
    /// </summary>
    /// <param name="distance">The distance input used to configure this deterministic test path.</param>
    /// <param name="normal">The normal input used to configure this deterministic test path.</param>
    /// <param name="albedo">The albedo input used to configure this deterministic test path.</param>
    /// <param name="roughness">The roughness input used to configure this deterministic test path.</param>
    /// <param name="metallic">The metallic input used to configure this deterministic test path.</param>
    /// <param name="emissive">The emissive input used to configure this deterministic test path.</param>
    /// <param name="vegetation">The vegetation input used to configure this deterministic test path.</param>
    /// <param name="water">The water input used to configure this deterministic test path.</param>
    /// <param name="surfaceIdentity">Explicit authored identity used to keep object-specific metrics disjoint.</param>
    private delegate void HitConsumer(
        float distance,
        Vector3 normal,
        Vector3 albedo,
        float roughness,
        float metallic,
        float emissive,
        bool vegetation,
        bool water,
        LabSurfaceIdentity surfaceIdentity);

    /// <summary>
    /// Traces anvil in the deterministic reference scene and returns the nearest valid synthetic hit.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <param name="accept">The accept input used to configure this deterministic test path.</param>
    private static void TraceAnvil(Vector3 origin, Vector3 direction, HitConsumer accept)
    {
        // A metallic base colour represents conductor reflectance rather than
        // diffuse charcoal. Use a neutral iron/steel F0 reference so the
        // absolute-energy assertion exercises a physically plausible authored
        // material instead of compensating for an artificially black fixture.
        Vector3 metal = new(0.56f, 0.57f, 0.58f);
        TraceBox(origin, direction, new Vector3(12.5f, 2.0f, 13.6f), new Vector3(14.5f, 2.3f, 14.6f), metal, 0.24f, 0.88f, accept, surfaceIdentity: LabSurfaceIdentity.AnvilMetal);
        TraceBox(origin, direction, new Vector3(13.05f, 2.3f, 13.75f), new Vector3(13.95f, 2.95f, 14.45f), metal, 0.23f, 0.90f, accept, surfaceIdentity: LabSurfaceIdentity.AnvilMetal);
        TraceBox(origin, direction, new Vector3(12.35f, 2.95f, 13.55f), new Vector3(14.75f, 3.25f, 14.55f), metal, 0.20f, 0.94f, accept, surfaceIdentity: LabSurfaceIdentity.AnvilMetal);
        TraceBox(origin, direction, new Vector3(14.7f, 3.0f, 13.75f), new Vector3(15.35f, 3.2f, 14.35f), metal, 0.18f, 0.95f, accept, surfaceIdentity: LabSurfaceIdentity.AnvilMetal);
    }

    /// <summary>
    /// Traces lantern in the deterministic reference scene and returns the nearest valid synthetic hit.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <param name="accept">The accept input used to configure this deterministic test path.</param>
    private static void TraceLantern(Vector3 origin, Vector3 direction, HitConsumer accept)
    {
        Vector3 cage = new(0.34f, 0.25f, 0.16f);
        TraceBox(origin, direction, new Vector3(9.08f, 4.95f, 15.08f), new Vector3(9.92f, 5.10f, 15.92f), cage, 0.20f, 0.82f, accept, surfaceIdentity: LabSurfaceIdentity.LanternCageMetal);
        TraceBox(origin, direction, new Vector3(9.12f, 5.92f, 15.12f), new Vector3(9.88f, 6.10f, 15.88f), cage, 0.20f, 0.82f, accept, surfaceIdentity: LabSurfaceIdentity.LanternCageMetal);
        foreach ((float x, float z) in new[] { (9.12f, 15.12f), (9.80f, 15.12f), (9.12f, 15.80f), (9.80f, 15.80f) })
        {
            TraceBox(origin, direction, new Vector3(x, 5.08f, z), new Vector3(x + 0.08f, 5.96f, z + 0.08f), cage, 0.18f, 0.85f, accept, surfaceIdentity: LabSurfaceIdentity.LanternCageMetal);
        }
        TraceBox(origin, direction, new Vector3(9.34f, 5.25f, 15.34f), new Vector3(9.66f, 5.72f, 15.66f),
            LightColor, 0.30f, 0.0f, accept, emissive: 1.0f);
    }

    /// <summary>
    /// Traces grass in the deterministic reference scene and returns the nearest valid synthetic hit.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <param name="accept">The accept input used to configure this deterministic test path.</param>
    private static void TraceGrass(Vector3 origin, Vector3 direction, HitConsumer accept)
    {
        TraceCrossPlane(origin, direction, false, accept);
        TraceCrossPlane(origin, direction, true, accept);
    }

    /// <summary>
    /// Traces cross Plane in the deterministic reference scene and returns the nearest valid synthetic hit.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <param name="opposite">The opposite input used to configure this deterministic test path.</param>
    /// <param name="accept">The accept input used to configure this deterministic test path.</param>
    private static void TraceCrossPlane(Vector3 origin, Vector3 direction, bool opposite, HitConsumer accept)
    {
        Vector3 planeNormal = Vector3.Normalize(opposite
            ? new Vector3(1, 0, 1)
            : new Vector3(1, 0, -1));
        Vector3 center = new(7.5f, 2.0f, 12.5f);
        float denominator = Vector3.Dot(direction, planeNormal);
        if (Math.Abs(denominator) < 0.0001f)
        {
            return;
        }
        float distance = Vector3.Dot(center - origin, planeNormal) / denominator;
        if (distance <= 0.001f)
        {
            return;
        }
        Vector3 point = origin + direction * distance;
        if (point.Y < GrassEnvelopeMinimum.Y || point.Y > GrassEnvelopeMaximum.Y
            || point.X < 7.0f || point.X > 8.0f
            || point.Z < 12.0f || point.Z > 13.0f)
        {
            return;
        }
        float normalizedHeight = point.Y - GrassEnvelopeMinimum.Y;
        if (!GrassTexelIsOpaque(point, normalizedHeight))
        {
            return;
        }
        if (Vector3.Dot(planeNormal, -direction) < 0.0f)
        {
            planeNormal = -planeNormal;
        }
        accept(distance, planeNormal, new Vector3(0.22f, 0.50f, 0.13f), 0.74f, 0.0f, 0.0f, true, false, LabSurfaceIdentity.None);
    }

    /// <summary>
    /// Traces box in the deterministic reference scene and returns the nearest valid synthetic hit.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <param name="minimum">The minimum input used to configure this deterministic test path.</param>
    /// <param name="maximum">The maximum input used to configure this deterministic test path.</param>
    /// <param name="color">The color input used to configure this deterministic test path.</param>
    /// <param name="roughness">The roughness input used to configure this deterministic test path.</param>
    /// <param name="metallic">The metallic input used to configure this deterministic test path.</param>
    /// <param name="accept">The accept input used to configure this deterministic test path.</param>
    /// <param name="emissive">The emissive input used to configure this deterministic test path.</param>
    /// <param name="surfaceIdentity">Explicit authored identity propagated to the visible hit.</param>
    private static void TraceBox(
        Vector3 origin,
        Vector3 direction,
        Vector3 minimum,
        Vector3 maximum,
        Vector3 color,
        float roughness,
        float metallic,
        HitConsumer accept,
        float emissive = 0.0f,
        LabSurfaceIdentity surfaceIdentity = LabSurfaceIdentity.None)
    {
        if (RayBox(origin, direction, minimum, maximum, out float distance, out Vector3 normal))
        {
            accept(distance, normal, color, roughness, metallic, emissive, false, false, surfaceIdentity);
        }
    }

    /// <summary>
    /// Executes the ray Box step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <param name="minimum">The minimum input used to configure this deterministic test path.</param>
    /// <param name="maximum">The maximum input used to configure this deterministic test path.</param>
    /// <param name="distance">The distance input used to configure this deterministic test path.</param>
    /// <param name="normal">The normal input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool RayBox(Vector3 origin, Vector3 direction, Vector3 minimum, Vector3 maximum, out float distance, out Vector3 normal)
    {
        float near = float.NegativeInfinity;
        float far = float.PositiveInfinity;
        normal = Vector3.Zero;
        for (int axis = 0; axis < 3; axis++)
        {
            float originAxis = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float directionAxis = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            float minimumAxis = axis == 0 ? minimum.X : axis == 1 ? minimum.Y : minimum.Z;
            float maximumAxis = axis == 0 ? maximum.X : axis == 1 ? maximum.Y : maximum.Z;
            if (Math.Abs(directionAxis) < 0.000001f)
            {
                if (originAxis < minimumAxis || originAxis > maximumAxis)
                {
                    distance = 0.0f;
                    return false;
                }
                continue;
            }
            float first = (minimumAxis - originAxis) / directionAxis;
            float second = (maximumAxis - originAxis) / directionAxis;
            float sign = -MathF.Sign(directionAxis);
            if (first > second)
            {
                (first, second) = (second, first);
                sign = -sign;
            }
            if (first > near)
            {
                near = first;
                normal = axis switch
                {
                    0 => new Vector3(sign, 0, 0),
                    1 => new Vector3(0, sign, 0),
                    _ => new Vector3(0, 0, sign)
                };
            }
            far = Math.Min(far, second);
            if (near > far)
            {
                distance = 0.0f;
                return false;
            }
        }
        distance = near > 0.001f ? near : far;
        return distance > 0.001f;
    }

    /// <summary>
    /// Executes the plane Y step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <param name="y">Coordinate component in the space defined by the tested API.</param>
    /// <param name="minX">Coordinate component in the space defined by the tested API.</param>
    /// <param name="maxX">Coordinate component in the space defined by the tested API.</param>
    /// <param name="minZ">Coordinate component in the space defined by the tested API.</param>
    /// <param name="maxZ">Coordinate component in the space defined by the tested API.</param>
    /// <param name="distance">The distance input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool PlaneY(Vector3 origin, Vector3 direction, float y, float minX, float maxX, float minZ, float maxZ, out float distance)
    {
        distance = Math.Abs(direction.Y) > 0.00001f ? (y - origin.Y) / direction.Y : -1.0f;
        Vector3 point = origin + direction * distance;
        return distance > 0.001f && point.X >= minX && point.X <= maxX && point.Z >= minZ && point.Z <= maxZ;
    }

    /// <summary>
    /// Executes the plane X step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="minY">Coordinate component in the space defined by the tested API.</param>
    /// <param name="maxY">Coordinate component in the space defined by the tested API.</param>
    /// <param name="minZ">Coordinate component in the space defined by the tested API.</param>
    /// <param name="maxZ">Coordinate component in the space defined by the tested API.</param>
    /// <param name="distance">The distance input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool PlaneX(Vector3 origin, Vector3 direction, float x, float minY, float maxY, float minZ, float maxZ, out float distance)
    {
        distance = Math.Abs(direction.X) > 0.00001f ? (x - origin.X) / direction.X : -1.0f;
        Vector3 point = origin + direction * distance;
        return distance > 0.001f && point.Y >= minY && point.Y <= maxY && point.Z >= minZ && point.Z <= maxZ;
    }

    /// <summary>
    /// Executes the plane Z step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="origin">The origin input used to configure this deterministic test path.</param>
    /// <param name="direction">The direction input used to configure this deterministic test path.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <param name="minX">Coordinate component in the space defined by the tested API.</param>
    /// <param name="maxX">Coordinate component in the space defined by the tested API.</param>
    /// <param name="minY">Coordinate component in the space defined by the tested API.</param>
    /// <param name="maxY">Coordinate component in the space defined by the tested API.</param>
    /// <param name="distance">The distance input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool PlaneZ(Vector3 origin, Vector3 direction, float z, float minX, float maxX, float minY, float maxY, out float distance)
    {
        distance = Math.Abs(direction.Z) > 0.00001f ? (z - origin.Z) / direction.Z : -1.0f;
        Vector3 point = origin + direction * distance;
        return distance > 0.001f && point.X >= minX && point.X <= maxX && point.Y >= minY && point.Y <= maxY;
    }

    /// <summary>
    /// Executes the perturb Normal step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="position">The position input used to configure this deterministic test path.</param>
    /// <param name="normal">The normal input used to configure this deterministic test path.</param>
    /// <param name="roughness">The roughness input used to configure this deterministic test path.</param>
    /// <param name="vegetation">The vegetation input used to configure this deterministic test path.</param>
    /// <param name="water">The water input used to configure this deterministic test path.</param>
    /// <returns>The perturb Normal result consumed by the caller&apos;s assertion.</returns>
    private static Vector3 PerturbNormal(Vector3 position, Vector3 normal, float roughness, bool vegetation, bool water)
    {
        if (vegetation || water || roughness < 0.5f)
        {
            return normal;
        }
        Vector3 tangent = Math.Abs(normal.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
        Vector3 bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));
        tangent = Vector3.Normalize(Vector3.Cross(bitangent, normal));
        float brickX = MathF.Sin(Vector3.Dot(position, tangent) * 12.0f);
        float brickY = MathF.Sin(Vector3.Dot(position, bitangent) * 8.0f + MathF.Floor(Vector3.Dot(position, tangent) * 2.0f) * 1.7f);
        return Vector3.Normalize(normal + tangent * brickX * 0.075f + bitangent * brickY * 0.065f);
    }

    /// <summary>
    /// Executes the inside step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="point">The point input used to configure this deterministic test path.</param>
    /// <param name="minimum">The minimum input used to configure this deterministic test path.</param>
    /// <param name="maximum">The maximum input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool Inside(Vector3 point, Vector3 minimum, Vector3 maximum) =>
        point.X >= minimum.X && point.X < maximum.X
        && point.Y >= minimum.Y && point.Y < maximum.Y
        && point.Z >= minimum.Z && point.Z < maximum.Z;

    /// <summary>
    /// Writes color into caller-owned fixture storage without retaining the destination buffer.
    /// </summary>
    /// <param name="target">The target input used to configure this deterministic test path.</param>
    /// <param name="index">Coordinate component in the space defined by the tested API.</param>
    /// <param name="color">The color input used to configure this deterministic test path.</param>
    private static void WriteColor(byte[] target, int index, Vector3 color)
    {
        target[index] = ToByte(color.X);
        target[index + 1] = ToByte(color.Y);
        target[index + 2] = ToByte(color.Z);
        target[index + 3] = 255;
    }

    /// <summary>
    /// Executes the tone Source step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <returns>The tone Source result consumed by the caller&apos;s assertion.</returns>
    private static Vector3 ToneSource(Vector3 value) => new(
        value.X / (1.0f + value.X),
        value.Y / (1.0f + value.Y),
        value.Z / (1.0f + value.Z));

    /// <summary>
    /// Executes the to Byte step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <returns>The to Byte result consumed by the caller&apos;s assertion.</returns>
    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f), 0, 255);

    /// <summary>
    /// Executes the luminance step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="color">The color input used to configure this deterministic test path.</param>
    /// <returns>The luminance result consumed by the caller&apos;s assertion.</returns>
    private static float Luminance(Vector3 color) =>
        color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;

    /// <summary>
    /// Executes the pack Surface Alpha step used by the deterministic synthetic Scene fixture.
    /// </summary>
    /// <param name="roughness">The roughness input used to configure this deterministic test path.</param>
    /// <param name="luminance">The luminance input used to configure this deterministic test path.</param>
    /// <returns>The pack Surface Alpha result consumed by the caller&apos;s assertion.</returns>
    private static float PackSurfaceAlpha(float roughness, float luminance)
    {
        int roughnessBits = Math.Clamp((int)MathF.Floor(roughness * 32.0f), 0, 31);
        int albedoBits = Math.Clamp((int)MathF.Floor(luminance * 32.0f), 0, 31);
        return (roughnessBits * 32 + albedoBits + 1.0f) / 1025.0f;
    }
}
