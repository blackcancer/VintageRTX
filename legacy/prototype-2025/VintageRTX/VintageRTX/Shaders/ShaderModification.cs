using System.Collections.Generic;

namespace VintageRTX.src.Shaders
{
    /// <summary>
    /// Defines modifications to be applied to shaders
    /// </summary>
    public class ShaderModification
    {
        /// <summary>
        /// Name of the shader to modify
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// List of uniform declarations to add
        /// </summary>
        public List<string> AddUniforms { get; set; }

        /// <summary>
        /// Code to inject into vertex shader main function
        /// </summary>
        public string VertexModifications { get; set; }

        /// <summary>
        /// Code to inject into fragment shader main function
        /// </summary>
        public string FragmentModifications { get; set; }

        /// <summary>
        /// Additional varyings to declare
        /// </summary>
        public List<string> AddVaryings { get; set; }

        /// <summary>
        /// Additional attributes to declare (vertex shader only)
        /// </summary>
        public List<string> AddAttributes { get; set; }

        /// <summary>
        /// Additional functions to add before main
        /// </summary>
        public string AddFunctions { get; set; }

        /// <summary>
        /// Whether to completely replace the shader with custom version
        /// </summary>
        public bool UseCustomShader { get; set; }

        /// <summary>
        /// Initializes a new instance of ShaderModification
        /// </summary>
        public ShaderModification()
        {
            AddUniforms = new List<string>();
            AddVaryings = new List<string>();
            AddAttributes = new List<string>();
        }
    }

    /// <summary>
    /// Predefined shader modifications for common effects
    /// </summary>
    public static class ShaderModificationTemplates
    {
        /// <summary>
        /// Creates modification for normal mapping support
        /// </summary>
        public static ShaderModification CreateNormalMappingModification()
        {
            return new ShaderModification
            {
                Name = "normal_mapping",
                AddUniforms = new List<string>
                {
                    "uniform sampler2D normalMap;",
                    "uniform float normalMapStrength;",
                    "uniform bool hasNormalMap;"
                },
                AddAttributes = new List<string>
                {
                    "attribute vec4 tangent;"
                },
                AddVaryings = new List<string>
                {
                    "varying vec3 v_tangent;",
                    "varying vec3 v_bitangent;"
                },
                VertexModifications = @"
    // Calculate TBN matrix
    vec3 T = normalize(normalMatrix * tangent.xyz);
    vec3 N = normalize(normalMatrix * normal);
    vec3 B = cross(N, T) * tangent.w;
    
    v_tangent = T;
    v_bitangent = B;",
                FragmentModifications = @"
    // Apply normal mapping
    if (hasNormalMap && normalMapStrength > 0.0) {
        vec3 normalFromMap = texture(normalMap, v_texCoord).xyz * 2.0 - 1.0;
        normalFromMap.xy *= normalMapStrength;
        
        mat3 TBN = mat3(normalize(v_tangent), normalize(v_bitangent), normalize(normal));
        normal = normalize(TBN * normalFromMap);
    }"
            };
        }

        /// <summary>
        /// Creates modification for SSR support
        /// </summary>
        public static ShaderModification CreateSSRModification()
        {
            return new ShaderModification
            {
                Name = "ssr",
                AddUniforms = new List<string>
                {
                    "uniform sampler2D sceneDepth;",
                    "uniform sampler2D sceneNormals;",
                    "uniform mat4 invProjectionMatrix;",
                    "uniform mat4 invViewMatrix;",
                    "uniform float ssrStrength;",
                    "uniform int ssrSteps;",
                    "uniform float ssrMaxDistance;"
                },
                AddFunctions = @"
vec3 getViewPosition(vec2 texCoord, float depth) {
    vec4 clipSpace = vec4(texCoord * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 viewSpace = invProjectionMatrix * clipSpace;
    return viewSpace.xyz / viewSpace.w;
}

vec3 screenSpaceReflection(vec3 viewPos, vec3 normal, float roughness) {
    // SSR implementation
    return vec3(0.0); // Placeholder
}",
                FragmentModifications = @"
    // Apply SSR
    vec3 reflection = screenSpaceReflection(v_viewPos, normal, roughness);
    color = mix(color, reflection, ssrStrength * (1.0 - roughness));"
            };
        }

        /// <summary>
        /// Creates modification for PBR material support
        /// </summary>
        public static ShaderModification CreatePBRModification()
        {
            return new ShaderModification
            {
                Name = "pbr",
                AddUniforms = new List<string>
                {
                    "uniform sampler2D roughnessMap;",
                    "uniform sampler2D metallicMap;",
                    "uniform sampler2D aoMap;",
                    "uniform float roughnessDefault;",
                    "uniform float metallicDefault;",
                    "uniform bool hasRoughnessMap;",
                    "uniform bool hasMetallicMap;",
                    "uniform bool hasAOMap;"
                },
                FragmentModifications = @"
    // Sample PBR textures
    float roughness = hasRoughnessMap ? texture(roughnessMap, v_texCoord).r : roughnessDefault;
    float metallic = hasMetallicMap ? texture(metallicMap, v_texCoord).r : metallicDefault;
    float ao = hasAOMap ? texture(aoMap, v_texCoord).r : 1.0;
    
    // Apply ambient occlusion
    color *= ao;"
            };
        }
    }
}