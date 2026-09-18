using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace VintageRTX.Configuration;

/// <summary>
/// Native Vintage Story controls over a detached configuration draft. Editing or closing this
/// dialog never mutates the live renderer; Apply publishes only after persistence succeeds.
/// Uses the public GUI API rather than patching a private settings dialog.
/// </summary>
internal sealed class GuiDialogVintageRtxSettings : GuiDialog
{
    private readonly ConfigStore store;
    private readonly Action applied;
    private VintageRtxConfig draft;
    private int page;
    private bool disposed;
    private int session;
    private readonly List<Action<GuiComposer>> initializeControls = new();

    /// <summary>Registers no resources until the user opens the dialog.</summary>
    /// <param name="api">Client GUI and main-thread services.</param>
    /// <param name="store">Live configuration and persistence owner.</param>
    /// <param name="applied">Invalidates renderer history after publication.</param>
    internal GuiDialogVintageRtxSettings(ICoreClientAPI api, ConfigStore store, Action applied)
        : base(api)
    {
        this.store = store;
        this.applied = applied;
        draft = Clone(store.Current);
    }

    /// <summary>Remappable shortcut shown in the game's normal control settings.</summary>
    public override string ToggleKeyCombinationCode => "vintagertx-settings";

    /// <summary>Releases camera capture while interacting with native controls.</summary>
    public override bool DisableMouseGrab => true;

    /// <summary>Opens a fresh draft or cancels the currently open draft.</summary>
    internal void Toggle()
    {
        if (disposed) return;
        if (IsOpened()) { TryClose(); return; }
        draft = Clone(store.Current);
        session++;
        ComposePanel();
        TryOpen();
    }

    /// <summary>Copies settings without sharing mutable state with the renderer.</summary>
    /// <param name="source">Current or draft configuration.</param>
    /// <returns>A detached equivalent configuration.</returns>
    internal static VintageRtxConfig Clone(VintageRtxConfig source) =>
        JsonConvert.DeserializeObject<VintageRtxConfig>(JsonConvert.SerializeObject(source))
        ?? throw new InvalidOperationException("Could not clone VintageRTX configuration.");

    /// <summary>Defers composition until the current GUI event has finished dispatching.</summary>
    private void ScheduleCompose()
    {
        int expectedSession = session;
        capi.Event.EnqueueMainThreadTask(() =>
        {
            if (!disposed && IsOpened() && expectedSession == session) ComposePanel();
        }, "vintagertx-settings-recompose");
    }

    /// <summary>Marks manually edited budgets as Custom without resetting their values.</summary>
    private void MarkCustom() => draft.RenderProfile = VintageRtxRenderProfile.Custom;

    /// <summary>Builds a compact, paginated native dialog.</summary>
    private void ComposePanel()
    {
        ClearComposers();
        initializeControls.Clear();
        ElementBounds content = ElementBounds.Fixed(0, 0, 590, 450);
        ElementBounds background = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        background.BothSizing = ElementSizing.FitToChildren;
        background.WithChildren(content);
        GuiComposer composer = capi.Gui.CreateCompo("vintagertx-settings", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(background)
            .AddDialogTitleBar("VintageRTX", () => TryClose())
            .BeginChildElements(background);
        string[] pages = ["general", "lighting", "reflections", "sampling"];
        composer.AddDropDown(pages, pages.Select(p => Lang.Get("vintagertx:settings-page-" + p)).ToArray(),
            page, (value, selected) =>
            {
                if (!selected) return;
                int index = Array.IndexOf(pages, value);
                if (index < 0 || index == page) return;
                page = index;
                ScheduleCompose();
            }, ElementBounds.Fixed(0, 30, 270, 30), "page");
        composer.AddStaticText(Lang.Get("vintagertx:settings-draft", draft.RenderProfile),
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(295, 32, 280, 35));

        if (page == 0)
        {
            AddSwitch(composer, "enabled", 0, draft.Enabled, v => draft.Enabled = v);
            string[] profiles = Enum.GetNames<VintageRtxRenderProfile>();
            AddLabel(composer, "profile", 1);
            composer.AddDropDown(profiles,
                profiles.Select(p => Lang.Get("vintagertx:settings-profile-" + p.ToLowerInvariant())).ToArray(),
                (int)draft.RenderProfile, (value, selected) =>
                {
                    if (!selected || !Enum.TryParse(value, out VintageRtxRenderProfile profile)) return;
                    draft.ApplyRenderProfile(profile);
                    ScheduleCompose();
                }, ControlBounds(1), "profile");
            AddSwitch(composer, "adaptive", 2, draft.AdaptiveQualityEnabled,
                v => { draft.AdaptiveQualityEnabled = v; MarkCustom(); });
            AddSlider(composer, "gpu-budget", 3, draft.GpuBudgetMilliseconds, 0.75f, 40, 100,
                v => { draft.GpuBudgetMilliseconds = v; MarkCustom(); }, " /100 ms");
            AddSwitch(composer, "temporal", 4, draft.TemporalAccumulationEnabled,
                v => { draft.TemporalAccumulationEnabled = v; MarkCustom(); });
        }
        else if (page == 1)
        {
            AddSwitch(composer, "voxel-light", 0, draft.VoxelLightingEnabled,
                v => { draft.VoxelLightingEnabled = v; MarkCustom(); });
            AddSwitch(composer, "ssgi", 1, draft.ScreenSpaceLightingEnabled,
                v => { draft.ScreenSpaceLightingEnabled = v; MarkCustom(); });
            AddSwitch(composer, "sun-shadow", 2, draft.SunShadowsEnabled,
                v => { draft.SunShadowsEnabled = v; MarkCustom(); });
            AddSlider(composer, "sun-strength", 3, draft.SunLightStrength, 0, 2.5f, 100,
                v => draft.SunLightStrength = v, " %");
            AddSlider(composer, "emission-strength", 4, draft.EmissiveLightStrength, 0, 2.5f, 100,
                v => draft.EmissiveLightStrength = v, " %");
            AddSlider(composer, "bounce-strength", 5, draft.PointLightBounceStrength, 0, 1.5f, 100,
                v => draft.PointLightBounceStrength = v, " %");
        }
        else if (page == 2)
        {
            AddSwitch(composer, "ssr", 0, draft.ScreenSpaceReflectionsEnabled,
                v => { draft.ScreenSpaceReflectionsEnabled = v; MarkCustom(); });
            AddSwitch(composer, "voxel-reflection", 1, draft.VoxelReflectionsEnabled,
                v => { draft.VoxelReflectionsEnabled = v; MarkCustom(); });
            AddSlider(composer, "reflection-strength", 2, draft.ReflectionStrength, 0, 1.5f, 100,
                v => draft.ReflectionStrength = v, " %");
            AddSlider(composer, "reflection-distance", 3, draft.ReflectionDistance, 1, 48, 1,
                v => { draft.ReflectionDistance = v; MarkCustom(); }, " blocks");
            AddSlider(composer, "history-weight", 4, draft.TemporalHistoryWeight, 0, 0.95f, 100,
                v => draft.TemporalHistoryWeight = v, " %");
        }
        else
        {
            AddSlider(composer, "ray-count", 0, draft.RayCount, 1, 8, 1,
                v => { draft.RayCount = (int)v; MarkCustom(); });
            AddSlider(composer, "ray-steps", 1, draft.RaySteps, 4, 24, 1,
                v => { draft.RaySteps = (int)v; MarkCustom(); });
            AddSlider(composer, "shadow-samples", 2, draft.PointLightShadowSamples, 1, 8, 1,
                v => { draft.PointLightShadowSamples = (int)v; MarkCustom(); });
            AddSlider(composer, "bounce-rays", 3, draft.VoxelBounceRayCount, 1, 4, 1,
                v => { draft.VoxelBounceRayCount = (int)v; MarkCustom(); });
            AddSlider(composer, "point-range", 4, draft.PointLightRadius, 4, 32, 1,
                v => { draft.PointLightRadius = v; MarkCustom(); }, " blocks");
        }
        composer.AddStaticText(Lang.Get("vintagertx:settings-note"), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(0, 356, 580, 44));
        composer.AddSmallButton(Lang.Get("vintagertx:settings-reset"), () =>
        {
            draft = new VintageRtxConfig { SchemaVersion = VintageRtxConfig.CurrentSchemaVersion };
            ScheduleCompose();
            return true;
        }, ElementBounds.Fixed(0, 408, 170, 28));
        composer.AddSmallButton(Lang.Get("vintagertx:settings-cancel"), () => TryClose(),
            ElementBounds.Fixed(215, 408, 170, 28));
        composer.AddSmallButton(Lang.Get("vintagertx:settings-apply"), Apply,
            ElementBounds.Fixed(410, 408, 170, 28));
        SingleComposer = composer.EndChildElements().Compose();
        foreach (Action<GuiComposer> initialize in initializeControls) initialize(SingleComposer);
    }

    /// <summary>Returns the native-control column for a zero-based row.</summary>
    /// <param name="row">Row in the current category.</param>
    /// <returns>Fixed native GUI bounds.</returns>
    private static ElementBounds ControlBounds(int row) => ElementBounds.Fixed(305, 82 + row * 44, 270, 30);

    /// <summary>Adds a localized setting caption.</summary>
    /// <param name="composer">Active composer.</param><param name="key">Localization suffix.</param>
    /// <param name="row">Zero-based setting row.</param>
    private static void AddLabel(GuiComposer composer, string key, int row) => composer.AddStaticText(
        Lang.Get("vintagertx:settings-" + key), CairoFont.WhiteSmallText(),
        ElementBounds.Fixed(0, 87 + row * 44, 295, 32));

    /// <summary>Adds a switch initialized without publishing changes to live settings.</summary>
    /// <param name="composer">Active composer.</param><param name="key">Setting key.</param>
    /// <param name="row">Zero-based row.</param><param name="value">Draft value.</param>
    /// <param name="changed">Draft-only callback.</param>
    private void AddSwitch(GuiComposer composer, string key, int row, bool value, Action<bool> changed)
    {
        AddLabel(composer, key, row);
        composer.AddSwitch(changed, ControlBounds(row), key);
        initializeControls.Add(c => c.GetSwitch(key).SetValue(value));
    }

    /// <summary>Adds a bounded integer slider representing a scaled draft value.</summary>
    /// <param name="composer">Active composer.</param><param name="key">Setting key.</param>
    /// <param name="row">Zero-based row.</param><param name="value">Unscaled draft value.</param>
    /// <param name="min">Unscaled minimum.</param><param name="max">Unscaled maximum.</param>
    /// <param name="scale">Integer steps per unit.</param><param name="changed">Draft-only callback.</param>
    /// <param name="unit">Visible unit or scaling legend.</param>
    private void AddSlider(GuiComposer composer, string key, int row, float value, float min, float max,
        int scale, Action<float> changed, string unit = "")
    {
        AddLabel(composer, key, row);
        composer.AddSlider(v => { changed((float)v / scale); return true; }, ControlBounds(row), key);
        initializeControls.Add(c => c.GetSlider(key).SetValues((int)MathF.Round(value * scale),
            (int)MathF.Round(min * scale), (int)MathF.Round(max * scale), 1, unit));
    }

    /// <summary>Persists then publishes the draft; persistence errors leave live settings intact.</summary>
    /// <returns>Whether the click was handled.</returns>
    private bool Apply()
    {
        try
        {
            store.Apply(draft);
            applied();
            TryClose();
        }
        catch (Exception exception)
        {
            capi.Logger.Error("[VintageRTX] Settings apply failed: {0}", exception.Message);
            capi.ShowChatMessage(Lang.Get("vintagertx:settings-error", exception.Message));
        }
        return true;
    }

    /// <summary>Invalidates queued callbacks and releases owned GUI resources.</summary>
    public override void Dispose()
    {
        disposed = true;
        session++;
        base.Dispose();
    }
}
