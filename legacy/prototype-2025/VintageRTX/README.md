# VintageRTX - Advanced Graphics Enhancement Mod for Vintage Story

## Description
VintageRTX brings advanced graphics enhancements to Vintage Story, including:
- **Normal Mapping**: Adds realistic surface detail
- **Screen Space Reflections (SSR)**: Real-time dynamic reflections
- **Screen-space Ray Tracing**: Enhanced lighting and shadows

## Requirements
- Vintage Story version 1.20.11
- .NET 6.0 SDK
- Visual Studio 2022 or Visual Studio Code
- OpenGL 3.3+ compatible graphics card

## Installation

### 1. Download Dependencies
```bash
# Download Harmony
curl -L https://github.com/pardeike/Harmony/releases/download/v2.2.2.0/Harmony.2.2.2.0.zip -o harmony.zip
# Extract 0Harmony.dll to lib/ folder
```

### 2. Build the Mod
```bash
# In project directory
dotnet build -c Release
```

### 3. Manual Installation
1. Copy `VintageRTX.dll` to `%appdata%/Vintagestory/Mods/`
2. Copy the `assets/` folder to `%appdata%/Vintagestory/Mods/VintageRTX/`
3. Copy `0Harmony.dll` to `%appdata%/Vintagestory/Mods/VintageRTX/`

## Usage

### In-Game Commands
- `/rtx status` - Display effect status
- `/rtx toggle` - Enable/disable RTX effects
- `/rtx reload` - Reload modified shaders
- `/rtx preset [low|medium|high|ultra]` - Change quality preset
- `/rtx config` - Reload configuration

### Configuration
The mod creates a configuration file at `ModConfig/vintagertx.json`:

```json
{
  "EnableNormalMapping": true,
  "NormalMapStrength": 1.0,
  "EnableSSR": true,
  "SSRQuality": "medium",
  "SSRMaxSteps": 32,
  "EnableRayTracing": false,
  "AdaptiveQuality": true,
  "TargetFPS": 60
}
```

## Custom Shaders

### Shader Directory Structure
```
assets/vintagertx/shaders/
├── standard.fsh      # Main object fragment shader
├── standard.vsh      # Main object vertex shader
├── final.fsh         # Post-processing & SSR
└── final.vsh         # Post-processing vertex
```

### Adding Custom Shaders
1. Place shader files in `assets/vintagertx/shaders/`
2. Use same naming as vanilla shaders
3. The mod will automatically load custom versions

## Development

### Project Structure
```
VintageRTX/
├── src/
│   ├── VintageRTXMod.cs      # Main entry point
│   ├── ShaderPatcher.cs      # Harmony patching system
│   ├── ShaderManager.cs      # Shader management
│   └── ConfigHandler.cs      # Configuration handling
├── assets/
│   └── vintagertx/
│       ├── shaders/          # Custom shader files
│       ├── lang/             # Translations
│       └── textures/         # Normal maps, etc.
└── lib/
    └── 0Harmony.dll         # Harmony library
```

### Adding New Shaders
1. Add shader name to `targetShaders` in `ShaderManager.cs`
2. Create new `ShaderModification` in `InitializeShaderModifications()`
3. Implement vertex/fragment modifications
4. Place custom shader files in `assets/vintagertx/shaders/`

### Debugging
- Enable debug logs in game options
- Mod logs are prefixed with `[VintageRTX]`
- Use `/rtx reload` to test changes without restart
- Check `VintagestoryData/Logs/` for detailed logs

## Performance Tips
- Start with "low" preset on older hardware
- Disable SSR for better performance
- Adaptive quality automatically adjusts settings
- Monitor FPS with F3 debug overlay

## Localization
The mod supports multiple languages:
- English: `assets/vintagertx/lang/en.json`
- French: `assets/vintagertx/lang/fr.json`
- Add your language by creating a new JSON file

## Known Issues
- Performance impact on lower-end systems
- Some third-party shader mods may be incompatible
- SSR may show artifacts on highly reflective surfaces

## Roadmap
- [x] Basic normal mapping
- [x] Configuration system
- [x] Localization support
- [ ] Complete SSR implementation
- [ ] Screen-space ray traced lighting
- [ ] PBR texture support
- [ ] In-game configuration UI
- [ ] Performance profiling tools

## Contributing
Contributions are welcome! Please:
1. Fork the project
2. Create a feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit changes (`git commit -m 'Add AmazingFeature'`)
4. Push to branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## License
This project is licensed under the MIT License. See LICENSE file for details.

## Credits
- Harmony library by Andreas Pardeike
- Vintage Story by Tyron & Anego Studios