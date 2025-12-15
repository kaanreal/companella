using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Platform;
using osuTK;
using osuTK.Graphics;

namespace OsuMappingHelper.Components;

/// <summary>
/// Custom osu!-styled title bar with window controls.
/// </summary>
public partial class CustomTitleBar : CompositeDrawable
{
    private readonly Color4 _accentColor = new Color4(255, 102, 170, 255);
    private readonly Color4 _backgroundColor = new Color4(20, 18, 25, 255);
    private readonly Color4 _hoverColor = new Color4(255, 130, 190, 255);
    private readonly Color4 _closeHoverColor = new Color4(255, 80, 80, 255);
    
    private IWindow? _window;
    private SpriteText _titleText = null!;
    private SpriteText _maximizeButtonText = null!;
    private WindowButton _minimizeButton = null!;
    private WindowButton _maximizeButton = null!;
    private WindowButton _closeButton = null!;
    
    private bool _isDragging;
    private Vector2 _dragStartPosition;

    [Resolved]
    private GameHost Host { get; set; } = null!;

    [BackgroundDependencyLoader]
    private void load()
    {
        _window = Host.Window;
        
        RelativeSizeAxes = Axes.X;
        Height = 32;
        Anchor = Anchor.TopLeft;
        Origin = Anchor.TopLeft;

        // Create window control buttons
        _minimizeButton = CreateWindowButton("−", Anchor.TopRight, () =>
        {
            Schedule(() =>
            {
                // Minimize functionality - may need platform-specific implementation
                // For now, we'll hide the minimize button functionality
                // if (_window != null)
                //     _window.WindowState = osu.Framework.Platform.WindowState.Minimized;
            });
        });
        _minimizeButton.Anchor = Anchor.TopRight;
        _minimizeButton.Origin = Anchor.TopRight;
        _minimizeButton.X = -80;

        _maximizeButton = CreateMaximizeButton();
        _maximizeButton.Anchor = Anchor.TopRight;
        _maximizeButton.Origin = Anchor.TopRight;
        _maximizeButton.X = -40;

        _closeButton = CreateWindowButton("×", Anchor.TopRight, () =>
        {
            Schedule(() => Host.Exit());
        }, isCloseButton: true);
        _closeButton.Anchor = Anchor.TopRight;
        _closeButton.Origin = Anchor.TopRight;

        InternalChildren = new Drawable[]
        {
            // Background with accent line at bottom
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = _backgroundColor
            },
            new Box
            {
                RelativeSizeAxes = Axes.X,
                Height = 2,
                Anchor = Anchor.BottomLeft,
                Origin = Anchor.BottomLeft,
                Colour = _accentColor,
                Alpha = 0.6f
            },
            // Title text
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Left = 15, Right = 120 }, // Right padding for buttons
                Child = _titleText = new SpriteText
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Text = "Companella!",
                    Font = new FontUsage("", 13, "SemiBold"),
                    Colour = new Color4(230, 230, 230, 255)
                }
            },
            // Window control buttons (right side)
            new Container
            {
                RelativeSizeAxes = Axes.Y,
                Width = 120,
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Children = new Drawable[]
                {
                    _minimizeButton,
                    _maximizeButton,
                    _closeButton
                }
            }
        };
    }

    private WindowButton CreateMaximizeButton()
    {
        return new WindowButton(_hoverColor, () =>
        {
            Schedule(() =>
            {
                if (_window != null)
                {
                    _window.WindowState = _window.WindowState == osu.Framework.Platform.WindowState.Fullscreen 
                        ? osu.Framework.Platform.WindowState.Normal 
                        : osu.Framework.Platform.WindowState.Fullscreen;
                    UpdateMaximizeButtonText();
                }
            });
        })
        {
            Size = new Vector2(40, 32),
            Anchor = Anchor.TopRight,
            Origin = Anchor.TopRight,
            Child = _maximizeButtonText = new SpriteText
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Text = "□",
                Font = new FontUsage("", 16, "Bold"),
                Colour = new Color4(200, 200, 200, 255)
            }
        };
    }

    private void UpdateMaximizeButtonText()
    {
        if (_window != null && _maximizeButtonText != null)
        {
            _maximizeButtonText.Text = _window.WindowState == osu.Framework.Platform.WindowState.Fullscreen ? "❐" : "□";
        }
    }

    private WindowButton CreateWindowButton(string text, Anchor anchor, Action onClick, bool isCloseButton = false)
    {
        return new WindowButton(isCloseButton ? _closeHoverColor : _hoverColor, onClick)
        {
            Size = new Vector2(40, 32),
            Anchor = anchor,
            Origin = anchor,
            Child = new SpriteText
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Text = text,
                Font = new FontUsage("", isCloseButton ? 20 : 18, "Bold"),
                Colour = new Color4(200, 200, 200, 255)
            }
        };
    }

    protected override bool OnDragStart(DragStartEvent e)
    {
        if (_window != null)
        {
            _isDragging = true;
            _dragStartPosition = e.ScreenSpaceMousePosition;
            // Note: Window positioning API may vary by platform
            // For now, we'll just track the drag but not move the window
            return true;
        }
        return base.OnDragStart(e);
    }

    protected override void OnDrag(DragEvent e)
    {
        if (_isDragging && _window != null)
        {
            // Window dragging would require platform-specific implementation
            // This is a placeholder for future implementation
        }
        base.OnDrag(e);
    }

    protected override void OnDragEnd(DragEndEvent e)
    {
        _isDragging = false;
        base.OnDragEnd(e);
    }

    protected override bool OnMouseDown(MouseDownEvent e)
    {
        // Allow dragging from anywhere on the title bar
        return true;
    }
}

/// <summary>
/// A clickable button for window controls with hover effects.
/// </summary>
public partial class WindowButton : CompositeDrawable
{
    private readonly Color4 _hoverColor;
    private readonly Action _onClick;
    private Box _hoverOverlay = null!;
    private Drawable? _content;

    public WindowButton(Color4 hoverColor, Action onClick)
    {
        _hoverColor = hoverColor;
        _onClick = onClick;
    }

    public Drawable? Child
    {
        get => _content;
        set
        {
            if (_content != null)
                RemoveInternal(_content, false);
            _content = value;
            if (_content != null && IsLoaded)
                AddInternal(_content);
        }
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        Masking = true;
        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Color4.Transparent
            },
            _hoverOverlay = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = _hoverColor,
                Alpha = 0
            }
        };
        
        if (_content != null)
            AddInternal(_content);
    }

    protected override bool OnHover(HoverEvent e)
    {
        _hoverOverlay.FadeTo(0.3f, 100);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        _hoverOverlay.FadeTo(0, 100);
        base.OnHoverLost(e);
    }

    protected override bool OnClick(ClickEvent e)
    {
        _onClick?.Invoke();
        return true;
    }
}
