# PBR Texture Creation Guide for VintageRTX

## Overview
VintageRTX supports PBR (Physically Based Rendering) textures to enhance the visual quality of Vintage Story. This guide explains how to create and use custom textures with the mod.

## Texture Types

### 1. Normal Maps (_n)
Normal maps add surface detail without additional geometry.

**File naming**: `[texture_name]_n.png`  
**Location**: `assets/vintagertx/textures/normalmaps/`

#### Creating Normal Maps:
- **From Height Maps**: Use tools like CrazyBump, xNormal, or Photoshop
- **From Diffuse**: Use GIMP's normalmap plugin or Photoshop's 3D features
- **Best Practices**:
  - Use tangent-space normal maps
  - Keep the blue channel pointing "up" (positive Z)
  - Test different strengths in-game

### 2. Roughness Maps (_r)
Controls how rough or smooth a surface appears.

**File naming**: `[texture_name]_r.png`  
**Location**: `assets/vintagertx/textures/roughness/`

#### Values:
- Black (0): Perfectly smooth (mirror-like)
- White (255): Completely rough (matte)
- Gray values: Varying roughness

### 3. Metallic Maps (_m)
Defines whether a surface is metallic or dielectric.

**File naming**: `[texture_name]_m.png`  
**Location**: `assets/vintagertx/textures/metallic/`

#### Values:
- Black (0): Non-metallic (stone, wood, fabric)
- White (255): Metallic (iron, copper, gold)
- Generally binary (avoid gray values)

## Directory Structure

```
assets/vintagertx/textures/
├── normalmaps/
│   ├── block/
│   │   ├── stone_n.png
│   │   ├── granite_n.png
│   │   ├── andesite_n.png
│   │   └── ...
│   └── item/
│       ├── ingot-iron_n.png
│       └── ...
├── roughness/
│   └── block/
│       ├── stone_r.png
│       └── ...
└── metallic/
    └── block/
        ├── ore-iron_m.png
        └── ...
```

## Texture Resolution

### Recommended Sizes:
- **Blocks**: 32x32 or 64x64
- **Items**: 16x16 or 32x32
- **Special blocks**: Up to 128x128

### Performance Considerations:
- Higher resolution = more VRAM usage
- Use mipmaps for better performance
- Keep total texture memory under 2GB

## Creating Textures with Common Tools

### GIMP (Free)
1. **Normal Maps**:
   - Filters → Distorts → Normalmap
   - Adjust scale and height
   - Export as PNG

2. **Roughness Maps**:
   - Desaturate the diffuse texture
   - Adjust levels/curves
   - Invert if needed

### Substance Designer/Painter
1. Export textures as PNG
2. Use correct naming convention
3. Ensure linear color space for normal maps

### Photoshop
1. **Normal Maps**:
   - Filter → 3D → Generate Normal Map
   - Adjust settings to taste

2. **PBR Maps**:
   - Use adjustment layers
   - Keep non-destructive workflow

## Example: Creating Stone Textures

### 1. Normal Map for Stone
```
1. Start with stone diffuse texture
2. Convert to grayscale
3. Apply high-pass filter (radius ~2-5)
4. Use normalmap plugin
5. Save as stone_n.png
```

### 2. Roughness Map for Stone
```
1. Use stone diffuse as base
2. Desaturate completely
3. Adjust contrast (stones are usually rough)
4. Brighten overall (higher values = more rough)
5. Save as stone_r.png
```

### 3. Metallic Map for Stone
```
1. Create new image, fill with black
2. Stone is non-metallic
3. Save as stone_m.png
```

## Testing Your Textures

1. Place textures in correct folders
2. Launch game with VintageRTX
3. Use `/rtx reload` to refresh textures
4. Adjust normal map strength with `/rtx config`

## Common Issues

### Normal maps look inverted
- Invert the green channel (Y axis)
- Some tools use different conventions

### Textures not loading
- Check file naming convention
- Ensure PNG format
- Verify folder structure

### Performance drops
- Reduce texture resolution
- Limit PBR maps to important surfaces
- Use texture compression

## Best Practices

1. **Consistency**: Keep similar materials at similar roughness levels
2. **Subtlety**: Less is often more with normal maps
3. **Testing**: Always test in different lighting conditions
4. **Optimization**: Only create PBR maps for frequently seen textures

## Material Reference Values

### Roughness Values:
- Polished metal: 0.0 - 0.2
- Rough metal: 0.3 - 0.7
- Stone/concrete: 0.6 - 0.9
- Wood: 0.5 - 0.8
- Glass: 0.0 - 0.1
- Fabric: 0.7 - 1.0

### Common Materials:
- **Iron**: Metallic=1.0, Roughness=0.4-0.6
- **Gold**: Metallic=1.0, Roughness=0.2-0.3
- **Stone**: Metallic=0.0, Roughness=0.7-0.9
- **Wood**: Metallic=0.0, Roughness=0.6-0.8

## Contributing Textures

To contribute textures to VintageRTX:

1. Follow naming conventions exactly
2. Test thoroughly in-game
3. Provide source files if possible
4. Document any special techniques used
5. Submit via GitHub pull request

## Tools and Resources

### Free Tools:
- [GIMP](https://www.gimp.org/) + normalmap plugin
- [Materialize](http://boundingboxsoftware.com/materialize/)
- [AwesomeBump](https://github.com/kmkolasinski/AwesomeBump)

### Paid Tools:
- [Substance Suite](https://www.substance3d.com/)
- [Quixel Mixer](https://quixel.com/mixer)
- [CrazyBump](http://www.crazybump.com/)

### Learning Resources:
- [LearnOpenGL PBR Theory](https://learnopengl.com/PBR/Theory)
- [Marmoset PBR Guide](https://marmoset.co/posts/basic-theory-of-physically-based-rendering/)
- [Google Filament PBR](https://google.github.io/filament/Filament.html)