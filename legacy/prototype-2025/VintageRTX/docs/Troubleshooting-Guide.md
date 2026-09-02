# VintageRTX Troubleshooting Guide

## Common Issues and Solutions

### Installation Issues

#### "Mod not loading" / "Mod not found"
**Symptoms:**
- VintageRTX doesn't appear in the mod list
- No `[VintageRTX]` messages in logs

**Solutions:**
1. Verify file structure:
   ```
   Mods/
   └── VintageRTX/
       ├── VintageRTX.dll
       ├── 0Harmony.dll    ← REQUIRED!
       ├── modinfo.json
       └── assets/
   ```

2. Check Harmony is present:
   - `0Harmony.dll` MUST be in the VintageRTX folder
   - Download from: https://github.com/pardeike/Harmony/releases

3. Verify game version:
   - Required: Vintage Story 1.20.11 or newer
   - Check version in main menu

#### "Failed to load mod" error
**Check logs:** `VintagestoryData/Logs/client-main.txt`

Common causes:
- Missing .NET 6.0 runtime
- Corrupted DLL files
- Permission issues

### Visual Issues

#### No visual changes
**Solutions:**
1. Check if effects are enabled:
   ```
   /rtx status
   ```

2. Toggle effects:
   ```
   /rtx toggle
   /rtx toggle
   ```

3. Verify shader loading in logs:
   ```
   [VintageRTX] Shader modified: standard
   ```

#### Black screen / Graphical corruption
**Solutions:**
1. Lower quality preset:
   ```
   /rtx preset low
   ```

2. Disable specific features:
   - Edit `vintagertx.json`
   - Set `"EnableSSR": false`
   - Set `"EnableRayTracing": false`

3. Update graphics drivers

#### Inverted normal maps
**Symptoms:**
- Lighting appears inside-out
- Shadows in wrong direction

**Solution:**
- Some normal maps need Y-channel inverted
- Use image editor to invert green channel
- Or adjust in shader: `normalFromMap.y = -normalFromMap.y;`

### Performance Issues

#### Low FPS / Stuttering
**Quick fixes:**
1. Use lower quality preset:
   ```
   /rtx preset low
   ```

2. Disable expensive features:
   ```json
   {
     "EnableSSR": false,
     "EnableRayTracing": false,
     "SSRMaxSteps": 16
   }
   ```

3. Enable adaptive quality:
   ```json
   {
     "AdaptiveQuality": true,
     "TargetFPS": 60
   }
   ```

#### Memory usage too high
**Solutions:**
1. Reduce texture resolution
2. Limit PBR textures to important blocks
3. Clear unused textures:
   ```
   /rtx reload
   ```

### Shader Compilation Errors

#### "Shader compilation failed"
**Check shader syntax:**
1. Look for error in logs:
   ```
   ERROR: 0:123: 'variable' : undeclared identifier
   ```

2. Common issues:
   - Missing semicolons
   - Undefined variables
   - GLSL version mismatch

3. Test with vanilla shaders:
   - Delete custom shaders from `assets/vintagertx/shaders/`
   - Restart game

### Compatibility Issues

#### Conflicts with other mods
**Known conflicts:**
- Other shader mods
- Mods that modify rendering

**Solutions:**
1. Load VintageRTX last
2. Disable conflicting features
3. Report compatibility issues

#### Crashes with specific blocks/items
**Debug steps:**
1. Enable debug mode:
   ```json
   {
     "ShowDebugInfo": true
   }
   ```

2. Identify problematic texture in logs
3. Remove corresponding PBR textures
4. Report issue with texture name

## Debug Commands

### Information Commands
```
/rtx status          # Show current configuration
/rtx debug           # Toggle debug overlay
/rtx shaders         # List loaded shaders
```

### Performance Commands
```
/rtx profile         # Show performance metrics
/rtx benchmark       # Run performance test
```

### Development Commands
```
/rtx reload          # Reload all shaders
/rtx reset           # Reset to defaults
/rtx dump            # Export current shader code
```

## Log Analysis

### Where to find logs
- Windows: `%appdata%\VintagestoryData\Logs\client-main.txt`
- Linux: `~/.config/VintagestoryData/Logs/client-main.txt`
- Mac: `~/Library/Application Support/VintagestoryData/Logs/client-main.txt`

### Important log entries
```
[VintageRTX] Initializing...           # Mod starting
[VintageRTX] Loaded X normal maps      # Texture loading
[VintageRTX] Shader modified: name     # Shader patching
[VintageRTX] Error: message           # Problems
```

### Debug logging
Enable verbose logging:
```json
{
  "ShowDebugInfo": true,
  "EnableShaderHotReload": true
}
```

## Performance Optimization

### GPU Usage
Monitor with:
- NVIDIA: GPU-Z, MSI Afterburner
- AMD: Radeon Software
- Intel: Arc Control

### Recommended Settings by GPU

#### Low-end (GTX 1050, RX 560):
```json
{
  "QualityPreset": "low",
  "EnableNormalMapping": true,
  "NormalMapStrength": 0.5,
  "EnableSSR": false,
  "EnableRayTracing": false
}
```

#### Mid-range (GTX 1660, RX 5600):
```json
{
  "QualityPreset": "medium",
  "EnableNormalMapping": true,
  "NormalMapStrength": 1.0,
  "EnableSSR": true,
  "SSRMaxSteps": 32,
  "EnableRayTracing": false
}
```

#### High-end (RTX 3070, RX 6700 XT):
```json
{
  "QualityPreset": "high",
  "EnableNormalMapping": true,
  "NormalMapStrength": 1.0,
  "EnableSSR": true,
  "SSRMaxSteps": 64,
  "EnableRayTracing": true,
  "RayTracingSamples": 16
}
```

## Reporting Issues

### Before reporting:
1. Update to latest version
2. Test with default configuration
3. Check if issue is already reported
4. Collect system information

### Information to include:
```
VintageRTX Version: X.X.X
Vintage Story Version: X.XX.XX
Operating System: Windows 10/11, Linux, macOS
GPU: Model and driver version
Issue: Clear description
Steps to reproduce: 1, 2, 3...
Error messages: From logs
Screenshots: If visual issue
```

### Where to report:
- GitHub Issues: [Link]
- Vintage Story Forums: [Link]
- Discord: [Link]

## Emergency Recovery

### Complete reset:
1. Delete configuration:
   ```
   ModConfig/vintagertx.json
   ```

2. Remove mod folder:
   ```
   Mods/VintageRTX/
   ```

3. Reinstall fresh copy

### Safe mode:
Create `vintagertx.json` with:
```json
{
  "EnableNormalMapping": false,
  "EnableSSR": false,
  "EnableRayTracing": false
}
```

## FAQ

**Q: Can I use resource packs with VintageRTX?**
A: Yes, VintageRTX works with texture packs. Add matching PBR textures.

**Q: Does this work in multiplayer?**
A: Yes, it's client-side only. No server requirements.

**Q: Can I distribute modified versions?**
A: Yes, under MIT license. Credit original authors.

**Q: Will this break my save?**
A: No, it's purely visual. Saves are unaffected.

**Q: How do I uninstall?**
A: Simply delete the VintageRTX folder from Mods.