using System.Drawing;
using System.Windows.Forms;

namespace CompanellaInstaller;

/// <summary>
/// Main installer form with a modern dark UI.
/// </summary>
public partial class InstallerForm : Form
{
    private readonly InstallerService _installerService;
    private ReleaseInfo? _releaseInfo;
    private string _installPath;
    private bool _isInstalling;

    // UI Controls
    private Panel _headerPanel = null!;
    private Label _titleLabel = null!;
    private Label _subtitleLabel = null!;
    private Panel _contentPanel = null!;
    private Label _versionLabel = null!;
    private RichTextBox _releaseNotesBox = null!;
    private Label _pathLabel = null!;
    private TextBox _pathTextBox = null!;
    private Button _browseButton = null!;
    private CheckBox _shortcutCheckBox = null!;
    private CheckBox _launchCheckBox = null!;
    private ProgressBar _progressBar = null!;
    private Label _statusLabel = null!;
    private Button _installButton = null!;
    private Button _cancelButton = null!;

    // Colors
    private readonly Color _backgroundColor = Color.FromArgb(30, 30, 35);
    private readonly Color _headerColor = Color.FromArgb(25, 25, 30);
    private readonly Color _accentColor = Color.FromArgb(255, 102, 170);
    private readonly Color _textColor = Color.White;
    private readonly Color _secondaryTextColor = Color.FromArgb(180, 180, 180);
    private readonly Color _inputBackgroundColor = Color.FromArgb(45, 45, 50);

    public InstallerForm()
    {
        _installerService = new InstallerService();
        _installPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Companella");

        InitializeComponent();
        SetupEventHandlers();
    }

    private void InitializeComponent()
    {
        // Form settings
        Text = "Companella Installer";
        Size = new Size(500, 520);
        MinimumSize = new Size(450, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = _backgroundColor;
        ForeColor = _textColor;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        // Try to load icon
        try
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
            }
        }
        catch { }

        // Header Panel
        _headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 80,
            BackColor = _headerColor
        };

        _titleLabel = new Label
        {
            Text = "Companella Installer",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = _accentColor,
            AutoSize = true,
            Location = new Point(20, 15)
        };

        _subtitleLabel = new Label
        {
            Text = "Easy installation for Companella",
            Font = new Font("Segoe UI", 10),
            ForeColor = _secondaryTextColor,
            AutoSize = true,
            Location = new Point(22, 48)
        };

        _headerPanel.Controls.AddRange(new Control[] { _titleLabel, _subtitleLabel });

        // Content Panel
        _contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20)
        };

        // Version Label
        _versionLabel = new Label
        {
            Text = "Fetching latest version...",
            Font = new Font("Segoe UI", 10),
            ForeColor = _textColor,
            Location = new Point(20, 10),
            Size = new Size(440, 25)
        };

        // Release Notes
        var releaseNotesLabel = new Label
        {
            Text = "Release Notes:",
            Font = new Font("Segoe UI", 9),
            ForeColor = _secondaryTextColor,
            Location = new Point(20, 40),
            AutoSize = true
        };

        _releaseNotesBox = new RichTextBox
        {
            Location = new Point(20, 60),
            Size = new Size(440, 100),
            BackColor = _inputBackgroundColor,
            ForeColor = _secondaryTextColor,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            Font = new Font("Segoe UI", 9),
            Text = "Loading..."
        };

        // Install Path
        _pathLabel = new Label
        {
            Text = "Installation Folder:",
            Font = new Font("Segoe UI", 9),
            ForeColor = _secondaryTextColor,
            Location = new Point(20, 175),
            AutoSize = true
        };

        _pathTextBox = new TextBox
        {
            Location = new Point(20, 195),
            Size = new Size(350, 25),
            BackColor = _inputBackgroundColor,
            ForeColor = _textColor,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10),
            Text = _installPath
        };

        _browseButton = new Button
        {
            Text = "Browse",
            Location = new Point(380, 194),
            Size = new Size(80, 27),
            BackColor = _inputBackgroundColor,
            ForeColor = _textColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9),
            Cursor = Cursors.Hand
        };
        _browseButton.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 85);

        // Checkboxes
        _shortcutCheckBox = new CheckBox
        {
            Text = "Create Desktop Shortcut",
            Font = new Font("Segoe UI", 10),
            ForeColor = _textColor,
            Location = new Point(20, 235),
            AutoSize = true,
            Checked = true
        };

        _launchCheckBox = new CheckBox
        {
            Text = "Launch Companella after installation",
            Font = new Font("Segoe UI", 10),
            ForeColor = _textColor,
            Location = new Point(20, 265),
            AutoSize = true,
            Checked = true
        };

        // Progress Bar
        _progressBar = new ProgressBar
        {
            Location = new Point(20, 305),
            Size = new Size(440, 23),
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };

        // Status Label
        _statusLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 9),
            ForeColor = _secondaryTextColor,
            Location = new Point(20, 335),
            Size = new Size(440, 20),
            Visible = false
        };

        // Buttons
        _cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(270, 365),
            Size = new Size(90, 35),
            BackColor = Color.FromArgb(80, 80, 85),
            ForeColor = _textColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10),
            Cursor = Cursors.Hand
        };
        _cancelButton.FlatAppearance.BorderSize = 0;

        _installButton = new Button
        {
            Text = "Install",
            Location = new Point(370, 365),
            Size = new Size(90, 35),
            BackColor = _accentColor,
            ForeColor = _textColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Enabled = false
        };
        _installButton.FlatAppearance.BorderSize = 0;

        _contentPanel.Controls.AddRange(new Control[]
        {
            _versionLabel,
            releaseNotesLabel,
            _releaseNotesBox,
            _pathLabel,
            _pathTextBox,
            _browseButton,
            _shortcutCheckBox,
            _launchCheckBox,
            _progressBar,
            _statusLabel,
            _cancelButton,
            _installButton
        });

        Controls.AddRange(new Control[] { _contentPanel, _headerPanel });
    }

    private void SetupEventHandlers()
    {
        _browseButton.Click += OnBrowseClicked;
        _installButton.Click += OnInstallClicked;
        _cancelButton.Click += OnCancelClicked;
        _pathTextBox.TextChanged += (s, e) => _installPath = _pathTextBox.Text;

        _installerService.DownloadProgressChanged += OnDownloadProgressChanged;
        _installerService.StatusChanged += OnStatusChanged;
        _installerService.Error += OnError;

        Load += OnFormLoad;
        FormClosing += OnFormClosing;
    }

    private async void OnFormLoad(object? sender, EventArgs e)
    {
        _releaseInfo = await _installerService.GetLatestReleaseAsync();

        if (_releaseInfo != null)
        {
            _versionLabel.Text = $"Version: {_releaseInfo.TagName} ({FormatBytes(_releaseInfo.DownloadSize)})";
            _releaseNotesBox.Text = string.IsNullOrWhiteSpace(_releaseInfo.Body)
                ? "No release notes available."
                : _releaseInfo.Body;
            _installButton.Enabled = true;
        }
        else
        {
            _versionLabel.Text = "Failed to fetch version information";
            _releaseNotesBox.Text = "Could not connect to GitHub. Please check your internet connection.";
        }
    }

    private void OnBrowseClicked(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select Installation Folder",
            SelectedPath = _installPath,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _installPath = dialog.SelectedPath;
            _pathTextBox.Text = _installPath;
        }
    }

    private async void OnInstallClicked(object? sender, EventArgs e)
    {
        if (_releaseInfo == null) return;

        if (_isInstalling)
        {
            // Installation complete - this is now the "Finish" button
            if (_launchCheckBox.Checked)
            {
                _installerService.LaunchApplication(_installPath);
            }
            Close();
            return;
        }

        _isInstalling = true;
        SetUIState(installing: true);

        var success = await _installerService.DownloadAndInstallAsync(_releaseInfo, _installPath);

        if (success)
        {
            if (_shortcutCheckBox.Checked)
            {
                _installerService.CreateDesktopShortcut(_installPath);
            }

            _statusLabel.Text = "Installation completed successfully!";
            _statusLabel.ForeColor = Color.FromArgb(100, 255, 100);
            _installButton.Text = "Finish";
            _installButton.Enabled = true;
            _cancelButton.Visible = false;
        }
        else
        {
            _isInstalling = false;
            SetUIState(installing: false);
            _statusLabel.Text = "Installation failed. Please try again.";
            _statusLabel.ForeColor = Color.FromArgb(255, 100, 100);
        }
    }

    private void OnCancelClicked(object? sender, EventArgs e)
    {
        if (_isInstalling)
        {
            _installerService.CancelDownload();
            _isInstalling = false;
            SetUIState(installing: false);
            _statusLabel.Text = "Installation cancelled.";
        }
        else
        {
            Close();
        }
    }

    private void OnDownloadProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        if (InvokeRequired)
        {
            Invoke(() => OnDownloadProgressChanged(sender, e));
            return;
        }

        _progressBar.Value = Math.Min(e.ProgressPercentage, 100);
        _statusLabel.Text = e.Status;
    }

    private void OnStatusChanged(object? sender, string status)
    {
        if (InvokeRequired)
        {
            Invoke(() => OnStatusChanged(sender, status));
            return;
        }

        _statusLabel.Text = status;
    }

    private void OnError(object? sender, string error)
    {
        if (InvokeRequired)
        {
            Invoke(() => OnError(sender, error));
            return;
        }

        _statusLabel.Text = error;
        _statusLabel.ForeColor = Color.FromArgb(255, 100, 100);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_isInstalling && _installButton.Text != "Finish")
        {
            var result = MessageBox.Show(
                "Installation is in progress. Are you sure you want to cancel?",
                "Cancel Installation",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.No)
            {
                e.Cancel = true;
                return;
            }

            _installerService.CancelDownload();
        }

        _installerService.Dispose();
    }

    private void SetUIState(bool installing)
    {
        _pathTextBox.Enabled = !installing;
        _browseButton.Enabled = !installing;
        _shortcutCheckBox.Enabled = !installing;
        _launchCheckBox.Enabled = !installing;
        _installButton.Enabled = !installing;
        _progressBar.Visible = installing;
        _statusLabel.Visible = true;
        _progressBar.Value = 0;

        if (installing)
        {
            _cancelButton.Text = "Cancel";
        }
        else
        {
            _cancelButton.Text = "Cancel";
            _installButton.Text = "Install";
            _installButton.Enabled = _releaseInfo != null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }
}
