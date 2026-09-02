using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace VintageRTX.Tools
{
    /// <summary>
    /// Utility to generate normal maps from diffuse textures
    /// </summary>
    public class NormalMapGenerator
    {
        /// <summary>
        /// Main entry point for normal map generation
        /// </summary>
        public static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: GenerateNormalMaps <input_directory> <output_directory> [strength]");
                Console.WriteLine("Example: GenerateNormalMaps textures/block textures/normalmaps/block 1.0");
                return;
            }

            string inputDir = args[0];
            string outputDir = args[1];
            float strength = args.Length > 2 ? float.Parse(args[2]) : 1.0f;

            if (!Directory.Exists(inputDir))
            {
                Console.WriteLine($"Input directory not found: {inputDir}");
                return;
            }

            Directory.CreateDirectory(outputDir);

            var generator = new NormalMapGenerator();
            generator.ProcessDirectory(inputDir, outputDir, strength);
        }

        /// <summary>
        /// Processes all textures in a directory
        /// </summary>
        public void ProcessDirectory(string inputDir, string outputDir, float strength)
        {
            var files = Directory.GetFiles(inputDir, "*.png", SearchOption.AllDirectories);

            Console.WriteLine($"Found {files.Length} textures to process");

            foreach (var file in files)
            {
                try
                {
                    string relativePath = Path.GetRelativePath(inputDir, file);
                    string outputPath = Path.Combine(outputDir, Path.GetDirectoryName(relativePath));
                    Directory.CreateDirectory(outputPath);

                    string fileName = Path.GetFileNameWithoutExtension(file);
                    string outputFile = Path.Combine(outputPath, fileName + "_n.png");

                    // Skip if already a normal map
                    if (fileName.EndsWith("_n") || fileName.EndsWith("_normal"))
                        continue;

                    Console.WriteLine($"Processing: {relativePath}");

                    using (var bitmap = new Bitmap(file))
                    {
                        var normalMap = GenerateNormalMap(bitmap, strength);
                        normalMap.Save(outputFile, ImageFormat.Png);
                        normalMap.Dispose();
                    }

                    Console.WriteLine($"  -> Saved: {outputFile}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing {file}: {ex.Message}");
                }
            }

            Console.WriteLine("Normal map generation complete!");
        }

        /// <summary>
        /// Generates a normal map from a diffuse texture using Sobel filter
        /// </summary>
        public Bitmap GenerateNormalMap(Bitmap source, float strength)
        {
            int width = source.Width;
            int height = source.Height;
            var normalMap = new Bitmap(width, height);

            // Sobel kernels
            float[,] sobelX = new float[,] {
                { -1, 0, 1 },
                { -2, 0, 2 },
                { -1, 0, 1 }
            };

            float[,] sobelY = new float[,] {
                { -1, -2, -1 },
                {  0,  0,  0 },
                {  1,  2,  1 }
            };

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float gx = 0, gy = 0;

                    // Apply Sobel filter
                    for (int ky = -1; ky <= 1; ky++)
                    {
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            int px = Math.Clamp(x + kx, 0, width - 1);
                            int py = Math.Clamp(y + ky, 0, height - 1);

                            Color pixel = source.GetPixel(px, py);
                            float gray = (pixel.R + pixel.G + pixel.B) / 3.0f / 255.0f;

                            gx += gray * sobelX[ky + 1, kx + 1];
                            gy += gray * sobelY[ky + 1, kx + 1];
                        }
                    }

                    // Scale by strength
                    gx *= strength;
                    gy *= strength;

                    // Calculate normal
                    var normal = Normalize(new Vector3(-gx, -gy, 1.0f));

                    // Convert to color (0-255 range)
                    int r = (int)((normal.X * 0.5f + 0.5f) * 255);
                    int g = (int)((normal.Y * 0.5f + 0.5f) * 255);
                    int b = (int)((normal.Z * 0.5f + 0.5f) * 255);

                    normalMap.SetPixel(x, y, Color.FromArgb(255, r, g, b));
                }
            }

            return normalMap;
        }

        /// <summary>
        /// Normalizes a 3D vector
        /// </summary>
        private Vector3 Normalize(Vector3 v)
        {
            float length = (float)Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            if (length > 0)
            {
                return new Vector3(v.X / length, v.Y / length, v.Z / length);
            }
            return v;
        }

        /// <summary>
        /// Simple 3D vector struct
        /// </summary>
        private struct Vector3
        {
            public float X, Y, Z;

            public Vector3(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }
    }
}