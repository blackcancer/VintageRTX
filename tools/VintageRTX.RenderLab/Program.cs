using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace VintageRTX.RenderLab;

/// <summary>
/// Hosts the command-line test runner and maps any failed contract to a non-zero process exit code.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs every registered check and returns zero only when all test contracts pass.
    /// </summary>
    /// <param name="args">The args input used to configure this deterministic test path.</param>
    /// <returns>Zero when every registered contract passes; otherwise a non-zero process exit code.</returns>
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            RenderLabOptions options = RenderLabOptions.Parse(args);
            RenderLabReport report = RenderLabRunner.Run(options);
            Console.WriteLine($"PASS standalone render lab: {report.FinalCapture}");
            Console.WriteLine(
                $"GPU average={report.AverageGpuMilliseconds:0.000}ms, p99={report.P99GpuMilliseconds:0.000}ms, equivalent 1% low={report.OnePercentLowFps:0.0} FPS.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {exception}");
            return 1;
        }
    }
}

/// <summary>
/// Supports render Lab Runner within the deterministic VintageRTX test infrastructure.
/// </summary>
internal static class RenderLabRunner
{
    /// <summary>
    /// Executes requested fixture operation as an isolated test step and propagates failures to the owning suite.
    /// </summary>
    /// <param name="options">The options input used to configure this deterministic test path.</param>
    /// <param name="contextReady">Coordinate component in the space defined by the tested API.</param>
    /// <returns>The run result consumed by the caller&apos;s assertion.</returns>
    public static RenderLabReport Run(RenderLabOptions options, Action? contextReady = null)
    {
        NativeWindowSettings settings = new()
        {
            API = ContextAPI.OpenGL,
            APIVersion = new Version(4, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,
            ClientSize = new Vector2i(64, 64),
            StartVisible = false,
            StartFocused = false,
            AutoLoadBindings = false,
            NumberOfSamples = 0,
            SrgbCapable = true,
            Title = "VintageRTX.RenderLab"
        };

        using NativeWindow window = new(settings);
        window.MakeCurrent();
        GL.LoadBindings(new GLFWBindingsContext());

        int major = GL.GetInteger(GetPName.MajorVersion);
        int minor = GL.GetInteger(GetPName.MinorVersion);
        Console.WriteLine(
            $"OpenGL {major}.{minor} | {GL.GetString(StringName.Renderer)} | GLSL {GL.GetString(StringName.ShadingLanguageVersion)}");
        if (major < 4 || major == 4 && minor < 3)
        {
            throw new NotSupportedException("OpenGL 4.3 or newer is required.");
        }

        System.Diagnostics.Stopwatch phase = System.Diagnostics.Stopwatch.StartNew();
        contextReady?.Invoke();
        Directory.CreateDirectory(options.OutputDirectory);
        File.WriteAllText(Path.Combine(options.OutputDirectory, "gpu-services-milliseconds.txt"),
            phase.Elapsed.TotalMilliseconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine($"RenderLab phase production-GPU-services: {phase.Elapsed.TotalMilliseconds:0.0} ms");
        Console.Out.Flush();
        using StandaloneRenderer renderer = new(options);
        return renderer.Run();
    }
}

/// <summary>
/// Supports render Lab Options within the deterministic VintageRTX test infrastructure.
/// </summary>
internal sealed record RenderLabOptions(
    int Width,
    int Height,
    int BenchmarkFrames,
    string OutputDirectory)
{
    /// <summary>
    /// Executes the parse step used by the deterministic render Lab Options fixture.
    /// </summary>
    /// <param name="args">The args input used to configure this deterministic test path.</param>
    /// <returns>The parse result consumed by the caller&apos;s assertion.</returns>
    public static RenderLabOptions Parse(string[] args)
    {
        int width = 1280;
        int height = 720;
        int frames = 180;
        string? output = null;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--width" when index + 1 < args.Length:
                    width = int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--height" when index + 1 < args.Length:
                    height = int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--frames" when index + 1 < args.Length:
                    frames = int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--output" when index + 1 < args.Length:
                    output = Path.GetFullPath(args[++index]);
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete option '{args[index]}'.");
            }
        }

        width = Math.Clamp(width, 320, 3840);
        height = Math.Clamp(height, 180, 2160);
        frames = Math.Clamp(frames, 60, 1200);
        output ??= Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "artifacts",
            "standalone",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        return new RenderLabOptions(width, height, frames, output);
    }

    /// <summary>
    /// Executes the find Repository Root step used by the deterministic render Lab Options fixture.
    /// </summary>
    /// <returns>The find Repository Root result consumed by the caller&apos;s assertion.</returns>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VintageRTX.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("VintageRTX.sln was not found above the render-lab binary.");
    }
}
