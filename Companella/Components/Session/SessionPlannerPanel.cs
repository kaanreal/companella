using Companella.Models.Session;
using Companella.Services.Analysis;
using Companella.Services.Beatmap;
using Companella.Services.Common;
using Companella.Services.Database;
using Companella.Services.Session;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osuTK;
using osuTK.Graphics;
using Squirrel.SimpleSplat;

namespace Companella.Components.Session;

/// <summary>
/// Panel for planning and generating structured practice sessions.
/// Uses an interactive MSD curve graph for customizing session difficulty progression.
/// </summary>
public partial class SessionPlannerPanel : CompositeDrawable
{
    [Resolved]
    private MapsDatabaseService MapsDatabase { get; set; } = null!;

    [Resolved]
    private OsuCollectionService CollectionService { get; set; } = null!;

    [Resolved]
    private SkillsTrendAnalyzer TrendAnalyzer { get; set; } = null!;

    private SessionPlannerService? _plannerService;
    private BeatmapIndexer? _beatmapIndexer;

    private MsdCurveGraph _curveGraph = null!;
    private DurationSlider _durationSlider = null!;
    private SessionGenerateButton _generateButton = null!;
    private SessionSmallButton _resetButton = null!;
    private SessionSmallButton _fromSessionsButton = null!;
    private SpriteText _statusText = null!;
    private SpriteText _summaryText = null!;
    private FillFlowContainer _previewContainer = null!;
    private SessionPlanningSpinner _loadingSpinner = null!;

    private SkillsTrendResult? _currentTrends;
    private SessionPlan? _currentPlan;
    private bool _isGenerating;

    private readonly Color4 _accentColor = new Color4(255, 102, 170, 255);

    /// <summary>
    /// Event raised when a loading operation starts.
    /// </summary>
    public event Action<string>? LoadingStarted;

    /// <summary>
    /// Event raised to update the loading status.
    /// </summary>
    public event Action<string>? LoadingStatusChanged;

    /// <summary>
    /// Event raised when a loading operation finishes.
    /// </summary>
    public event Action? LoadingFinished;

    public SessionPlannerPanel()
    {
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        _beatmapIndexer = new BeatmapIndexer();
        _plannerService = new SessionPlannerService(MapsDatabase, CollectionService, _beatmapIndexer);
        _plannerService.ProgressChanged += OnPlannerProgressChanged;

        InternalChildren = new Drawable[]
        {
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 10),
                Children = new Drawable[]
                {
                    // Header
                    new SpriteText
                    {
                        Text = "Session Planner",
                        Font = new FontUsage("", 17, "Bold"),
                        Colour = new Color4(180, 180, 180, 255)
                    },
                    // Description
                    new TextFlowContainer(s => { s.Font = new FontUsage("", 13); s.Colour = new Color4(140, 140, 140, 255); })
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Text = "Right-click: add/remove points | Left-click point: cycle skillset | Drag: move points"
                    },
                    // MSD Curve Graph
                    _curveGraph = new MsdCurveGraph
                    {
                        Size = new Vector2(420, 180)
                    },
                    // Settings row
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(0, 6),
                        Children = new Drawable[]
                        {
                            // Base MSD row
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Horizontal,
                                Spacing = new Vector2(8, 0),
                                Children = new Drawable[]
                                {
                                    new SpriteText
                                    {
                                        Text = "Base MSD:",
                                        Font = new FontUsage("", 14),
                                        Colour = new Color4(160, 160, 160, 255),
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                        Width = 80
                                    },
                                    new BaseMsdSlider(_curveGraph.BaseMsd)
                                    {
                                        Width = 180,
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft
                                    }
                                }
                            },
                            // Duration row
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Horizontal,
                                Spacing = new Vector2(8, 0),
                                Children = new Drawable[]
                                {
                                    new SpriteText
                                    {
                                        Text = "Duration:",
                                        Font = new FontUsage("", 14),
                                        Colour = new Color4(160, 160, 160, 255),
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                        Width = 80
                                    },
                                    _durationSlider = new DurationSlider
                                    {
                                        Width = 180,
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft
                                    }
                                }
                            }
                        }
                    },
                    // Generate button row
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Horizontal,
                        Spacing = new Vector2(8, 0),
                        Children = new Drawable[]
                        {
                            _generateButton = new SessionGenerateButton
                            {
                                Size = new Vector2(140, 32),
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                TooltipText = "Generate a practice session based on the curve"
                            },
                            _resetButton = new SessionSmallButton("Reset")
                            {
                                Size = new Vector2(50, 24),
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                TooltipText = "Reset curve to default"
                            },
                            _fromSessionsButton = new SessionSmallButton("From Sessions")
                            {
                                Size = new Vector2(95, 24),
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                TooltipText = "Generate curve from your session history"
                            },
                            _loadingSpinner = new SessionPlanningSpinner
                            {
                                Size = new Vector2(20),
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Alpha = 0
                            }
                        }
                    },
                    // Status text
                    _statusText = new SpriteText
                    {
                        Text = "Customize the curve and click Generate to create a session",
                        Font = new FontUsage("", 14),
                        Colour = new Color4(120, 120, 120, 255)
                    },
                    // Summary text
                    _summaryText = new SpriteText
                    {
                        Text = "",
                        Font = new FontUsage("", 14),
                        Colour = _accentColor,
                        Alpha = 0
                    },
                    // Preview container for session structure
                    _previewContainer = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(0, 4),
                        Alpha = 0
                    }
                }
            }
        };

        // Wire up events
        _generateButton.Clicked += OnGenerateClicked;
        _resetButton.Clicked += OnResetClicked;
        _fromSessionsButton.Clicked += OnFromSessionsClicked;
        _fromSessionsButton.SetEnabled(false); // Disabled until trends are set
        _durationSlider.Current.BindValueChanged(OnDurationChanged, true);
        _curveGraph.CurveChanged += OnCurveChanged;
    }

    private void OnCurveChanged()
    {
        UpdateStatusText();
    }

    private void OnDurationChanged(ValueChangedEvent<int> e)
    {
        _curveGraph.Config.TotalSessionMinutes = e.NewValue;
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        var config = _curveGraph.Config;
        var minMsd = config.BaseMsd * (1 + config.MinMsdPercent / 100.0);
        var maxMsd = config.BaseMsd * (1 + config.MaxMsdPercent / 100.0);

        // Count skillsets used
        var skillsets = config.Points
            .Where(p => p.Skillset != null)
            .Select(p => p.Skillset)
            .Distinct()
            .ToList();

        var skillsetInfo = skillsets.Count > 0 ? $" | Skills: {string.Join(", ", skillsets)}" : "";
        _statusText.Text = $"MSD: {minMsd:F1}-{maxMsd:F1} | {config.TotalSessionMinutes}min | {config.Points.Count} pts{skillsetInfo}";
    }

    private void OnResetClicked()
    {
        _curveGraph.ResetToDefault();
    }

    private void OnFromSessionsClicked()
    {
        if (_currentTrends == null)
        {
            _statusText.Text = "No session data available. Run Skills Analysis first.";
            return;
        }

        var generatedConfig = MsdCurveConfig.GenerateFromTrends(_currentTrends);
        if (generatedConfig == null)
        {
            _statusText.Text = "Not enough session data to generate a curve (need at least 5 plays).";
            return;
        }

        // Apply the generated config to the graph
        _curveGraph.Config = generatedConfig;
        _curveGraph.BaseMsd.Value = generatedConfig.BaseMsd;
        _durationSlider.Current.Value = generatedConfig.TotalSessionMinutes;
        _curveGraph.Redraw();

        _statusText.Text = $"Curve generated from {_currentTrends.TotalPlays} plays across your sessions";
        UpdateStatusText();
    }

    private async void OnGenerateClicked()
    {
        if (_isGenerating || _plannerService == null)
            return;

        _isGenerating = true;
        _generateButton.SetEnabled(false);
        _loadingSpinner.FadeTo(1, 100);
        _previewContainer.FadeTo(0, 100);
        _summaryText.FadeTo(0, 100);

        LoadingStarted?.Invoke("Generating session plan...");

        try
        {
            var curveConfig = _curveGraph.Config.Clone();
            var plan = await _plannerService.GenerateFromCurveAsync(curveConfig);

            if (plan != null)
            {
                _currentPlan = plan;
                DisplayPlan(plan);
                _statusText.Text = $"Session created: {plan.CollectionName}";
                _summaryText.Text = plan.Summary;
                _summaryText.FadeTo(1, 200);
            }
            else
            {
                _statusText.Text = "Failed to generate session plan. Check that maps are indexed.";
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Error: {ex.Message}";
            Logger.Info($"[SessionPlanner] Error: {ex}");
        }
        finally
        {
            _isGenerating = false;
            _generateButton.SetEnabled(true);
            _loadingSpinner.FadeTo(0, 100);
            LoadingFinished?.Invoke();
        }
    }

    private void OnPlannerProgressChanged(object? sender, SessionPlanProgressEventArgs e)
    {
        Schedule(() =>
        {
            _statusText.Text = e.Status;
            LoadingStatusChanged?.Invoke($"{e.Percentage}%: {e.Status}");
        });
    }

    private void DisplayPlan(SessionPlan plan)
    {
        _previewContainer.Clear();

        var items = plan.Items;
        if (items.Count == 0) return;

        var minMsd = items.Min(i => i.ActualMsd);
        var maxMsd = items.Max(i => i.ActualMsd);
        var avgMsd = items.Average(i => i.ActualMsd);

        // Group by skillset
        var skillsetGroups = items.GroupBy(i => i.Skillset).ToList();
        var skillsetSummary = string.Join(", ", skillsetGroups.Select(g => $"{g.Key}: {g.Count()}"));

        _previewContainer.Add(new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 24,
            Masking = true,
            CornerRadius = 4,
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = new Color4(_accentColor.R, _accentColor.G, _accentColor.B, 0.15f)
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Padding = new MarginPadding { Horizontal = 8 },
                    Children = new Drawable[]
                    {
                        new SpriteText
                        {
                            Text = $"{items.Count} maps",
                            Font = new FontUsage("", 15, "Bold"),
                            Colour = _accentColor,
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft
                        },
                        new SpriteText
                        {
                            Text = $" | MSD: {minMsd:F1}-{maxMsd:F1} | ~{plan.TotalDurationMinutes:F0}min",
                            Font = new FontUsage("", 14),
                            Colour = new Color4(160, 160, 160, 255),
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft
                        }
                    }
                }
            }
        });

        // Show skillset breakdown if multiple
        if (skillsetGroups.Count > 1)
        {
            _previewContainer.Add(new SpriteText
            {
                Text = skillsetSummary,
                Font = new FontUsage("", 13),
                Colour = new Color4(140, 140, 140, 255)
            });
        }

        _previewContainer.FadeTo(1, 200);
    }

    /// <summary>
    /// Sets the current skill trends to pre-fill the base MSD.
    /// </summary>
    public void SetTrends(SkillsTrendResult? trends)
    {
        _currentTrends = trends;
        if (_curveGraph == null) return;

        // Enable/disable the From Sessions button based on trends availability
        _fromSessionsButton?.SetEnabled(trends != null && trends.TotalPlays >= 5);

        if (trends != null)
        {
            _curveGraph.BaseMsd.Value = trends.OverallSkillLevel;
            _statusText.Text = $"Base MSD set from analysis: {trends.OverallSkillLevel:F1}";
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        if (_plannerService != null)
        {
            _plannerService.ProgressChanged -= OnPlannerProgressChanged;
        }

        base.Dispose(isDisposing);
    }
}

/// <summary>
/// Slider for base MSD selection.
/// </summary>
public partial class BaseMsdSlider : CompositeDrawable
{
    private readonly BindableDouble _current;
    private BasicSliderBar<double> _slider = null!;
    private SpriteText _valueText = null!;

    public BaseMsdSlider(BindableDouble bindable)
    {
        _current = bindable;
        AutoSizeAxes = Axes.Y;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChild = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Horizontal,
            Spacing = new Vector2(8, 0),
            Children = new Drawable[]
            {
                _slider = new BasicSliderBar<double>
                {
                    Width = 100,
                    Height = 20,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Current = _current
                },
                _valueText = new SpriteText
                {
                    Font = new FontUsage("", 14),
                    Colour = new Color4(255, 102, 170, 255),
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft
                }
            }
        };

        _current.BindValueChanged(e => _valueText.Text = $"{e.NewValue:F1} MSD", true);
    }
}

/// <summary>
/// Slider for session duration selection.
/// </summary>
public partial class DurationSlider : CompositeDrawable
{
    public BindableInt Current { get; } = new BindableInt(80)
    {
        MinValue = 30,
        MaxValue = 180
    };

    private BasicSliderBar<int> _slider = null!;
    private SpriteText _valueText = null!;

    public DurationSlider()
    {
        AutoSizeAxes = Axes.Y;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChild = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Horizontal,
            Spacing = new Vector2(8, 0),
            Children = new Drawable[]
            {
                _slider = new BasicSliderBar<int>
                {
                    Width = 100,
                    Height = 20,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Current = Current
                },
                _valueText = new SpriteText
                {
                    Font = new FontUsage("", 14),
                    Colour = new Color4(255, 102, 170, 255),
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft
                }
            }
        };

        Current.BindValueChanged(e => _valueText.Text = $"{e.NewValue} min", true);
    }
}

/// <summary>
/// Small utility button for graph controls.
/// </summary>
public partial class SessionSmallButton : CompositeDrawable, IHasTooltip
{
    private readonly string _label;
    private Box _background = null!;
    private Box _hoverOverlay = null!;
    private SpriteText _labelText = null!;
    private bool _enabled = true;

    private readonly Color4 _normalColor = new Color4(60, 60, 65, 255);
    private readonly Color4 _disabledColor = new Color4(45, 45, 50, 255);

    public LocalisableString TooltipText { get; set; }
    public event Action? Clicked;

    public SessionSmallButton(string label)
    {
        _label = label;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        Masking = true;
        CornerRadius = 4;

        InternalChildren = new Drawable[]
        {
            _background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = _normalColor
            },
            _hoverOverlay = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Color4.White,
                Alpha = 0
            },
            _labelText = new SpriteText
            {
                Text = _label,
                Font = new FontUsage("", 14),
                Colour = new Color4(180, 180, 180, 255),
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre
            }
        };
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _background?.FadeColour(enabled ? _normalColor : _disabledColor, 150);
        _labelText?.FadeColour(enabled ? new Color4(180, 180, 180, 255) : new Color4(100, 100, 100, 255), 150);
    }

    protected override bool OnHover(HoverEvent e)
    {
        if (_enabled)
            _hoverOverlay.FadeTo(0.15f, 100);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        _hoverOverlay.FadeTo(0, 100);
        base.OnHoverLost(e);
    }

    protected override bool OnClick(ClickEvent e)
    {
        if (!_enabled) return false;

        _hoverOverlay.FadeTo(0.3f, 50).Then().FadeTo(0.15f, 100);
        Clicked?.Invoke();
        return true;
    }
}

/// <summary>
/// Button for generating a session.
/// </summary>
public partial class SessionGenerateButton : CompositeDrawable, IHasTooltip
{
    private Box _background = null!;
    private Box _hoverOverlay = null!;
    private SpriteText _label = null!;
    private bool _enabled = true;

    private readonly Color4 _enabledColor = new Color4(255, 102, 170, 255);
    private readonly Color4 _disabledColor = new Color4(80, 80, 85, 255);

    public LocalisableString TooltipText { get; set; }
    public event Action? Clicked;

    [BackgroundDependencyLoader]
    private void load()
    {
        Masking = true;
        CornerRadius = 6;

        InternalChildren = new Drawable[]
        {
            _background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = _enabledColor
            },
            _hoverOverlay = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Color4.White,
                Alpha = 0
            },
            _label = new SpriteText
            {
                Text = "Generate Session",
                Font = new FontUsage("", 15, "Bold"),
                Colour = Color4.White,
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre
            }
        };
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _background.FadeColour(enabled ? _enabledColor : _disabledColor, 150);
        _label.FadeColour(enabled ? Color4.White : new Color4(120, 120, 120, 255), 150);
    }

    protected override bool OnHover(HoverEvent e)
    {
        if (_enabled)
            _hoverOverlay.FadeTo(0.15f, 100);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        _hoverOverlay.FadeTo(0, 100);
        base.OnHoverLost(e);
    }

    protected override bool OnClick(ClickEvent e)
    {
        if (!_enabled)
            return false;

        _hoverOverlay.FadeTo(0.3f, 50).Then().FadeTo(0.15f, 100);
        Clicked?.Invoke();
        return true;
    }
}

/// <summary>
/// Loading spinner for session planning.
/// </summary>
public partial class SessionPlanningSpinner : CompositeDrawable
{
    private Box _spinner = null!;

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChild = _spinner = new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = new Color4(255, 102, 170, 255),
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre
        };
    }

    protected override void Update()
    {
        base.Update();
        _spinner.Rotation += (float)(Clock.ElapsedFrameTime * 0.2);
    }
}
