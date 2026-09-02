using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace VintageRTX.src
{
    /// <summary>
    /// Handles runtime patching of shader loading methods using Harmony
    /// </summary>
    public static class ShaderPatcher
    {
        private static ShaderManager _shaderManager;

        /// <summary>
        /// Applies Harmony patches to intercept shader loading
        /// </summary>
        /// <param name="harmony">Harmony instance</param>
        /// <param name="shaderManager">Shader manager instance</param>
        public static void ApplyPatches(Harmony harmony, ShaderManager shaderManager)
        {
            _shaderManager = shaderManager;

            try
            {
                // Patch shader compilation method
                var shaderProgramType = typeof(ShaderProgram);
                var compileMethod = shaderProgramType.GetMethod("Compile",
                    BindingFlags.Public | BindingFlags.Instance);

                if (compileMethod != null)
                {
                    harmony.Patch(compileMethod,
                        prefix: new HarmonyMethod(typeof(ShaderPatcher), nameof(CompilePrefix)));
                }

                // Patch shader loading method
                var loadShaderMethod = AccessTools.Method(typeof(ShaderProgram), "LoadShader");
                if (loadShaderMethod != null)
                {
                    harmony.Patch(loadShaderMethod,
                        prefix: new HarmonyMethod(typeof(ShaderPatcher), nameof(LoadShaderPrefix)));
                }

                _shaderManager.Logger.Notification(Lang.Get("vintagertx:loaded"));
            }
            catch (Exception ex)
            {
                _shaderManager.Logger.Error(Lang.Get("vintagertx:error.harmony", ex.Message));
            }
        }

        /// <summary>
        /// Prefix patch for shader compilation
        /// </summary>
        /// <param name="__instance">ShaderProgram instance</param>
        /// <returns>True to continue with original method</returns>
        static bool CompilePrefix(ShaderProgram __instance)
        {
            try
            {
                var shaderName = __instance.PassName;

                if (_shaderManager.ShouldModifyShader(shaderName))
                {
                    _shaderManager.Logger.Debug(Lang.Get("vintagertx:shader.intercepted", shaderName));

                    // Enregistrer automatiquement le shader
                    _shaderManager.RegisterShaderProgram(__instance);
                }
            }
            catch (Exception ex)
            {
                _shaderManager.Logger.Error(Lang.Get("vintagertx:shader.error", ex.Message));
            }

            return true; // Continue with original method
        }

        /// <summary>
        /// Prefix patch for shader loading to modify shader code
        /// </summary>
        /// <param name="__result">The resulting shader code</param>
        /// <param name="shaderType">Type of shader</param>
        /// <param name="code">Original shader code</param>
        /// <param name="prefixCode">Prefix code</param>
        /// <param name="shaderName">Name of the shader</param>
        /// <returns>False to skip original method if shader was modified</returns>
        static bool LoadShaderPrefix(ref string __result, EnumShaderType shaderType, string code, string prefixCode, string shaderName)
        {
            try
            {
                if (_shaderManager.ShouldModifyShader(shaderName))
                {
                    // Modify shader code
                    var modifiedCode = _shaderManager.ModifyShaderCode(shaderName, shaderType, code, prefixCode);

                    if (!string.IsNullOrEmpty(modifiedCode))
                    {
                        __result = modifiedCode;
                        _shaderManager.Logger.Debug(Lang.Get("vintagertx:shader.modified", shaderName));
                        return false; // Skip original method
                    }
                }
            }
            catch (Exception ex)
            {
                _shaderManager.Logger.Error(Lang.Get("vintagertx:shader.error", ex.Message));
            }

            return true; // Continue with original method
        }

        /// <summary>
        /// Patch for ShaderProgram constructor to register instances
        /// </summary>
        [HarmonyPatch(typeof(ShaderProgram))]
        [HarmonyPatch(MethodType.Constructor)]
        [HarmonyPatch(new Type[] { })]
        public static class ShaderProgramConstructorPatch
        {
            /// <summary>
            /// Postfix to register shader program after creation
            /// </summary>
            /// <param name="__instance">Created ShaderProgram instance</param>
            static void Postfix(ShaderProgram __instance)
            {
                RegisterShaderProgramSafe(__instance);
            }

            /// <summary>
            /// Méthode wrapper pour éviter l'ambiguïté - CORRECTION
            /// </summary>
            private static void RegisterShaderProgramSafe(ShaderProgram shaderProgram)
            {
                if (_shaderManager == null || shaderProgram == null) return;

                try
                {
                    // Correction: Appel direct de la méthode au lieu d'utiliser la réflexion
                    _shaderManager.RegisterShaderProgram(shaderProgram);
                }
                catch (Exception ex)
                {
                    _shaderManager.Logger?.Debug($"[VintageRTX] Could not register shader program: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Shader type enumeration
    /// </summary>
    public enum EnumShaderType
    {
        /// <summary>Vertex shader</summary>
        VertexShader = 35633,
        /// <summary>Fragment/pixel shader</summary>
        FragmentShader = 35632,
        /// <summary>Geometry shader</summary>
        GeometryShader = 36313
    }
}