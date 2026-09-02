using System;
using VintageRTX.src;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VintageRTX.GUI
{
    /// <summary>
    /// In-game configuration GUI for VintageRTX
    /// </summary>
    public class VintageRTXConfigGUI : GuiDialog
    {
        private ConfigHandler configHandler;
        private ShaderManager shaderManager;
        private Action onConfigChanged;

        /// <summary>
        /// Initializes a new instance of the configuration GUI
        /// </summary>
        public VintageRTXConfigGUI(ICoreClientAPI api, ConfigHandler configHandler, ShaderManager shaderManager) : base(api)
        {
            this.configHandler = configHandler;
            this.shaderManager = shaderManager;
            SetupDialog();
        }

        /// <summary>
        /// Gets the dialog key for identification
        /// </summary>
        public override string ToggleKeyCombinationCode => "vintagertxconfig";

        /// <summary>
        /// Sets up the dialog interface
        /// </summary>
        private void SetupDialog()
        {
            // Dialog bounds
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            // Background
            SingleComposer = capi.Gui
                .CreateCompo("vintagertxconfig", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("vintagertx:title") + " " + Lang.Get("game:settings"), OnTitleBarClose)
                .BeginChildElements(bgBounds);

            ElementBounds leftColumn = ElementBounds.Fixed(0, GuiStyle.TitleBarHeight + 10, 250, 25);
            ElementBounds rightColumn = ElementBounds.Fixed(260, GuiStyle.TitleBarHeight + 10, 200, 25);

            var config = configHandler.Config;

            // Quality Preset
            SingleComposer
                .AddStaticText(Lang.Get("Quality Preset"), CairoFont.WhiteSmallText(), leftColumn)
                .AddDropDown(
                    new string[] { "low", "medium", "high", "ultra" },
                    new string[] { "Low", "Medium", "High", "Ultra" },
                    Array.IndexOf(new string[] { "low", "medium", "high", "ultra" }, config.QualityPreset),
                    OnQualityPresetChanged,
                    rightColumn.FlatCopy(),
                    "qualityPreset"
                );

            leftColumn = leftColumn.BelowCopy(0, 10);
            rightColumn = rightColumn.BelowCopy(0, 10);

            // Normal Mapping
            SingleComposer
                .AddStaticText(Lang.Get("Normal Mapping"), CairoFont.WhiteSmallText(), leftColumn)
                .AddSwitch(OnNormalMappingToggled, rightColumn.FlatCopy(), "normalMapping");

            leftColumn = leftColumn.BelowCopy(0, 5);
            rightColumn = rightColumn.BelowCopy(0, 5);

            // Normal Map Strength
            SingleComposer
                .AddStaticText(Lang.Get("Normal Strength"), CairoFont.WhiteSmallText(), leftColumn)
                .AddSlider(
                    OnNormalStrengthChanged,
                    rightColumn.FlatCopy().FixedGrow(0, 0).WithFixedWidth(150),
                    "normalStrength"
                );

            leftColumn = leftColumn.BelowCopy(0, 10);
            rightColumn = rightColumn.BelowCopy(0, 10);

            // Screen Space Reflections
            SingleComposer
                .AddStaticText(Lang.Get("Screen Space Reflections"), CairoFont.WhiteSmallText(), leftColumn)
                .AddSwitch(OnSSRToggled, rightColumn.FlatCopy(), "ssr");

            leftColumn = leftColumn.BelowCopy(0, 5);
            rightColumn = rightColumn.BelowCopy(0, 5);

            // SSR Quality
            SingleComposer
                .AddStaticText(Lang.Get("SSR Quality"), CairoFont.WhiteSmallText(), leftColumn)
                .AddDropDown(
                    new string[] { "low", "medium", "high", "ultra" },
                    new string[] { "Low", "Medium", "High", "Ultra" },
                    Array.IndexOf(new string[] { "low", "medium", "high", "ultra" }, config.SSRQuality),
                    OnSSRQualityChanged,
                    rightColumn.FlatCopy(),
                    "ssrQuality"
                );

            leftColumn = leftColumn.BelowCopy(0, 5);
            rightColumn = rightColumn.BelowCopy(0, 5);

            // SSR Strength
            SingleComposer
                .AddStaticText(Lang.Get("SSR Strength"), CairoFont.WhiteSmallText(), leftColumn)
                .AddSlider(
                    OnSSRStrengthChanged,
                    rightColumn.FlatCopy().FixedGrow(0, 0).WithFixedWidth(150),
                    "ssrStrength"
                );

            leftColumn = leftColumn.BelowCopy(0, 10);
            rightColumn = rightColumn.BelowCopy(0, 10);

            // Ray Tracing
            SingleComposer
                .AddStaticText(Lang.Get("Ray Tracing"), CairoFont.WhiteSmallText(), leftColumn)
                .AddSwitch(OnRayTracingToggled, rightColumn.FlatCopy(), "rayTracing");

            leftColumn = leftColumn.BelowCopy(0, 10);
            rightColumn = rightColumn.BelowCopy(0, 10);

            // Adaptive Quality
            SingleComposer
                .AddStaticText(Lang.Get("Adaptive Quality"), CairoFont.WhiteSmallText(), leftColumn)
                .AddSwitch(OnAdaptiveQualityToggled, rightColumn.FlatCopy(), "adaptiveQuality");

            leftColumn = leftColumn.BelowCopy(0, 5);
            rightColumn = rightColumn.BelowCopy(0, 5);

            // Target FPS
            SingleComposer
                .AddStaticText(Lang.Get("Target FPS"), CairoFont.WhiteSmallText(), leftColumn)
                .AddNumberInput(
                    rightColumn.FlatCopy().WithFixedWidth(60),
                    OnTargetFPSChanged,
                    CairoFont.WhiteSmallText(),
                    "targetFPS"
                );

            leftColumn = leftColumn.BelowCopy(0, 10);
            rightColumn = rightColumn.BelowCopy(0, 10);

            // Debug Info
            SingleComposer
                .AddStaticText(Lang.Get("Show Debug Info"), CairoFont.WhiteSmallText(), leftColumn)
                .AddSwitch(OnDebugInfoToggled, rightColumn.FlatCopy(), "debugInfo");

            leftColumn = leftColumn.BelowCopy(0, 20);

            // Buttons
            ElementBounds buttonRow = ElementBounds.Fixed(0, leftColumn.fixedY, 400, 25);

            SingleComposer
                .AddSmallButton(Lang.Get("Apply"), OnApplyButton, buttonRow.FlatCopy().WithFixedWidth(80))
                .AddSmallButton(Lang.Get("Reset"), OnResetButton, buttonRow.FlatCopy().WithFixedWidth(80).FixedGrow(90, 0))
                .AddSmallButton(Lang.Get("Close"), OnCloseButton, buttonRow.FlatCopy().WithFixedWidth(80).FixedGrow(180, 0));

            SingleComposer.Compose();

            // Set initial values
            UpdateGUIValues();
        }

        /// <summary>
        /// Updates GUI elements with current configuration values
        /// </summary>
        private void UpdateGUIValues()
        {
            var config = configHandler.Config;

            SingleComposer.GetSwitch("normalMapping").On = config.EnableNormalMapping;
            SingleComposer.GetSlider("normalStrength").SetValues((int)(config.NormalMapStrength * 100), 0, 200, 1, "");
            SingleComposer.GetSwitch("ssr").On = config.EnableSSR;
            SingleComposer.GetSlider("ssrStrength").SetValues((int)(config.SSRStrength * 100), 0, 100, 1, "");
            SingleComposer.GetSwitch("rayTracing").On = config.EnableRayTracing;
            SingleComposer.GetSwitch("adaptiveQuality").On = config.AdaptiveQuality;
            SingleComposer.GetTextInput("targetFPS").SetValue(config.TargetFPS.ToString());
            SingleComposer.GetSwitch("debugInfo").On = config.ShowDebugInfo;
        }

        // Event handlers
        private void OnQualityPresetChanged(string code, bool selected)
        {
            if (selected)
            {
                configHandler.ApplyQualityPreset(code);
                UpdateGUIValues();
            }
        }

        private void OnNormalMappingToggled(bool on)
        {
            configHandler.Config.EnableNormalMapping = on;
        }

        private bool OnNormalStrengthChanged(int value)
        {
            configHandler.Config.NormalMapStrength = value / 100f;
            return true;
        }

        private void OnSSRToggled(bool on)
        {
            configHandler.Config.EnableSSR = on;
        }

        private void OnSSRQualityChanged(string code, bool selected)
        {
            if (selected)
            {
                configHandler.Config.SSRQuality = code;
            }
        }

        private bool OnSSRStrengthChanged(int value)
        {
            configHandler.Config.SSRStrength = value / 100f;
            return true;
        }

        private void OnRayTracingToggled(bool on)
        {
            configHandler.Config.EnableRayTracing = on;
        }

        private void OnAdaptiveQualityToggled(bool on)
        {
            configHandler.Config.AdaptiveQuality = on;
        }

        private void OnTargetFPSChanged(string value)
        {
            if (int.TryParse(value, out int fps))
            {
                configHandler.Config.TargetFPS = Math.Clamp(fps, 30, 240);
            }
        }

        private void OnDebugInfoToggled(bool on)
        {
            configHandler.Config.ShowDebugInfo = on;
        }

        private bool OnApplyButton()
        {
            configHandler.SaveConfig();
            shaderManager.UpdateFromConfig(configHandler);
            shaderManager.ReloadShaders();
            capi.ShowChatMessage(Lang.Get("Settings applied"));
            return true;
        }

        private bool OnResetButton()
        {
            // CORRECTION : Au lieu d'assigner directement, utiliser une méthode publique
            // ou modifier ConfigHandler pour permettre la réinitialisation
            ResetConfigToDefault();
            UpdateGUIValues();
            return true;
        }

        /// <summary>
        /// Remet la configuration aux valeurs par défaut
        /// </summary>
        private void ResetConfigToDefault()
        {
            var config = configHandler.Config;

            // Réinitialiser toutes les propriétés aux valeurs par défaut
            config.EnableNormalMapping = true;
            config.NormalMapStrength = 1.0f;
            config.EnableSSR = true;
            config.SSRQuality = "medium";
            config.SSRMaxSteps = 32;
            config.SSRMaxDistance = 10.0f;
            config.SSRStrength = 0.5f;
            config.SSRFadeDistance = 5.0f;
            config.EnableRayTracing = false;
            config.RayTracingSamples = 16;
            config.QualityPreset = "medium";
            config.AdaptiveQuality = true;
            config.TargetFPS = 60;
            config.ShowDebugInfo = false;
            config.EnableShaderHotReload = true;
        }

        private bool OnCloseButton()
        {
            TryClose();
            return true;
        }

        private void OnTitleBarClose()
        {
            TryClose();
        }

        /// <summary>
        /// Called when the dialog is opened
        /// </summary>
        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            UpdateGUIValues();
        }
    }
}