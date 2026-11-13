using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RemoteDesk.Core;
using RemoteDesk.Protocol;
using RemoteDesk.Transport;
using System.Diagnostics;
using System.Globalization;

namespace RemoteDesk.Client;

internal sealed class MainWindow : Window, IAsyncDisposable
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush AccentHoverBrush = new SolidColorBrush(Color.Parse("#DC3E43"));
    private static readonly IBrush AccentSoftBg = new SolidColorBrush(Color.Parse("#FFF5F5"));
    private static readonly IBrush PageBrush = new SolidColorBrush(Color.Parse("#F8F9FA"));
    private static readonly IBrush CardBrush = Brushes.White;
    private static readonly IBrush CardBorderBrush = new SolidColorBrush(Color.Parse("#E9ECEF"));
    private static readonly IBrush PrimaryTextBrush = new SolidColorBrush(Color.Parse("#1A1D20"));
    private static readonly IBrush SecondaryTextBrush = new SolidColorBrush(Color.Parse("#6C757D"));
    private static readonly IBrush MutedBgBrush = new SolidColorBrush(Color.Parse("#F1F3F5"));

    private readonly TextBox _server = new()
    {
        Text = ReadConfiguredEnvironmentVariable("REMOTEDESK_SERVER") ?? "http://localhost:5080",
    };
    private readonly TextBox _operatorKey = new()
    {
        Text = ReadConfiguredEnvironmentVariable("REMOTEDESK_OPERATOR_KEY") ?? "local-operator-change-me",
        PasswordChar = '●',
    };
    private readonly TextBox _operatorName = new() { Text = Environment.UserName };
    private readonly TextBox _deviceId = new()
    {
        PlaceholderText = "Enter Remote Address",
        FontSize = 16,
    };
    private readonly CheckBox _viewScreen = new() { Content = "View screen", IsChecked = true };
    private readonly CheckBox _controlInput = new() { Content = "Control input", IsChecked = true };
    private readonly CheckBox _transferFiles = new() { Content = "File transfer" };
    private readonly CheckBox _reboot = new() { Content = "Remote reboot" };
    private readonly Button _connect = new() { Content = "Connect  →" };
    private readonly TextBlock _status = new()
    {
        Text = "Ready to connect",
        TextWrapping = TextWrapping.Wrap,
        Foreground = SecondaryTextBrush,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBox _filePath = new() { PlaceholderText = "Choose a file to send" };
    private readonly Button _sendFile = new() { Content = "Send file", IsEnabled = false };
    private readonly TextBlock _thisDeviceId = new()
    {
        Text = "Creating your SwiftDesk ID…",
        FontSize = 46,
        FontWeight = FontWeight.SemiBold,
        Foreground = AccentBrush,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Button _copyDeviceId = new()
    {
        Content = "Copy address",
        IsEnabled = false,
    };

    private readonly Image _screenImage = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Focusable = true,
    };

    private readonly TextBlock _screenPlaceholderText = new()
    {
        Text = "REMOTE DESKTOP\n\nYour secure session will appear here",
        Foreground = new SolidColorBrush(Color.Parse("#98A2B3")),
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 15,
        FontWeight = FontWeight.Medium,
    };

    private readonly TextBlock _authorizationMessage = new()
    {
        Text = "Waiting for the remote user to approve this session.",
        Foreground = SecondaryTextBrush,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
    };
    private readonly Button _cancelAuthorization = new() { Content = "Cancel" };
    private readonly TextBlock _viewerAddress = new()
    {
        FontSize = 16,
        FontWeight = FontWeight.SemiBold,
        Foreground = PrimaryTextBrush,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Button _disconnectSession = new() { Content = "Disconnect" };
    private readonly Button _toggleFullScreen = new() { Content = "Full screen" };
    private readonly StackPanel _recentSessionsList = new() { Spacing = 8 };

    private readonly Border _screenContainer;
    private readonly Border _sessionWorkspacePanel;
    private readonly Border _authorizationOverlay;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _deviceIdRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly RecentSessionHistory _recentSessionHistory = new();
    private HttpClient? _httpClient;
    private ClientSession? _session;
    private RecentSessionResponse? _currentRecentSession;
    private CancellationTokenSource? _connectionAttemptCts;
    private double _remoteFrameWidth;
    private double _remoteFrameHeight;
    private byte[]? _latestPendingFrameBytes;
    private int _isRenderingFrame;
    private int _disposed;

    public MainWindow()
    {
        Title = "SwiftDesk — Remote Desktop";
        Width = 1280;
        Height = 860;
        MinWidth = 980;
        MinHeight = 680;
        Background = PageBrush;

        var appIconBitmap = LoadAssetBitmap("app-icon.png") ?? LoadAssetBitmap("logo.png");
        if (appIconBitmap is not null)
        {
            Icon = new WindowIcon(appIconBitmap);
        }

        var logoBitmap = LoadAssetBitmap("logo.png");

        StyleTextBox(_server, 38);
        StyleTextBox(_operatorKey, 38);
        StyleTextBox(_operatorName, 38);
        StyleTextBox(_deviceId, 46);
        StyleTextBox(_filePath, 38);
        StylePrimaryButton(_connect, 46);
        StyleSecondaryButton(_sendFile);
        StyleSecondaryButton(_cancelAuthorization);

        _copyDeviceId.Height = 34;
        _copyDeviceId.Padding = new Thickness(14, 0);
        _copyDeviceId.Background = AccentSoftBg;
        _copyDeviceId.Foreground = AccentBrush;
        _copyDeviceId.BorderBrush = new SolidColorBrush(Color.Parse("#FFD0D3"));
        _copyDeviceId.BorderThickness = new Thickness(1);
        _copyDeviceId.CornerRadius = new CornerRadius(8);
        _copyDeviceId.FontWeight = FontWeight.SemiBold;
        _copyDeviceId.FontSize = 13;

        // --- HERO SECTION: Dual Compact Cards ---
        // Left Card: This Device Address
        _thisDeviceId.TextAlignment = TextAlignment.Center;
        _thisDeviceId.LetterSpacing = 1.5;
        _thisDeviceId.FontSize = 32;

        var statusDot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse("#12B76A")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var hostPill = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                statusDot,
                new TextBlock
                {
                    Text = $"{Environment.MachineName} (Ready)",
                    Foreground = SecondaryTextBrush,
                    FontSize = 12,
                    FontWeight = FontWeight.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        var thisAddressCard = Card(new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                new TextBlock
                {
                    Text = "THIS COMPUTER",
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    Foreground = AccentBrush,
                    LetterSpacing = 1.0,
                },
                new TextBlock
                {
                    Text = "Your Address",
                    FontSize = 18,
                    FontWeight = FontWeight.Bold,
                    Foreground = PrimaryTextBrush,
                },
                new Border
                {
                    Background = MutedBgBrush,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 14),
                    Child = new StackPanel
                    {
                        Spacing = 8,
                        Children = { _thisDeviceId, hostPill },
                    },
                },
                _copyDeviceId,
            },
        });

        // Right Card: Connect to Remote Device
        var permissions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children = { _viewScreen, _controlInput, _transferFiles, _reboot },
        };

        var connectBar = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(140)),
            },
            ColumnSpacing = 8,
        };
        connectBar.Children.Add(_deviceId);
        Grid.SetColumn(_connect, 1);
        connectBar.Children.Add(_connect);

        var connectRemoteCard = Card(new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                new TextBlock
                {
                    Text = "REMOTE ACCESS",
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#2563EB")),
                    LetterSpacing = 1.0,
                },
                new TextBlock
                {
                    Text = "Connect to Remote Device",
                    FontSize = 18,
                    FontWeight = FontWeight.Bold,
                    Foreground = PrimaryTextBrush,
                },
                new TextBlock
                {
                    Text = "Enter the 12-digit SwiftDesk address to start a secure, attended session.",
                    FontSize = 12,
                    Foreground = SecondaryTextBrush,
                },
                connectBar,
                permissions,
            },
        });

        var heroGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(1.1, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(1.4, GridUnitType.Star)),
            },
            ColumnSpacing = 16,
        };
        heroGrid.Children.Add(thisAddressCard);
        Grid.SetColumn(connectRemoteCard, 1);
        heroGrid.Children.Add(connectRemoteCard);

        // --- QUICK STATS / FEATURE ROW ---
        var featureRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 12,
        };
        featureRow.Children.Add(CompactFeaturePill(
            "🔒 End-to-End Encrypted",
            "Attended consent required before sharing",
            "#027A48",
            "#ECFDF3"));
        var transferPill = CompactFeaturePill(
            "⚡ WebRTC Low Latency",
            "P2P direct streaming with relay fallback",
            "#2563EB",
            "#EFF8FF");
        Grid.SetColumn(transferPill, 1);
        featureRow.Children.Add(transferPill);
        var auditPill = CompactFeaturePill(
            "🛡️ Zero-Trust Architecture",
            "Signed HMAC tickets with 10-minute expiry",
            "#B54708",
            "#FFFAEB");
        Grid.SetColumn(auditPill, 2);
        featureRow.Children.Add(auditPill);

        // --- RECENT SESSIONS CARD ---
        var recentSessions = Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Auto),
                    },
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Recent Connections",
                            FontSize = 16,
                            FontWeight = FontWeight.Bold,
                            Foreground = PrimaryTextBrush,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = "Click any entry to fill address",
                            FontSize = 12,
                            Foreground = SecondaryTextBrush,
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Right,
                        },
                    },
                },
                new Border
                {
                    MinHeight = 70,
                    Background = MutedBgBrush,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(6),
                    Child = _recentSessionsList,
                },
            },
        });

        var homeGrid = new StackPanel
        {
            Spacing = 14,
            Children = { heroGrid, featureRow, recentSessions },
        };

        var settingsGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 14,
        };
        var serverField = Field("Signaling server", _server);
        var keyField = Field("Operator key", _operatorKey);
        var operatorField = Field("Operator name", _operatorName);
        settingsGrid.Children.Add(serverField);
        Grid.SetColumn(keyField, 1);
        settingsGrid.Children.Add(keyField);
        Grid.SetColumn(operatorField, 2);
        settingsGrid.Children.Add(operatorField);

        var advancedSettings = new Expander
        {
            Header = "Connection settings",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new Border
            {
                Padding = new Thickness(0, 14, 0, 2),
                Child = settingsGrid,
            },
        };

        _cancelAuthorization.Width = 140;
        _cancelAuthorization.HorizontalAlignment = HorizontalAlignment.Right;
        _authorizationOverlay = new Border
        {
            Width = 520,
            Background = Brushes.White,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Authorization",
                        FontSize = 24,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = PrimaryTextBrush,
                    },
                    new TextBlock
                    {
                        Text = "Remote owner confirmation required",
                        FontSize = 15,
                        FontWeight = FontWeight.Medium,
                        Foreground = PrimaryTextBrush,
                    },
                    _authorizationMessage,
                    new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12),
                        Child = new TextBlock
                        {
                            Text = "SwiftDesk uses attended consent. The remote user must choose Allow before screen sharing begins.",
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = SecondaryTextBrush,
                            FontSize = 13,
                        },
                    },
                    _cancelAuthorization,
                },
            },
        };

        var screenGrid = new Grid();
        screenGrid.Children.Add(_screenImage);
        screenGrid.Children.Add(_screenPlaceholderText);
        screenGrid.Children.Add(_authorizationOverlay);

        _screenContainer = new Border
        {
            Background = Brushes.Black,
            ClipToBounds = true,
            Child = screenGrid,
        };

        _screenContainer.PointerMoved += OnScreenPointerMoved;
        _screenContainer.PointerPressed += OnScreenPointerPressed;
        _screenContainer.PointerReleased += OnScreenPointerReleased;
        _screenContainer.PointerWheelChanged += OnScreenPointerWheel;
        KeyDown += OnWindowKeyDown;
        KeyUp += OnWindowKeyUp;

        var viewerToolbar = new Grid
        {
            Height = 58,
            Background = new SolidColorBrush(Color.Parse("#F1F3F5")),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(1)),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 14,
        };
        if (logoBitmap is not null)
        {
            viewerToolbar.Children.Add(new Image
            {
                Source = logoBitmap,
                Height = 26,
                Margin = new Thickness(16, 0, 0, 0),
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            viewerToolbar.Children.Add(new TextBlock
            {
                Text = "SwiftDesk",
                Margin = new Thickness(18, 0, 0, 0),
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var toolbarDivider = new Border
        {
            Width = 1,
            Height = 28,
            Background = CardBorderBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(toolbarDivider, 1);
        viewerToolbar.Children.Add(toolbarDivider);

        var addressPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.Parse("#16A34A")),
                    Child = new TextBlock
                    {
                        Text = "✓",
                        Foreground = Brushes.White,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
                _viewerAddress,
                _status,
            },
        };
        Grid.SetColumn(addressPanel, 2);
        viewerToolbar.Children.Add(addressPanel);

        var viewerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _toggleFullScreen, _disconnectSession },
        };
        Grid.SetColumn(viewerActions, 3);
        viewerToolbar.Children.Add(viewerActions);

        var viewerShell = new DockPanel();
        DockPanel.SetDock(viewerToolbar, Dock.Top);
        viewerShell.Children.Add(viewerToolbar);
        viewerShell.Children.Add(_screenContainer);

        _sessionWorkspacePanel = new Border
        {
            Background = Brushes.Black,
            Child = viewerShell,
        };
        _sessionWorkspacePanel.IsVisible = false;

        var fileGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(140)),
            },
            ColumnSpacing = 12,
        };
        fileGrid.Children.Add(_filePath);
        Grid.SetColumn(_sendFile, 1);
        fileGrid.Children.Add(_sendFile);

        var filePanel = Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "File transfer",
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                },
                new TextBlock
                {
                    Text = "Send a file after the remote user grants file-transfer permission.",
                    FontSize = 13,
                    Foreground = SecondaryTextBrush,
                },
                fileGrid,
            },
        });

        var headerGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        if (logoBitmap is not null)
        {
            var headerLogo = new Image
            {
                Source = logoBitmap,
                Height = 36,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(headerLogo, 0);
            headerGrid.Children.Add(headerLogo);
        }
        else
        {
            headerGrid.Children.Add(new Border
            {
                Width = 40,
                Height = 40,
                Background = AccentBrush,
                CornerRadius = new CornerRadius(11),
                Child = new TextBlock
                {
                    Text = "S",
                    FontSize = 22,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });

            var brand = new StackPanel
            {
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "SwiftDesk",
                        FontSize = 20,
                        FontWeight = FontWeight.Bold,
                        Foreground = PrimaryTextBrush,
                    },
                    new TextBlock
                    {
                        Text = "Fast, consent-first remote support",
                        FontSize = 12,
                        Foreground = SecondaryTextBrush,
                    },
                },
            };
            Grid.SetColumn(brand, 1);
            headerGrid.Children.Add(brand);
        }

        var securityLabel = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#ECFDF3")),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(12, 7),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "Consent protected",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#027A48")),
            },
        };
        Grid.SetColumn(securityLabel, 2);
        headerGrid.Children.Add(securityLabel);

        var header = new Border
        {
            Background = CardBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 14),
            Child = headerGrid,
        };

        var pageContent = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                homeGrid,
                advancedSettings,
                filePanel,
            },
        };

        var homeShell = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        homeShell.Children.Add(header);
        homeShell.Children.Add(new ScrollViewer { Content = pageContent });

        var root = new Grid();
        root.Children.Add(homeShell);
        root.Children.Add(_sessionWorkspacePanel);
        Content = root;

        _controlInput.IsCheckedChanged += (_, _) =>
        {
            if (_controlInput.IsChecked == true)
            {
                _viewScreen.IsChecked = true;
            }
        };

        _connect.Click += ConnectClickedAsync;
        _sendFile.Click += SendFileClickedAsync;
        _copyDeviceId.Click += CopyDeviceIdClickedAsync;
        _cancelAuthorization.Click += (_, _) => _connectionAttemptCts?.Cancel();
        _disconnectSession.Click += DisconnectSessionClickedAsync;
        _toggleFullScreen.Click += (_, _) => ToggleFullScreen();
        Activated += (_, _) =>
        {
            RefreshLocalDeviceId();
            _ = RefreshRecentSessionsAsync();
        };
        Closed += WindowClosed;

        _deviceIdRefreshTimer.Tick += (_, _) => RefreshLocalDeviceId();
        ShowRecentSessionsPlaceholder("Loading recent sessions...");
        _ = InitializeLocalAgentAsync();
        _ = RefreshRecentSessionsAsync();
    }

    private void OnScreenPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_session is null || !_session.Permissions.HasFlag(SessionPermission.ControlInput))
        {
            return;
        }

        var coordinates = GetNormalizedCoordinates(e.GetPosition(_screenImage));
        if (coordinates is not { } position)
        {
            return;
        }

        var (normX, normY) = position;
        _ = _session.SendInputAsync(RemoteInputKind.PointerMove, normX, normY);
    }

    private void OnScreenPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_session is null || !_session.Permissions.HasFlag(SessionPermission.ControlInput))
        {
            return;
        }

        var coordinates = GetNormalizedCoordinates(e.GetPosition(_screenImage));
        if (coordinates is not { } position)
        {
            return;
        }

        _screenImage.Focus();
        var (normX, normY) = position;
        var props = e.GetCurrentPoint(_screenImage).Properties;
        string key = props.IsRightButtonPressed ? "Right" : props.IsMiddleButtonPressed ? "Middle" : "Left";
        _ = _session.SendInputAsync(RemoteInputKind.PointerButtonDown, normX, normY, key);
    }

    private void OnScreenPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_session is null || !_session.Permissions.HasFlag(SessionPermission.ControlInput))
        {
            return;
        }

        var coordinates = GetNormalizedCoordinates(e.GetPosition(_screenImage));
        if (coordinates is not { } position)
        {
            return;
        }

        var (normX, normY) = position;
        var kind = e.GetCurrentPoint(_screenImage).Properties.PointerUpdateKind;
        string key = kind switch
        {
            PointerUpdateKind.RightButtonReleased => "Right",
            PointerUpdateKind.MiddleButtonReleased => "Middle",
            _ => "Left",
        };

        _ = _session.SendInputAsync(RemoteInputKind.PointerButtonUp, normX, normY, key);
    }

    private void OnScreenPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_session is null || !_session.Permissions.HasFlag(SessionPermission.ControlInput))
        {
            return;
        }

        var coordinates = GetNormalizedCoordinates(e.GetPosition(_screenImage));
        if (coordinates is not { } position)
        {
            return;
        }

        var (normX, normY) = position;
        var delta = (int)(e.Delta.Y * 120);
        _ = _session.SendInputAsync(RemoteInputKind.MouseWheel, normX, normY, null, delta);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_session is null || !_session.Permissions.HasFlag(SessionPermission.ControlInput) || _screenImage.Source is null)
        {
            return;
        }

        _ = _session.SendInputAsync(RemoteInputKind.KeyDown, 0, 0, e.Key.ToString());
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (_session is null || !_session.Permissions.HasFlag(SessionPermission.ControlInput) || _screenImage.Source is null)
        {
            return;
        }

        _ = _session.SendInputAsync(RemoteInputKind.KeyUp, 0, 0, e.Key.ToString());
    }

    private (double X, double Y)? GetNormalizedCoordinates(Point pos)
    {
        var availableWidth = _screenImage.Bounds.Width;
        var availableHeight = _screenImage.Bounds.Height;
        if (availableWidth <= 0 || availableHeight <= 0 || _remoteFrameWidth <= 0 || _remoteFrameHeight <= 0)
        {
            return null;
        }

        var scale = Math.Min(availableWidth / _remoteFrameWidth, availableHeight / _remoteFrameHeight);
        var renderedWidth = _remoteFrameWidth * scale;
        var renderedHeight = _remoteFrameHeight * scale;
        var left = (availableWidth - renderedWidth) / 2;
        var top = (availableHeight - renderedHeight) / 2;
        if (pos.X < left || pos.X > left + renderedWidth || pos.Y < top || pos.Y > top + renderedHeight)
        {
            return null;
        }

        return ((pos.X - left) / renderedWidth, (pos.Y - top) / renderedHeight);
    }

    private async void ConnectClickedAsync(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _connectionAttemptCts?.Cancel();
        _connectionAttemptCts?.Dispose();
        _connectionAttemptCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var connectionToken = _connectionAttemptCts.Token;

        _connect.IsEnabled = false;
        _connect.Content = "Connecting...";
        _sessionWorkspacePanel.IsVisible = true;
        _screenPlaceholderText.IsVisible = false;
        _authorizationMessage.Text = "Sending the request to the remote device...";
        _authorizationOverlay.IsVisible = true;
        try
        {
            if (!Uri.TryCreate(_server.Text, UriKind.Absolute, out var serverUri)
                || serverUri.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException("Enter an absolute HTTP or HTTPS signaling URL.");
            }

            var requested = SessionPermission.None;
            if (_viewScreen.IsChecked == true) requested |= SessionPermission.ViewScreen;
            if (_controlInput.IsChecked == true) requested |= SessionPermission.ControlInput;
            if (_transferFiles.IsChecked == true) requested |= SessionPermission.TransferFiles;
            if (_reboot.IsChecked == true) requested |= SessionPermission.RebootDevice;

            var request = new CreateSessionRequest(
                NormalizeDeviceId(_deviceId.Text),
                _operatorName.Text?.Trim() ?? string.Empty,
                requested);
            ProtocolValidation.Validate(request);
            _viewerAddress.Text = FormatDeviceId(request.DeviceId);

            if (_session is not null)
            {
                await _session.DisposeAsync();
                _session = null;
            }

            _httpClient?.Dispose();
            _httpClient = new HttpClient { BaseAddress = serverUri };
            var api = new SignalingApiClient(_httpClient);
            var created = await api.CreateSessionAsync(
                _operatorKey.Text ?? string.Empty,
                request,
                connectionToken);

            _currentRecentSession = new RecentSessionResponse(
                created.SessionId,
                request.DeviceId,
                request.OperatorName,
                SessionPermission.None,
                created.State,
                DateTimeOffset.Now);
            await RememberRecentSessionAsync(_currentRecentSession);

            _authorizationMessage.Text = $"Waiting for approval from {FormatDeviceId(request.DeviceId)}.";
            SetStatus($"Waiting for approval ({created.SessionId})...");
            SessionStatusResponse status;
            do
            {
                await Task.Delay(TimeSpan.FromSeconds(1), connectionToken);
                status = await api.GetSessionStatusAsync(
                    _operatorKey.Text ?? string.Empty,
                    created.SessionId,
                    connectionToken);
            }
            while (status.State == SessionState.Pending);

            _currentRecentSession = _currentRecentSession with
            {
                GrantedPermissions = status.GrantedPermissions,
                State = status.State,
            };
            await RememberRecentSessionAsync(_currentRecentSession);

            if (status.State is not (SessionState.Approved or SessionState.Active)
                || status.ControllerTicket is null)
            {
                throw new InvalidOperationException($"Connection ended with state {status.State}.");
            }

            var socket = await api.ConnectSessionAsync(
                created.SessionId,
                status.ControllerTicket,
                connectionToken);

            _session = new ClientSession(created.SessionId, status.GrantedPermissions, socket);
            _session.OnFrameReceived += OnRemoteFrameReceived;
            _session.OnStatusMessage += SetStatus;

            await _session.StartAsync(connectionToken);

            _currentRecentSession = _currentRecentSession with { State = SessionState.Active };
            await RememberRecentSessionAsync(_currentRecentSession);

            _sendFile.IsEnabled = status.GrantedPermissions.HasFlag(SessionPermission.TransferFiles);
            _sessionWorkspacePanel.IsVisible = true;
            _authorizationOverlay.IsVisible = false;
            _screenPlaceholderText.IsVisible = true;
            SetStatus($"Connected with permissions: {status.GrantedPermissions}");
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            _authorizationOverlay.IsVisible = false;
            _sessionWorkspacePanel.IsVisible = false;
            SetStatus("Connection cancelled");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            SetStatus("Closed");
        }
        catch (Exception exception)
        {
            _authorizationOverlay.IsVisible = false;
            _sessionWorkspacePanel.IsVisible = false;
            SetStatus($"Connection failed: {exception.Message}");
        }
        finally
        {
            _connect.IsEnabled = true;
            _connect.Content = "Connect  ->";
            _ = RefreshRecentSessionsAsync();
        }
    }

    private async Task RefreshRecentSessionsAsync()
    {
        IReadOnlyList<RecentSessionResponse> cached;
        try
        {
            cached = await _recentSessionHistory.LoadAsync(_shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }

        if (!Uri.TryCreate(_server.Text, UriKind.Absolute, out var serverUri)
            || serverUri.Scheme is not ("http" or "https"))
        {
            RenderRecentSessionsOnUiThread(cached);
            return;
        }

        try
        {
            using var httpClient = new HttpClient { BaseAddress = serverUri };
            var api = new SignalingApiClient(httpClient);
            var serverRecent = await api.GetRecentSessionsAsync(
                _operatorKey.Text ?? string.Empty,
                50,
                _shutdown.Token);
            var recent = await _recentSessionHistory.MergeAndSaveAsync(
                serverRecent,
                10,
                _shutdown.Token);
            RenderRecentSessionsOnUiThread(recent);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Expected while the application is closing.
        }
        catch
        {
            if (cached.Count > 0)
            {
                RenderRecentSessionsOnUiThread(cached.Take(10).ToArray());
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_recentSessionsList.Children.Count == 0
                        || _recentSessionsList.Children[0] is TextBlock { Text: "Loading recent sessions..." })
                    {
                        ShowRecentSessionsPlaceholder("Recent sessions are unavailable.");
                    }
                });
            }
        }
    }

    private void RenderRecentSessionsOnUiThread(IReadOnlyList<RecentSessionResponse> sessions)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RenderRecentSessions(sessions);
        }
        else
        {
            Dispatcher.UIThread.Post(() => RenderRecentSessions(sessions));
        }
    }

    private async Task RememberRecentSessionAsync(RecentSessionResponse session)
    {
        try
        {
            await _recentSessionHistory.UpsertAsync(session, _shutdown.Token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // History persistence is best-effort and must not break a remote session.
        }
    }

    private void RenderRecentSessions(IReadOnlyList<RecentSessionResponse> sessions)
    {
        _recentSessionsList.Children.Clear();
        if (sessions.Count == 0)
        {
            ShowRecentSessionsPlaceholder("No recent remote connections yet.");
            return;
        }

        foreach (var session in sessions)
        {
            var content = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(new GridLength(170)),
                    new ColumnDefinition(new GridLength(130)),
                },
                ColumnSpacing = 12,
            };
            content.Children.Add(new TextBlock
            {
                Text = FormatDeviceId(session.DeviceId),
                FontWeight = FontWeight.SemiBold,
                Foreground = PrimaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var createdAt = new TextBlock
            {
                Text = session.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                Foreground = SecondaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(createdAt, 1);
            content.Children.Add(createdAt);

            var state = new TextBlock
            {
                Text = session.State.ToString(),
                Foreground = session.State == SessionState.Active
                    ? new SolidColorBrush(Color.Parse("#15803D"))
                    : SecondaryTextBrush,
                FontWeight = FontWeight.Medium,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(state, 2);
            content.Children.Add(state);

            var row = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 10),
                Background = Brushes.White,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
            };
            row.Click += (_, _) =>
            {
                _deviceId.Text = FormatDeviceId(session.DeviceId);
                _deviceId.Focus();
            };
            _recentSessionsList.Children.Add(row);
        }
    }

    private void ShowRecentSessionsPlaceholder(string message)
    {
        _recentSessionsList.Children.Clear();
        _recentSessionsList.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = SecondaryTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 22),
        });
    }

    private void OnRemoteFrameReceived(byte[] frameBytes)
    {
        Interlocked.Exchange(ref _latestPendingFrameBytes, frameBytes);

        if (Interlocked.CompareExchange(ref _isRenderingFrame, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(RenderPendingFrame, DispatcherPriority.Render);
        }
    }

    private void RenderPendingFrame()
    {
        try
        {
            var latest = Interlocked.Exchange(ref _latestPendingFrameBytes, null);
            if (latest is not null)
            {
                using var stream = new MemoryStream(latest);
                var bitmap = new Bitmap(stream);
                var previous = _screenImage.Source as IDisposable;
                _screenImage.Source = bitmap;
                _remoteFrameWidth = bitmap.PixelSize.Width;
                _remoteFrameHeight = bitmap.PixelSize.Height;
                previous?.Dispose();
                _screenPlaceholderText.IsVisible = false;
                _sessionWorkspacePanel.IsVisible = true;
            }
        }
        catch
        {
            // Ignore transient frame decoding errors
        }
        finally
        {
            Interlocked.Exchange(ref _isRenderingFrame, 0);
            if (Volatile.Read(ref _latestPendingFrameBytes) is not null
                && Interlocked.CompareExchange(ref _isRenderingFrame, 1, 0) == 0)
            {
                Dispatcher.UIThread.Post(RenderPendingFrame, DispatcherPriority.Render);
            }
        }
    }

    private async void DisconnectSessionClickedAsync(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _connectionAttemptCts?.Cancel();
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        if (_screenImage.Source is IDisposable image)
        {
            image.Dispose();
        }

        _screenImage.Source = null;
        _remoteFrameWidth = 0;
        _remoteFrameHeight = 0;
        _authorizationOverlay.IsVisible = false;
        _screenPlaceholderText.IsVisible = true;
        _sessionWorkspacePanel.IsVisible = false;
        WindowState = WindowState.Normal;
        _toggleFullScreen.Content = "Full screen";
        SetStatus("Disconnected");
        if (_currentRecentSession is not null)
        {
            _currentRecentSession = _currentRecentSession with { State = SessionState.Disconnected };
            await RememberRecentSessionAsync(_currentRecentSession);
        }
        _ = RefreshRecentSessionsAsync();
    }

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
            _toggleFullScreen.Content = "Full screen";
        }
        else
        {
            WindowState = WindowState.FullScreen;
            _toggleFullScreen.Content = "Exit full screen";
        }
    }

    private async void SendFileClickedAsync(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (_session is null)
        {
            return;
        }

        _sendFile.IsEnabled = false;
        try
        {
            await _session.SendFileAsync(_filePath.Text?.Trim() ?? string.Empty, _shutdown.Token);
            SetStatus("File sent and awaiting integrity verification on the agent.");
        }
        catch (Exception exception)
        {
            SetStatus($"File transfer failed: {exception.Message}");
        }
        finally
        {
            _sendFile.IsEnabled = _session.Permissions.HasFlag(SessionPermission.TransferFiles);
        }
    }

    private async void CopyDeviceIdClickedAsync(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        var deviceId = GetLocalDeviceId();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (deviceId is null || clipboard is null)
        {
            SetStatus("Your SwiftDesk address is still being created.");
            return;
        }

        await clipboard.SetTextAsync(deviceId);
        SetStatus("This device's SwiftDesk ID was copied.");
    }

    private void RefreshLocalDeviceId()
    {
        var deviceId = GetLocalDeviceId();
        _thisDeviceId.Text = deviceId is null ? "Creating your SwiftDesk ID…" : FormatDeviceId(deviceId);
        _copyDeviceId.IsEnabled = deviceId is not null;
    }

    private static string? GetLocalDeviceId()
    {
        var configuredId = ReadConfiguredEnvironmentVariable("REMOTEDESK_DEVICE_ID");
        if (!string.IsNullOrWhiteSpace(configuredId))
        {
            return configuredId.Trim();
        }

        return DeviceIdentityStore.Load()?.DeviceId;
    }

    private async Task InitializeLocalAgentAsync()
    {
        if (GetLocalDeviceId() is null)
        {
            var serverText = ReadConfiguredEnvironmentVariable("REMOTEDESK_SERVER");
            var bootstrapToken = ReadConfiguredEnvironmentVariable("REMOTEDESK_BOOTSTRAP_TOKEN");
            if (!Uri.TryCreate(serverText, UriKind.Absolute, out var server)
                || server.Scheme is not ("http" or "https")
                || string.IsNullOrWhiteSpace(bootstrapToken))
            {
                _thisDeviceId.Text = "SwiftDesk server configuration is missing.";
                return;
            }

            try
            {
                using var enrollmentHttpClient = new HttpClient { BaseAddress = server };
                var enrollmentApi = new SignalingApiClient(enrollmentHttpClient);
                var enrollment = await enrollmentApi.EnrollAsync(
                    bootstrapToken,
                    new DeviceEnrollmentRequest(Environment.MachineName, GetPlatformName()),
                    _shutdown.Token);
                DeviceIdentityStore.Save(new LocalDeviceIdentity(enrollment.DeviceId, enrollment.DeviceSecret));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException
                or SignalingApiException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                _thisDeviceId.Text = $"Unable to create ID: {exception.Message}";
                return;
            }
        }

        RefreshLocalDeviceId();
        EnsureInstalledAgentIsRunning();
        _deviceIdRefreshTimer.Start();
    }

    private static string GetPlatformName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        return OperatingSystem.IsLinux() ? "Linux" : "Unknown";
    }

    private static string? ReadConfiguredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);

    private static string NormalizeDeviceId(string? value) => string.Concat(
        (value ?? string.Empty).Where(character => !char.IsWhiteSpace(character) && character != '-'));

    private static string FormatDeviceId(string deviceId)
    {
        if (deviceId.Length != 12 || !deviceId.All(char.IsAsciiDigit))
        {
            return deviceId;
        }

        return $"{deviceId[..3]} {deviceId[3..6]} {deviceId[6..9]} {deviceId[9..]}";
    }

    private static void EnsureInstalledAgentIsRunning()
    {
        try
        {
            if (Process.GetProcessesByName("RemoteDesk.Agent").Length > 0)
            {
                return;
            }

            var applicationDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrWhiteSpace(applicationDirectory))
            {
                return;
            }

            var agentPath = Path.Combine(applicationDirectory, "RemoteDesk.Agent.exe");
            if (!File.Exists(agentPath))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(agentPath)
            {
                UseShellExecute = true,
                WorkingDirectory = applicationDirectory,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            // The Agent can still be started manually from the Start menu.
        }
    }

    private async void WindowClosed(object? sender, EventArgs eventArgs)
    {
        _deviceIdRefreshTimer.Stop();
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connectionAttemptCts?.Cancel();
        _connectionAttemptCts?.Dispose();
        _connectionAttemptCts = null;
        await _shutdown.CancelAsync();
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        _httpClient?.Dispose();
        _httpClient = null;
        _recentSessionHistory.Dispose();
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SetStatus(string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _status.Text = message;
        }
        else
        {
            Dispatcher.UIThread.Post(() => _status.Text = message);
        }
    }

    private static Border CompactFeaturePill(string title, string subtitle, string textColor, string bgColor) => new()
    {
        Background = new SolidColorBrush(Color.Parse(bgColor)),
        BorderBrush = new SolidColorBrush(Color.Parse(textColor)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12),
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 13,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse(textColor)),
                },
                new TextBlock
                {
                    Text = subtitle,
                    FontSize = 11,
                    Foreground = SecondaryTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        },
    };

    private static Border Card(Control child) => new()
    {
        Background = CardBrush,
        BorderBrush = CardBorderBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(20),
        Child = child,
    };

    private static StackPanel Field(string label, Control control) => new()
    {
        Spacing = 6,
        Children = { Label(label), control },
    };

    private static TextBlock Eyebrow(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeight.Bold,
        Foreground = AccentBrush,
        LetterSpacing = 1.2,
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Foreground = PrimaryTextBrush,
        FontSize = 13,
    };

    private static void StyleTextBox(TextBox textBox, double height = 42)
    {
        textBox.Height = height;
        textBox.Padding = new Thickness(12, 0);
        textBox.VerticalContentAlignment = VerticalAlignment.Center;
        textBox.Background = Brushes.White;
        textBox.BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD"));
        textBox.BorderThickness = new Thickness(1);
        textBox.CornerRadius = new CornerRadius(9);
    }

    private static void StylePrimaryButton(Button button, double height = 42)
    {
        button.Height = height;
        button.Padding = new Thickness(18, 0);
        button.Background = AccentBrush;
        button.Foreground = Brushes.White;
        button.BorderThickness = new Thickness(0);
        button.CornerRadius = new CornerRadius(9);
        button.FontWeight = FontWeight.SemiBold;
        button.FontSize = 15;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private static void StyleSecondaryButton(Button button)
    {
        button.Height = 42;
        button.Padding = new Thickness(18, 0);
        button.Background = new SolidColorBrush(Color.Parse("#17212B"));
        button.Foreground = Brushes.White;
        button.BorderThickness = new Thickness(0);
        button.CornerRadius = new CornerRadius(9);
        button.FontWeight = FontWeight.SemiBold;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private static Bitmap? LoadAssetBitmap(string fileName)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "Assets", fileName),
                Path.Combine(baseDir, fileName),
                Path.Combine(baseDir, "..", "..", "..", "Assets", fileName),
                Path.Combine(baseDir, "..", "..", "..", "..", "docs", "assets", fileName),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return new Bitmap(Path.GetFullPath(candidate));
                }
            }
        }
        catch
        {
            // Fallback to text brand
        }

        return null;
    }
}
