# VintageRTX Changelog

## [Unreleased]

### Added
- Initial release of VintageRTX
- Normal mapping support for all block and entity textures
- Screen Space Reflections (SSR) for water and reflective surfaces
- Multi-language support (English and French)
- Quality presets (Low, Medium, High, Ultra)
- Adaptive quality system based on FPS target
- Custom shader loading system
- PBR texture support (normal, roughness, metallic maps)
- Configuration system with hot reload
- Debug commands for development

### Technical Features
- Harmony patching for shader interception
- Deferred rendering pipeline preparation
- Custom framebuffer management
- Texture manager for PBR assets
- Shader modification system
- Real-time shader reloading

## [1.0.0] - TBD

### Core Features
- **Normal Mapping**
  - Automatic detection of normal map textures
  - Configurable strength (0.0 - 2.0)
  - Support for custom normal maps
  - TBN matrix calculation for accurate lighting

- **Screen Space Reflections**
  - Ray marching implementation
  - Binary search refinement
  - Configurable quality levels
  - Fade distance control
  - Performance optimizations

- **Configuration**
  - JSON-based configuration
  - In-game commands
  - Quality presets
  - Per-feature toggles

### Supported Shaders
- standard (entities and items)
- chunkopaque (terrain)
- chunkliquid (water)
- final (post-processing)

### Commands
- `/rtx status` - Display current settings
- `/rtx toggle` - Enable/disable all effects
- `/rtx preset [quality]` - Change quality preset
- `/rtx reload` - Reload shaders
- `/rtx config` - Reload configuration

### Texture Support
- Normal maps: `*_n.png`
- Roughness maps: `*_r.png`
- Metallic maps: `*_m.png`

## Roadmap

### Version 1.1.0
- [ ] Screen-space ray traced global illumination
- [ ] Improved water rendering with caustics
- [ ] Volumetric fog integration
- [ ] Shadow quality improvements
- [ ] Performance optimizations

### Version 1.2.0
- [ ] Full PBR shading model
- [ ] Environment mapping
- [ ] Subsurface scattering for leaves
- [ ] Temporal anti-aliasing (TAA)
- [ ] Motion blur

### Version 2.0.0
- [ ] Complete render pipeline replacement
- [ ] Ray traced shadows
- [ ] Advanced atmospheric scattering
- [ ] HDR rendering pipeline
- [ ] Tone mapping options

## Known Issues

### Current Limitations
- SSR only works on screen-visible objects
- No reflections beyond screen borders
- Performance impact on older GPUs
- Some third-party shaders may conflict

### Bugs
- Water reflections may flicker at certain angles
- Normal maps may appear inverted on some textures
- Adaptive quality may oscillate near target FPS

## Performance Guidelines

### Minimum Requirements
- GPU: GTX 960 / RX 470 or better
- VRAM: 2GB
- OpenGL: 3.3 support

### Recommended Requirements
- GPU: GTX 1060 / RX 580 or better
- VRAM: 4GB
- OpenGL: 4.5 support

### Performance Tips
1. Start with Medium preset
2. Disable SSR for +20-30% FPS
3. Reduce normal map strength for minor gains
4. Use adaptive quality for automatic adjustment

## Development Notes

### Building from Source
See `docs/Build-and-Test-Guide.md`

### Contributing
- Follow existing code style
- Test on multiple quality settings
- Include performance impact notes
- Update documentation

### Architecture
- Harmony for runtime patching
- Minimal core game modifications
- Extensible shader system
- Modular feature implementation

## Credits

### Development
- Lead Developer: [Your Name]
- Contributors: [List contributors]

### Libraries Used
- Harmony by Andreas Pardeike
- OpenTK by the OpenTK Team
- Newtonsoft.Json by James Newton-King

### Special Thanks
- Vintage Story development team
- Community testers
- Tutorial and guide authors

## License

This project is licensed under the MIT License - see LICENSE file for details.

---

For bug reports and feature requests, please use the GitHub issue tracker.
For support, visit the Vintage Story forums or Discord.