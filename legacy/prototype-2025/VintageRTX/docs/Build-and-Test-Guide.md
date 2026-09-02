# VintageRTX Build and Test Guide

## Prerequisites

### Required Software
- **.NET 6.0 SDK**: [Download](https://dotnet.microsoft.com/download/dotnet/6.0)
- **Visual Studio 2022** or **Visual Studio Code** with C# extension
- **Git** for version control
- **Vintage Story 1.20.11** installed

### Required Libraries
- **Harmony 2.2.2.0**: For runtime patching
- **Newtonsoft.Json**: Included via NuGet
- **OpenTK**: Included via NuGet

## Project Setup

### 1. Clone or Create Project Structure
```bash
mkdir VintageRTX
cd VintageRTX

# Create directory structure
mkdir src lib assets docs
mkdir assets/vintagertx
mkdir assets/vintagertx/shaders
mkdir assets/vintagertx/lang
mkdir assets/vintagertx/textures
mkdir assets/vintagertx/textures/normalmaps
mkdir assets/vintagertx/textures/roughness
mkdir assets/vintagertx/textures/metallic
```

### 2. Download Harmony
```bash
# Windows PowerShell
Invoke-WebRequest -Uri "https://github.com/pardeike/Harmony/releases/download/v2.2.2.0/Harmony.2.2.2.0.zip" -OutFile "harmony.zip"
Expand-Archive -Path "harmony.zip" -DestinationPath "harmony_temp"
Copy-Item "harmony_temp/net48/0Harmony.dll" -Destination "lib/"

# Linux/Mac
wget https://github.com/pardeike/Harmony/releases/download/v2.2.2.0/Harmony.2.2.2.0.zip
unzip harmony.zip -d harmony_temp
cp harmony_temp/net48/0Harmony.dll lib/
```

### 3. Copy Source Files
Place all C# files in the `src/` directory:
- `VintageRTXMod.cs`
- `ShaderManager.cs`
- `ShaderPatcher.cs`
- `ConfigHandler.cs`
- `TextureManager.cs`
- `RenderIntegration.cs`
- `Shaders/ShaderModifications.cs`

### 4. Copy Asset Files
- Place shader files in `assets/vintagertx/shaders/`
- Place language files in `assets/vintagertx/lang/`
- Place textures in appropriate subdirectories

## Building the Mod

### Using Command Line
```bash
# Restore NuGet packages
dotnet restore

# Build in Release mode
dotnet build -c Release

# Output will be in bin/Release/net6.0/
```

### Using Visual Studio
1. Open `VintageRTX.csproj`
2. Set configuration to "Release"
3. Build → Build Solution (Ctrl+Shift+B)

### Build Output
After successful build:
```
bin/Release/net6.0/
├── VintageRTX.dll        # Main mod file
├── VintageRTX.pdb        # Debug symbols
└── [other files]         # Dependencies
```

## Creating the Mod Package

### Manual Packaging
```bash
# Create package directory
mkdir package
mkdir package/VintageRTX

# Copy required files
cp bin/Release/net6.0/VintageRTX.dll package/VintageRTX/
cp lib/0Harmony.dll package/VintageRTX/
cp modinfo.json package/VintageRTX/
cp -r assets package/VintageRTX/

# Create zip for distribution (optional)
cd package
zip -r VintageRTX.zip VintageRTX/
```

### Automated Script (Windows)
Use the provided `test-build.bat`:
```batch
test-build.bat
```

### Automated Script (Linux/Mac)
Use the provided `test-build.sh`:
```bash
chmod +x test-build.sh
./test-build.sh
```

## Installation

### Automatic Installation (via build scripts)
The build scripts automatically install to the correct location.

### Manual Installation
1. Navigate to Vintage Story mods folder:
   - Windows: `%appdata%\Vintagestory\Mods\`
   - Linux: `~/.config/VintagestoryData/Mods/`
   - Mac: `~/Library/Application Support/VintagestoryData/Mods/`

2. Copy the entire `VintageRTX` folder

3. Final structure:
```
Mods/
└── VintageRTX/
    ├── VintageRTX.dll
    ├── 0Harmony.dll
    ├── modinfo.json
    └── assets/
```

## Testing the Mod

### 1. Initial Launch Test
1. Start Vintage Story
2. Check main menu → Mods
3. Verify "VintageRTX" appears and is enabled
4. Check logs for `[VintageRTX]` messages

### 2. In-Game Testing
1. Create or load a world
2. Open chat (T key)
3. Test basic commands:
```
/rtx status
/rtx toggle
/rtx preset medium
```

### 3. Visual Testing
1. **Normal Mapping**:
   - Look at stone blocks
   - Should see enhanced surface detail
   - Adjust with `/rtx config`

2. **Water Reflections**:
   - Find water
   - Look for improved reflections
   - Test at different angles

3. **Performance**:
   - Press F3 for debug info
   - Monitor FPS
   - Try different quality presets

### 4. Debug Mode
Enable debug logging:
1. Edit `vintagertx.json`
2. Set `"ShowDebugInfo": true`
3. Restart game
4. Check logs in `VintagestoryData/Logs/client-main.txt`

## Common Issues and Solutions

### Mod Not Loading
- **Check**: Harmony.dll is in the mod folder
- **Check**: Game version is 1.20.11
- **Solution**: Look for errors in `client-main.txt`

### No Visual Changes
- **Check**: `/rtx status` shows effects enabled
- **Check**: Config file exists and is valid JSON
- **Solution**: Try `/rtx toggle` twice to reset

### Performance Issues
- **Solution**: Lower quality preset `/rtx preset low`
- **Solution**: Disable SSR in config
- **Solution**: Reduce normal map strength

### Shader Compilation Errors
- **Check**: Custom shaders have correct syntax
- **Solution**: Delete custom shaders to use built-in modifications
- **Solution**: Check shader error messages in logs

## Development Workflow

### 1. Making Changes
1. Edit source files
2. Build with `dotnet build`
3. Copy DLL to mods folder
4. Restart game or use `/rtx reload`

### 2. Testing Shaders
1. Edit shader files in `assets/vintagertx/shaders/`
2. Use `/rtx reload` in-game
3. Check visual results immediately

### 3. Performance Profiling
1. Use in-game profiler (Shift+F3)
2. Monitor frame times
3. Test on different hardware

## Advanced Testing

### Shader Hot Reload
Enable in config:
```json
{
  "EnableShaderHotReload": true
}
```

Now shaders reload automatically when files change.

### Custom Test Scenarios
1. **Stress Test**:
   - Set ultra quality
   - Visit complex areas
   - Monitor performance

2. **Compatibility Test**:
   - Test with other mods
   - Check for conflicts
   - Document issues

3. **Visual Quality Test**:
   - Compare screenshots
   - Test different lighting
   - Verify improvements

## Release Checklist

Before releasing:
- [ ] Test on clean Vintage Story install
- [ ] Verify all commands work
- [ ] Test all quality presets
- [ ] Update version in modinfo.json
- [ ] Update documentation
- [ ] Create example textures
- [ ] Package with correct structure
- [ ] Test package installation

## Debugging Tips

### Enable Verbose Logging
```csharp
api.Logger.VerboseDebug = true;
```

### Shader Debug Output
Add to fragment shader:
```glsl
#ifdef DEBUG_MODE
    fragColor = vec4(normal * 0.5 + 0.5, 1.0); // Visualize normals
#endif
```

### Performance Monitoring
```csharp
var stopwatch = Stopwatch.StartNew();
// Code to measure
api.Logger.Debug($"Operation took: {stopwatch.ElapsedMilliseconds}ms");
```

## Contributing

### Before Submitting
1. Test thoroughly
2. Follow code style
3. Update documentation
4. Add unit tests if applicable

### Pull Request Process
1. Fork repository
2. Create feature branch
3. Make changes
4. Test extensively
5. Submit PR with description

## Support

### Getting Help
- Check existing issues on GitHub
- Read documentation thoroughly
- Ask on Vintage Story forums
- Contact mod author

### Reporting Issues
Include:
- VintageRTX version
- Vintage Story version
- Full error messages
- Steps to reproduce
- System specifications