using System.Drawing;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Framework.Screens;
using OsuMappingHelper.Screens;
using OsuMappingHelper.Services;

namespace OsuMappingHelper;

/// <summary>
/// Main game class for the Companella! application.
/// </summary>
public partial class OsuMappingHelperGame : Game
{
    private ScreenStack _screenStack = null!;
    private DependencyContainer _dependencies = null!;
    private MainScreen? _mainScreen;
    private TrainingScreen? _trainingScreen;

    /// <summary>
    /// Whether the application is running in training mode.
    /// </summary>
    private readonly bool _trainingMode;

    // Settings saving
    private double _settingsSaveTimer;
    private const double SETTINGS_SAVE_INTERVAL = 2000; // Save every 2 seconds
    private System.Drawing.Size _lastWindowSize;
    private System.Drawing.Point _lastWindowPosition;
    private System.Drawing.Size _targetWindowSize = new System.Drawing.Size(640, 810);

    // Services
    private OsuProcessDetector _processDetector = null!;
    private OsuFileParser _fileParser = null!;
    private OsuFileWriter _fileWriter = null!;
    private AudioExtractor _audioExtractor = null!;
    private TimingPointConverter _timingConverter = null!;
    private DanConfigurationService _danConfigService = null!;
    private TrainingDataService _trainingDataService = null!;
    private UserSettingsService _userSettingsService = null!;

    /// <summary>
    /// Creates a new instance of the game.
    /// </summary>
    /// <param name="trainingMode">Whether to start in training mode.</param>
    public OsuMappingHelperGame(bool trainingMode = false)
    {
        _trainingMode = trainingMode;
    }

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        _dependencies = new DependencyContainer(base.CreateChildDependencies(parent));

        // Create and register services
        _processDetector = new OsuProcessDetector();
        _fileParser = new OsuFileParser();
        _fileWriter = new OsuFileWriter();
        _audioExtractor = new AudioExtractor();
        _timingConverter = new TimingPointConverter();
        _danConfigService = new DanConfigurationService();
        _trainingDataService = new TrainingDataService();
        _userSettingsService = new UserSettingsService();

        _dependencies.CacheAs(_processDetector);
        _dependencies.CacheAs(_fileParser);
        _dependencies.CacheAs(_fileWriter);
        _dependencies.CacheAs(_audioExtractor);
        _dependencies.CacheAs(_timingConverter);
        _dependencies.CacheAs(_danConfigService);
        _dependencies.CacheAs(_trainingDataService);
        _dependencies.CacheAs(_userSettingsService);

        return _dependencies;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        // Add screen stack
        _screenStack = new ScreenStack
        {
            RelativeSizeAxes = Axes.Both
        };

        Add(_screenStack);

        // Push appropriate screen based on mode
        if (_trainingMode)
        {
            _trainingScreen = new TrainingScreen();
            _screenStack.Push(_trainingScreen);
        }
        else
        {
            _mainScreen = new MainScreen();
            _screenStack.Push(_mainScreen);
        }
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        Window.Resizable = false;
        

        // Initialize services
        Task.Run(async () =>
        {
            await _userSettingsService.InitializeAsync();
            await _danConfigService.InitializeAsync();
            
            // Restore window settings on UI thread
            Schedule(() => RestoreWindowSettings());
        });

        // Set window properties after load is complete
        if (Window != null)
        {
            Window.Title = _trainingMode ? "Companella! - Training Mode" : "Companella!";
            
            // Configure osu!-style borderless window
            Window.CursorState = osu.Framework.Platform.CursorState.Default;
            
            // Subscribe to file drop events
            Window.DragDrop += OnWindowFileDrop;
            
            // Subscribe to window state changes to save settings
            Window.WindowStateChanged += OnWindowStateChanged;
            
            // Try to restore window settings immediately if available
            if (_userSettingsService != null)
            {
                RestoreWindowSettings();
            }
        }
    }

    private const int TARGET_WIDTH = 640;
    private const int TARGET_HEIGHT = 810;

    private void ForceWindowResolution(int width, int height)
    {
        if (Window == null) return;

        // Use only osu!framework API to set window properties
        // Note: Window.Size is read-only, so we monitor and correct size changes
        Window.WindowState = osu.Framework.Platform.WindowState.Normal;
        
        // Store target size for monitoring
        _targetWindowSize = new System.Drawing.Size(width, height);
    }

    private void RestoreWindowSettings()
    {
        if (Window == null || _userSettingsService == null) return;

        var settings = _userSettingsService.Settings;
        
        // Restore window state first (this might affect size/position)
        if (Enum.TryParse<osu.Framework.Platform.WindowState>(settings.WindowState, out var windowState))
        {
            Window.WindowState = windowState;
        }
        else
        {
            Window.WindowState = osu.Framework.Platform.WindowState.Normal;
        }
        
        // Note: Window.Size and Window.Position are read-only in osu!framework
        // Window size/position restoration would need to be done through the host/window implementation
        // For now, we'll only restore window state and save the current size/position
        
        // Initialize tracking variables
        _lastWindowSize = Window.Size;
        _lastWindowPosition = Window.Position;
    }

    private void OnWindowStateChanged(WindowState newState)
    {
        SaveWindowSettings();
    }

    private void SaveWindowSettings()
    {
        if (Window == null || _userSettingsService == null) return;

        var settings = _userSettingsService.Settings;
        
        // Save window size and position
        settings.WindowWidth = Window.Size.Width;
        settings.WindowHeight = Window.Size.Height;
        settings.WindowX = Window.Position.X;
        settings.WindowY = Window.Position.Y;
        settings.WindowState = Window.WindowState.ToString();
        
        // Update tracking variables
        _lastWindowSize = Window.Size;
        _lastWindowPosition = Window.Position;
        
        // Save asynchronously
        Task.Run(async () => await _userSettingsService.SaveAsync());
    }

    protected override void Update()
    {
        base.Update();

        // Periodically save window settings and prevent resizing using osu!framework API only
        if (Window != null)
        {
            _settingsSaveTimer += Clock.ElapsedFrameTime;
            if (_settingsSaveTimer >= SETTINGS_SAVE_INTERVAL)
            {
                _settingsSaveTimer = 0;
                
                // Prevent resizing by ensuring window state stays Normal (using osu!framework API)
                if (Window.WindowState != osu.Framework.Platform.WindowState.Normal)
                {
                    Window.WindowState = osu.Framework.Platform.WindowState.Normal;
                }
                
                // Check if window size changed from target (using osu!framework API)
                // Note: We can't prevent the resize, but we can detect it
                var currentSize = Window.Size;
                if (currentSize.Width != _targetWindowSize.Width || 
                    currentSize.Height != _targetWindowSize.Height)
                {
                    // Window was resized - we can't prevent it with osu!framework API alone
                    // but we can ensure window state stays Normal
                    Window.WindowState = osu.Framework.Platform.WindowState.Normal;
                }
                
                // Check if window size or position changed for saving
                if (Window.Size.Width != _lastWindowSize.Width || 
                    Window.Size.Height != _lastWindowSize.Height ||
                    Window.Position.X != _lastWindowPosition.X ||
                    Window.Position.Y != _lastWindowPosition.Y)
                {
                    SaveWindowSettings();
                }
            }
        }
    }

    private void OnWindowFileDrop(string file)
    {
        if (file.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
        {
            if (_trainingMode)
            {
                Schedule(() => _trainingScreen?.HandleFileDrop(file));
            }
            else
            {
                Schedule(() => _mainScreen?.HandleFileDrop(file));
            }
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        if (Window != null)
        {
            Window.DragDrop -= OnWindowFileDrop;
            Window.WindowStateChanged -= OnWindowStateChanged;
            
            // Save settings before closing
            SaveWindowSettings();
        }
        _processDetector?.Dispose();
        base.Dispose(isDisposing);
    }
}
