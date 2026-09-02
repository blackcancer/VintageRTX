# VintageRTX Shader Examples

## Custom Shader Examples

### 1. Enhanced Bloom Effect (bloom.fsh)
```glsl
#version 330 core

uniform sampler2D sceneColor;
uniform float bloomThreshold = 0.8;
uniform float bloomIntensity = 1.0;
uniform int bloomRadius = 5;

in vec2 v_texCoord;
out vec4 fragColor;

vec3 getBloom(vec2 uv) {
    vec3 bloom = vec3(0.0);
    float total = 0.0;
    
    for (int x = -bloomRadius; x <= bloomRadius; x++) {
        for (int y = -bloomRadius; y <= bloomRadius; y++) {
            vec2 offset = vec2(x, y) * 0.002;
            vec3 color = texture(sceneColor, uv + offset).rgb;
            
            // Extract bright areas
            float brightness = dot(color, vec3(0.299, 0.587, 0.114));
            if (brightness > bloomThreshold) {
                float weight = 1.0 / (1.0 + length(vec2(x, y)));
                bloom += color * weight;
                total += weight;
            }
        }
    }
    
    return bloom / total * bloomIntensity;
}

void main() {
    vec3 color = texture(sceneColor, v_texCoord).rgb;
    vec3 bloom = getBloom(v_texCoord);
    
    fragColor = vec4(color + bloom, 1.0);
}
```

### 2. Depth of Field Effect (dof.fsh)
```glsl
#version 330 core

uniform sampler2D sceneColor;
uniform sampler2D sceneDepth;
uniform float focusDistance = 10.0;
uniform float focusRange = 5.0;
uniform float blurAmount = 1.0;

in vec2 v_texCoord;
out vec4 fragColor;

float getLinearDepth(float depth) {
    float near = 0.1;
    float far = 1000.0;
    return (2.0 * near) / (far + near - depth * (far - near));
}

vec3 getBlurredColor(vec2 uv, float blur) {
    vec3 color = vec3(0.0);
    float total = 0.0;
    
    int samples = int(blur * 10.0) + 1;
    
    for (int i = -samples; i <= samples; i++) {
        for (int j = -samples; j <= samples; j++) {
            vec2 offset = vec2(i, j) * 0.001 * blur;
            float weight = 1.0 / (1.0 + length(vec2(i, j)));
            
            color += texture(sceneColor, uv + offset).rgb * weight;
            total += weight;
        }
    }
    
    return color / total;
}

void main() {
    float depth = texture(sceneDepth, v_texCoord).r;
    float linearDepth = getLinearDepth(depth) * 100.0;
    
    float blur = abs(linearDepth - focusDistance) / focusRange;
    blur = clamp(blur * blurAmount, 0.0, 1.0);
    
    vec3 color = blur > 0.01 ? 
        getBlurredColor(v_texCoord, blur) : 
        texture(sceneColor, v_texCoord).rgb;
    
    fragColor = vec4(color, 1.0);
}
```

### 3. Motion Blur Effect (motionblur.fsh)
```glsl
#version 330 core

uniform sampler2D sceneColor;
uniform sampler2D velocityBuffer;
uniform float motionBlurStrength = 1.0;
uniform int motionBlurSamples = 8;

in vec2 v_texCoord;
out vec4 fragColor;

void main() {
    vec2 velocity = texture(velocityBuffer, v_texCoord).xy;
    velocity *= motionBlurStrength;
    
    vec3 color = vec3(0.0);
    float total = 0.0;
    
    for (int i = 0; i < motionBlurSamples; i++) {
        float t = float(i) / float(motionBlurSamples - 1);
        vec2 offset = velocity * (t - 0.5);
        
        color += texture(sceneColor, v_texCoord + offset).rgb;
        total += 1.0;
    }
    
    fragColor = vec4(color / total, 1.0);
}
```

### 4. God Rays / Light Shafts (godrays.fsh)
```glsl
#version 330 core

uniform sampler2D sceneColor;
uniform sampler2D sceneDepth;
uniform vec2 lightScreenPos;
uniform float exposure = 0.5;
uniform float decay = 0.96;
uniform float density = 0.5;
uniform float weight = 0.5;
uniform int samples = 64;

in vec2 v_texCoord;
out vec4 fragColor;

void main() {
    vec2 deltaTexCoord = (v_texCoord - lightScreenPos) * density / float(samples);
    vec2 texCoord = v_texCoord;
    
    vec3 color = texture(sceneColor, v_texCoord).rgb;
    vec3 godRays = vec3(0.0);
    
    float illuminationDecay = 1.0;
    
    for (int i = 0; i < samples; i++) {
        texCoord -= deltaTexCoord;
        
        vec3 sample = texture(sceneColor, texCoord).rgb;
        float depth = texture(sceneDepth, texCoord).r;
        
        // Only accumulate if looking at sky
        if (depth >= 0.999) {
            sample *= illuminationDecay * weight;
            godRays += sample;
        }
        
        illuminationDecay *= decay;
    }
    
    godRays *= exposure;
    
    fragColor = vec4(color + godRays, 1.0);
}
```

### 5. FXAA Anti-Aliasing (fxaa.fsh)
```glsl
#version 330 core

uniform sampler2D sceneColor;
uniform vec2 texelSize;

in vec2 v_texCoord;
out vec4 fragColor;

float rgb2luma(vec3 rgb) {
    return dot(rgb, vec3(0.299, 0.587, 0.114));
}

void main() {
    vec3 rgbNW = texture(sceneColor, v_texCoord + vec2(-1.0, -1.0) * texelSize).rgb;
    vec3 rgbNE = texture(sceneColor, v_texCoord + vec2( 1.0, -1.0) * texelSize).rgb;
    vec3 rgbSW = texture(sceneColor, v_texCoord + vec2(-1.0,  1.0) * texelSize).rgb;
    vec3 rgbSE = texture(sceneColor, v_texCoord + vec2( 1.0,  1.0) * texelSize).rgb;
    vec3 rgbM  = texture(sceneColor, v_texCoord).rgb;
    
    float lumaNW = rgb2luma(rgbNW);
    float lumaNE = rgb2luma(rgbNE);
    float lumaSW = rgb2luma(rgbSW);
    float lumaSE = rgb2luma(rgbSE);
    float lumaM  = rgb2luma(rgbM);
    
    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
    
    vec2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));
    
    float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.25 * 0.25, 0.0078125);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    
    dir = min(vec2(8.0), max(vec2(-8.0), dir * rcpDirMin)) * texelSize;
    
    vec3 rgbA = 0.5 * (
        texture(sceneColor, v_texCoord + dir * (1.0/3.0 - 0.5)).rgb +
        texture(sceneColor, v_texCoord + dir * (2.0/3.0 - 0.5)).rgb
    );
    
    vec3 rgbB = rgbA * 0.5 + 0.25 * (
        texture(sceneColor, v_texCoord + dir * -0.5).rgb +
        texture(sceneColor, v_texCoord + dir *  0.5).rgb
    );
    
    float lumaB = rgb2luma(rgbB);
    
    if ((lumaB < lumaMin) || (lumaB > lumaMax)) {
        fragColor = vec4(rgbA, 1.0);
    } else {
        fragColor = vec4(rgbB, 1.0);
    }
}
```

## Integrating Custom Shaders

### 1. Add to ShaderManager
```csharp
// In InitializeShaderModifications()
shaderModifications["bloom"] = new ShaderModification
{
    Name = "bloom",
    UseCustomShader = true,
    AddUniforms = new List<string>
    {
        "uniform float bloomThreshold;",
        "uniform float bloomIntensity;"
    }
};
```

### 2. Register in targetShaders
```csharp
private readonly HashSet<string> targetShaders = new HashSet<string>
{
    "standard",
    "chunkopaque", 
    "chunkliquid",
    "final",
    "ssao",
    "bloom",  // Add new shader
    "dof"     // Add new shader
};
```

### 3. Add Configuration Options
```json
{
  "EnableBloom": true,
  "BloomThreshold": 0.8,
  "BloomIntensity": 1.0,
  
  "EnableDOF": false,
  "DOFFocusDistance": 10.0,
  "DOFBlurAmount": 1.0
}
```

## Shader Debugging Tips

### 1. Visualize Normals
```glsl
#ifdef DEBUG_NORMALS
    fragColor = vec4(normal * 0.5 + 0.5, 1.0);
    return;
#endif
```

### 2. Visualize UV Coordinates
```glsl
#ifdef DEBUG_UV
    fragColor = vec4(v_texCoord, 0.0, 1.0);
    return;
#endif
```

### 3. Visualize Depth Buffer
```glsl
#ifdef DEBUG_DEPTH
    float depth = texture(sceneDepth, v_texCoord).r;
    fragColor = vec4(vec3(depth), 1.0);
    return;
#endif
```

### 4. Performance Timer
```glsl
// At shader start
float startTime = gl_FragCoord.x * 0.001;

// ... shader code ...

// At shader end
float endTime = gl_FragCoord.x * 0.001;
float executionTime = endTime - startTime;
```

## Best Practices

### 1. Minimize Texture Lookups
```glsl
// Bad - multiple lookups
vec3 color1 = texture(tex, uv).rgb;
float alpha1 = texture(tex, uv).a;

// Good - single lookup
vec4 texSample = texture(tex, uv);
vec3 color = texSample.rgb;
float alpha = texSample.a;
```

### 2. Use Constants for Loops
```glsl
// Bad - dynamic loop
for (int i = 0; i < samples; i++) { }

// Good - constant loop
const int SAMPLES = 32;
for (int i = 0; i < SAMPLES; i++) { }
```

### 3. Avoid Branching When Possible
```glsl
// Bad - branching
if (value > 0.5) {
    color = vec3(1.0);
} else {
    color = vec3(0.0);
}

// Good - no branching
color = vec3(step(0.5, value));
```

### 4. Optimize Math Operations
```glsl
// Bad - expensive
vec3 normalized = vector / length(vector);

// Good - built-in
vec3 normalized = normalize(vector);

// Bad - multiple divisions
float a = x / w;
float b = y / w;
float c = z / w;

// Good - one division
float invW = 1.0 / w;
float a = x * invW;
float b = y * invW;
float c = z * invW;
```

## Common Shader Functions

### Utility Functions
```glsl
// Saturate (clamp 0-1)
float saturate(float x) {
    return clamp(x, 0.0, 1.0);
}

// Linear to sRGB
vec3 linearToSRGB(vec3 color) {
    return pow(color, vec3(1.0/2.2));
}

// sRGB to Linear
vec3 sRGBToLinear(vec3 color) {
    return pow(color, vec3(2.2));
}

// Luminance
float luminance(vec3 color) {
    return dot(color, vec3(0.299, 0.587, 0.114));
}

// Contrast adjustment
vec3 adjustContrast(vec3 color, float contrast) {
    return (color - 0.5) * contrast + 0.5;
}
```

### Noise Functions
```glsl
// Simple hash function
float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

// Value noise
float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    
    vec2 u = f * f * (3.0 - 2.0 * f);
    
    return mix(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}
```