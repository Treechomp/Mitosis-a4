using System;
using System.Collections.Generic;
using Godot;
using Mitosis.World;

namespace Mitosis.Tools;

/// <summary>
/// Standalone worldgen preview tool — open Scenes/WorldgenPreview.tscn and run it (F6) to
/// explore terrain parameters with live sliders instead of relaunching the game per change.
/// It uses the REAL generation pipeline (<see cref="TerrainGenerator.SampleTile"/> — the same
/// per-tile function the game runs), so what you see is what a world with these parameters is.
///
/// Two modes:
///  - LIVE (every slider change, debounced): climate-only classification sampled on a stride —
///    no river pre-pass, so rivers/lakes/shores/moisture-feedback are absent, but elevation,
///    ridges, cliffs, climate bands, and landmarks are exact. Regenerates in ~0.1–0.3 s.
///  - FULL (button): runs PrecomputeRivers and renders the complete pipeline (rivers, lakes,
///    deltas, shores, hydrology moisture feedback) — exactly the world the game would generate
///    for this seed. Takes a few seconds at 36 chunks.
///
/// Views: biome / elevation / moisture / temperature (same palettes as the WorldSnapshot
/// PNGs). The stats panel shows the biome distribution and niche-coverage roll-ups (same
/// code as the snapshot report) plus the current parameter set for transcribing into the
/// GameManager inspector.
/// </summary>
public partial class WorldgenPreviewer : Control
{
    private const int PreviewRes = 320;      // preview samples per axis (strided over the world)
    private const double DebounceSeconds = 0.15;

    private int _seed = 730415729;
    private int _worldChunks = 36;
    private readonly TerrainSettings _settings = new()
    {
        // Match the current GameManager test defaults rather than TerrainSettings' own
        // (production) defaults, so the tool opens on what runs actually use.
        ElevationFrequency = 0.004f,
    };

    private TextureRect _mapRect = null!;
    private RichTextLabel _stats = null!;
    private OptionButton _viewSelect = null!;
    private Label _modeLabel = null!;
    private SpinBox _seedBox = null!;

    private double _debounce = -1;           // > 0 → live regen pending
    private TerrainGenerator? _fullGen;      // generator with a river pre-pass matching params
    private bool _busy;

    public override void _Ready()
    {
        BuildUi();
        MarkDirty();
    }

    public override void _Process(double delta)
    {
        // Debounce only ticks while not busy — a slider moved during a full-detail
        // generation must still trigger its live regen once the generation finishes.
        if (_busy || _debounce <= 0)
            return;
        _debounce -= delta;
        if (_debounce <= 0)
            RegenerateLive();
    }

    // ── Generation ─────────────────────────────────────────────────────────────────────

    /// <summary>Any parameter changed: invalidate the full-detail generator, queue a live regen.</summary>
    private void MarkDirty()
    {
        _fullGen = null;
        _debounce = DebounceSeconds;
    }

    private void RegenerateLive()
    {
        var gen = new TerrainGenerator(_seed, Clone(_settings));
        gen.SetWorldSizeTiles(_worldChunks * 32);
        Render(gen, live: true);
        _modeLabel.Text = "mode: LIVE (climate only — no rivers/shores; press Full detail)";
    }

    private async void OnFullDetailPressed()
    {
        if (_busy) return;
        _busy = true;
        _modeLabel.Text = "generating full detail (rivers)…";
        // Let the label paint before the synchronous generation blocks the frame.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        if (_fullGen == null)
        {
            _fullGen = new TerrainGenerator(_seed, Clone(_settings));
            _fullGen.PrecomputeRivers(_worldChunks * 32);
        }
        Render(_fullGen, live: false);
        _modeLabel.Text = "mode: FULL (exact world for this seed, incl. rivers/deltas/shores)";
        _busy = false;
    }

    private void Render(TerrainGenerator gen, bool live)
    {
        int worldTiles = _worldChunks * 32;
        int res = Math.Min(PreviewRes * (live ? 1 : 2), worldTiles);
        float stride = worldTiles / (float)res;

        var img = Image.CreateEmpty(res, res, false, Image.Format.Rgb8);
        var hist = new Dictionary<TileType, int>();
        int view = _viewSelect.Selected;

        for (int py = 0; py < res; py++)
        {
            int wy = (int)(py * stride);
            for (int px = 0; px < res; px++)
            {
                gen.SampleTile((int)(px * stride), wy,
                    out TileType tile, out float elev, out float moist, out float temp);
                hist.TryGetValue(tile, out int c);
                hist[tile] = c + 1;

                img.SetPixel(px, py, view switch
                {
                    1 => WorldSnapshot.ElevColor(elev),
                    2 => WorldSnapshot.MoistColor(moist),
                    3 => WorldSnapshot.TempColor(temp),
                    _ => Chunk.GetTileColor(tile),
                });
            }
        }

        _mapRect.Texture = ImageTexture.CreateFromImage(img);
        _stats.Text = ParamHeader(live) + "\n" +
                      WorldSnapshot.BuildDistributionSummary(hist, res * res);
    }

    private string ParamHeader(bool live)
    {
        var s = _settings;
        // 0.##### formatting keeps float artifacts (0.19999999) out of the transcribable line.
        return FormattableString.Invariant(
            $"seed {_seed} · {_worldChunks} chunks ({_worldChunks * 32} tiles) · {(live ? "LIVE (no hydrology)" : "FULL")}\n") +
            FormattableString.Invariant(
            $"ef {s.ElevationFrequency:0.#####} · warp {s.WarpAmplitude:0.##} · mf {s.MoistureFrequency:0.#####} · mc {s.MoistureContrast:0.###}\n") +
            FormattableString.Invariant(
            $"ridge f {s.RidgeFrequency:0.#####} a {s.RidgeAmplitude:0.###} of {s.OrogenyFrequency:0.#####} · cliff f {s.CliffFrequency:0.#####} s {s.CliffStrength:0.##} h {s.CliffStepHeight:0.###}\n") +
            FormattableString.Invariant(
            $"detail f {s.DetailFrequency:0.#####} a {s.DetailAmplitude:0.###} · rough f {s.RoughnessFrequency:0.#####} floor {s.RoughnessFloor:0.##} · rivers {s.RiverDensity:0.##}\n");
    }

    private static TerrainSettings Clone(TerrainSettings s) => new()
    {
        ElevationFrequency = s.ElevationFrequency,
        WarpAmplitude      = s.WarpAmplitude,
        MoistureFrequency  = s.MoistureFrequency,
        MoistureContrast   = s.MoistureContrast,
        DetailFrequency    = s.DetailFrequency,
        DetailOctaves      = s.DetailOctaves,
        DetailAmplitude    = s.DetailAmplitude,
        RoughnessFrequency = s.RoughnessFrequency,
        RoughnessFloor     = s.RoughnessFloor,
        RidgeFrequency     = s.RidgeFrequency,
        RidgeAmplitude     = s.RidgeAmplitude,
        OrogenyFrequency   = s.OrogenyFrequency,
        CliffFrequency     = s.CliffFrequency,
        CliffStrength      = s.CliffStrength,
        CliffStepHeight    = s.CliffStepHeight,
        RiverDensity       = s.RiverDensity,
    };

    // ── UI construction ────────────────────────────────────────────────────────────────

    private void BuildUi()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        var root = new HBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        // Left: parameter panel
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(320, 0) };
        var panel = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        panel.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(panel);
        root.AddChild(scroll);

        panel.AddChild(new Label { Text = "Worldgen Preview", HorizontalAlignment = HorizontalAlignment.Center });

        // Seed row
        var seedRow = new HBoxContainer();
        seedRow.AddChild(new Label { Text = "Seed" });
        _seedBox = new SpinBox { MinValue = 1, MaxValue = int.MaxValue, Step = 1, Value = _seed,
                                 SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _seedBox.ValueChanged += v => { _seed = (int)v; MarkDirty(); };
        seedRow.AddChild(_seedBox);
        var randBtn = new Button { Text = "🎲" };
        randBtn.Pressed += () => { _seedBox.Value = Random.Shared.Next(1, int.MaxValue); };
        seedRow.AddChild(randBtn);
        panel.AddChild(seedRow);

        AddSlider(panel, "World chunks", 9, 72, 1, _worldChunks, v => _worldChunks = (int)v);

        panel.AddChild(new HSeparator());
        AddSlider(panel, "Elevation freq", 0.001f, 0.02f, 0.0005f, _settings.ElevationFrequency, v => _settings.ElevationFrequency = v);
        AddSlider(panel, "Warp amplitude", 0f, 40f, 1f, _settings.WarpAmplitude, v => _settings.WarpAmplitude = v);

        panel.AddChild(new HSeparator());
        AddSlider(panel, "Moisture freq", 0.0005f, 0.012f, 0.0005f, _settings.MoistureFrequency, v => _settings.MoistureFrequency = v);
        AddSlider(panel, "Moisture contrast", 1f, 2.5f, 0.05f, _settings.MoistureContrast, v => _settings.MoistureContrast = v);

        panel.AddChild(new HSeparator());
        AddSlider(panel, "Ridge freq", 0.002f, 0.03f, 0.001f, _settings.RidgeFrequency, v => _settings.RidgeFrequency = v);
        AddSlider(panel, "Ridge amplitude", 0f, 0.4f, 0.01f, _settings.RidgeAmplitude, v => _settings.RidgeAmplitude = v);
        AddSlider(panel, "Orogeny freq", 0.001f, 0.01f, 0.0005f, _settings.OrogenyFrequency, v => _settings.OrogenyFrequency = v);

        panel.AddChild(new HSeparator());
        AddSlider(panel, "Cliff freq", 0.001f, 0.02f, 0.0005f, _settings.CliffFrequency, v => _settings.CliffFrequency = v);
        AddSlider(panel, "Cliff strength", 0f, 1f, 0.05f, _settings.CliffStrength, v => _settings.CliffStrength = v);
        AddSlider(panel, "Cliff step height", 0.02f, 0.2f, 0.01f, _settings.CliffStepHeight, v => _settings.CliffStepHeight = v);

        panel.AddChild(new HSeparator());
        AddSlider(panel, "Detail freq", 0.01f, 0.12f, 0.005f, _settings.DetailFrequency, v => _settings.DetailFrequency = v);
        AddSlider(panel, "Detail amplitude", 0f, 0.1f, 0.005f, _settings.DetailAmplitude, v => _settings.DetailAmplitude = v);
        AddSlider(panel, "Roughness freq", 0.001f, 0.02f, 0.0005f, _settings.RoughnessFrequency, v => _settings.RoughnessFrequency = v);
        AddSlider(panel, "Roughness floor", 0f, 1f, 0.05f, _settings.RoughnessFloor, v => _settings.RoughnessFloor = v);

        panel.AddChild(new HSeparator());
        AddSlider(panel, "River density (Full mode)", 0f, 2f, 0.05f, _settings.RiverDensity, v => _settings.RiverDensity = v);

        panel.AddChild(new HSeparator());
        _viewSelect = new OptionButton();
        _viewSelect.AddItem("Biome map", 0);
        _viewSelect.AddItem("Elevation", 1);
        _viewSelect.AddItem("Moisture", 2);
        _viewSelect.AddItem("Temperature", 3);
        _viewSelect.ItemSelected += _ =>
        {
            // Re-render only: reuse the full generator if it's still valid for these params.
            if (_fullGen != null) Render(_fullGen, live: false);
            else _debounce = 0.01;
        };
        panel.AddChild(_viewSelect);

        var fullBtn = new Button { Text = "Full detail (rivers/shores) — slower" };
        fullBtn.Pressed += OnFullDetailPressed;
        panel.AddChild(fullBtn);

        _modeLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        panel.AddChild(_modeLabel);

        // Middle: the map
        _mapRect = new TextureRect
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        root.AddChild(_mapRect);

        // Right: stats
        _stats = new RichTextLabel
        {
            CustomMinimumSize = new Vector2(420, 0),
            FitContent = false,
        };
        _stats.AddThemeFontSizeOverride("normal_font_size", 13);
        root.AddChild(_stats);
    }

    private void AddSlider(Container parent, string label, float min, float max, float step,
        float value, Action<float> setter)
    {
        var lbl = new Label();
        void SetText(float v) => lbl.Text = FormattableString.Invariant($"{label}: {v:0.####}");
        SetText(value);

        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value };
        slider.ValueChanged += v => { setter((float)v); SetText((float)v); MarkDirty(); };

        parent.AddChild(lbl);
        parent.AddChild(slider);
    }
}
