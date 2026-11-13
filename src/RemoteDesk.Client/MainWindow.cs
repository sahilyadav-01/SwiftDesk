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
    private readonly Button _connect = new()
    {
        Content = "Connect →",
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
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
        Content = "Copy Address",
        IsEnabled = false,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
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

    private enum DashboardTab
    {
        Home,
        Favorites,
        RecentSessions,
        Discovered,
        Invitations,
    }

    private DashboardTab _activeTab = DashboardTab.Home;
    private readonly FavoritesStore _favoritesStore = new();
    private readonly List<(DashboardTab Tab, TextBlock Label, Border Indicator, Border Button)> _navTabs = [];

    // Tab content panels
    private readonly StackPanel _homeTabPanel = new() { Spacing = 14 };
    private readonly StackPanel _favoritesTabPanel = new() { Spacing = 14, IsVisible = false };
    private readonly StackPanel _recentSessionsTabPanel = new() { Spacing = 14, IsVisible = false };
    private readonly StackPanel _discoveredTabPanel = new() { Spacing = 14, IsVisible = false };
    private readonly StackPanel _invitationsTabPanel = new() { Spacing = 14, IsVisible = false };

    // Favorites tab controls
    private readonly TextBox _favAddressInput = new() { PlaceholderText = "Enter 12-digit SwiftDesk Address", FontSize = 14 };
    private readonly TextBox _favNicknameInput = new() { PlaceholderText = "Device nickname (e.g. Office PC, Support Server)", FontSize = 14 };
    private readonly Button _addFavButton = new() { Content = "+ Add to Favorites" };
    private readonly StackPanel _favoritesList = new() { Spacing = 8 };

    // Recent Sessions tab controls
    private readonly StackPanel _fullRecentSessionsList = new() { Spacing = 8 };
    private readonly Button _clearRecentHistoryBtn = new() { Content = "Clear History" };
    private readonly Button _refreshRecentHistoryBtn = new() { Content = "Refresh" };

    // Discovered tab controls
    private readonly StackPanel _discoveredList = new() { Spacing = 8 };
    private readonly Button _scanDiscoveredBtn = new()
    {
        Content = "Scan Network",
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    // Invitations tab controls
    private readonly Button _copyInvitationBtn = new()
    {
        Content = "Copy Invitation Details",
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _invitationStatusText = new()
    {
        Text = "✓ Invitation copied to clipboard!",
        FontSize = 13,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.Parse("#12B76A")),
        IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center,
    };
    private readonly StackPanel _invitationsHistoryList = new() { Spacing = 8 };

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

        _toggleFullScreen.Height = 32;
        _toggleFullScreen.Padding = new Thickness(14, 0);
        _toggleFullScreen.HorizontalContentAlignment = HorizontalAlignment.Center;
        _toggleFullScreen.VerticalContentAlignment = VerticalAlignment.Center;
        _toggleFullScreen.CornerRadius = new CornerRadius(6);
        _toggleFullScreen.Cursor = new Cursor(StandardCursorType.Hand);

        _disconnectSession.Height = 32;
        _disconnectSession.Padding = new Thickness(14, 0);
        _disconnectSession.HorizontalContentAlignment = HorizontalAlignment.Center;
        _disconnectSession.VerticalContentAlignment = VerticalAlignment.Center;
        _disconnectSession.CornerRadius = new CornerRadius(6);
        _disconnectSession.Cursor = new Cursor(StandardCursorType.Hand);

        StyleTextBox(_favAddressInput, 40);
        StyleTextBox(_favNicknameInput, 40);
        StylePrimaryButton(_addFavButton, 40);
        StyleSecondaryButton(_clearRecentHistoryBtn);
        StyleSecondaryButton(_refreshRecentHistoryBtn);
        StyleSecondaryButton(_scanDiscoveredBtn);
        StylePrimaryButton(_copyInvitationBtn, 42);

        _copyDeviceId.Height = 36;
        _copyDeviceId.Padding = new Thickness(20, 0);
        _copyDeviceId.Background = new SolidColorBrush(Color.Parse("#FEF3F2"));
        _copyDeviceId.Foreground = new SolidColorBrush(Color.Parse("#D92D20"));
        _copyDeviceId.BorderBrush = new SolidColorBrush(Color.Parse("#FDA29B"));
        _copyDeviceId.BorderThickness = new Thickness(1);
        _copyDeviceId.CornerRadius = new CornerRadius(8);
        _copyDeviceId.FontWeight = FontWeight.Bold;
        _copyDeviceId.FontSize = 13.5;
        _copyDeviceId.HorizontalAlignment = HorizontalAlignment.Center;
        _copyDeviceId.HorizontalContentAlignment = HorizontalAlignment.Center;
        _copyDeviceId.VerticalContentAlignment = VerticalAlignment.Center;
        _copyDeviceId.Cursor = new Cursor(StandardCursorType.Hand);

        // --- TOP HERO SECTION: Dual Balanced Cards ---
        // Left Card: This Computer / Your Address
        _thisDeviceId.TextAlignment = TextAlignment.Center;
        _thisDeviceId.HorizontalAlignment = HorizontalAlignment.Center;
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

        var readyBadge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#ECFDF3")),
            BorderBrush = new SolidColorBrush(Color.Parse("#A6F4C5")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "● Ready",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#027A48")),
            },
        };
        Grid.SetColumn(readyBadge, 1);

        var thisComputerHeader = new Grid
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
                    Text = "THIS COMPUTER",
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    Foreground = AccentBrush,
                    LetterSpacing = 1.0,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                readyBadge,
            },
        };

        var addressDisplayBox = new Border
        {
            Background = MutedBgBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 14),
            Child = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { _thisDeviceId, hostPill },
            },
        };

        var thisAddressCard = Card(new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                thisComputerHeader,
                new TextBlock
                {
                    Text = "Your SwiftDesk Address",
                    FontSize = 18,
                    FontWeight = FontWeight.Bold,
                    Foreground = PrimaryTextBrush,
                },
                addressDisplayBox,
                _copyDeviceId,
            },
        });

        // Right Card: Connect to Remote Device
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

        var permissions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children = { _viewScreen, _controlInput, _transferFiles, _reboot },
        };

        var attendedBadge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#EFF8FF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#B2DDFF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "Attended Session",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#175CD3")),
            },
        };
        Grid.SetColumn(attendedBadge, 1);

        var remoteAccessHeader = new Grid
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
                    Text = "REMOTE ACCESS",
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#175CD3")),
                    LetterSpacing = 1.0,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                attendedBadge,
            },
        };

        var connectRemoteCard = Card(new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                remoteAccessHeader,
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
            Children = { thisAddressCard },
        };
        Grid.SetColumn(connectRemoteCard, 1);
        heroGrid.Children.Add(connectRemoteCard);

        // --- NAVIGATION TABS BAR ---
        var navigation = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 4, 0, 4),
            Children =
            {
                CreateNavigationTab("Home", DashboardTab.Home),
                CreateNavigationTab("Favorites", DashboardTab.Favorites),
                CreateNavigationTab("Recent Sessions", DashboardTab.RecentSessions),
                CreateNavigationTab("Discovered", DashboardTab.Discovered),
                CreateNavigationTab("Invitations", DashboardTab.Invitations),
            },
        };

        // --- TAB 1: HOME TAB CONTENT ---
        var featureGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 12,
        };
        featureGrid.Children.Add(ModernFeatureTile(
            "🖥️",
            "REMOTE SUPPORT",
            "Connect Remote Device",
            "Start a 60 FPS attended WebRTC session with ultra-low latency.",
            "#D92D20",
            "#FEF3F2",
            () => _deviceId.Focus()));

        var secureTile = ModernFeatureTile(
            "🛡️",
            "ZERO-TRUST SECURITY",
            "Consent On Every Session",
            "Remote owner must explicitly approve screen and input permissions.",
            "#027A48",
            "#ECFDF3",
            () => SwitchTab(DashboardTab.Invitations));
        Grid.SetColumn(secureTile, 1);
        featureGrid.Children.Add(secureTile);

        var transferTile = ModernFeatureTile(
            "📁",
            "FILE TRANSFER",
            "Send Files Securely",
            "SHA-256 chunked file delivery with end-to-end integrity checks.",
            "#B54708",
            "#FEF0C7",
            () =>
            {
                _transferFiles.IsChecked = true;
                _filePath.Focus();
            });
        Grid.SetColumn(transferTile, 2);
        featureGrid.Children.Add(transferTile);

        var discoveryTile = ModernFeatureTile(
            "📡",
            "LOCAL DISCOVERY",
            "Find Nearby Devices",
            "Auto-detect SwiftDesk instances on your local subnet and broker.",
            "#175CD3",
            "#EFF8FF",
            () => SwitchTab(DashboardTab.Discovered));
        Grid.SetColumn(discoveryTile, 3);
        featureGrid.Children.Add(discoveryTile);

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

        _homeTabPanel.Children.Add(featureGrid);
        _homeTabPanel.Children.Add(recentSessions);

        // --- TAB 2: FAVORITES TAB CONTENT ---
        var addFavBar = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(1.4, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(1.6, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(160)),
            },
            ColumnSpacing = 10,
        };
        addFavBar.Children.Add(_favAddressInput);
        Grid.SetColumn(_favNicknameInput, 1);
        addFavBar.Children.Add(_favNicknameInput);
        Grid.SetColumn(_addFavButton, 2);
        addFavBar.Children.Add(_addFavButton);

        var addFavCard = Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Add Favorite Device",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                },
                new TextBlock
                {
                    Text = "Bookmark frequently connected remote computers for quick 1-click access.",
                    FontSize = 12,
                    Foreground = SecondaryTextBrush,
                },
                addFavBar,
            },
        });

        var favListCard = Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Starred Devices",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                },
                _favoritesList,
            },
        });

        _favoritesTabPanel.Children.Add(addFavCard);
        _favoritesTabPanel.Children.Add(favListCard);

        // --- TAB 3: RECENT SESSIONS TAB CONTENT ---
        var recentControlsBar = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "All Recent Connections",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        _refreshRecentHistoryBtn.Height = 32;
        _refreshRecentHistoryBtn.Padding = new Thickness(14, 0);
        Grid.SetColumn(_refreshRecentHistoryBtn, 1);
        recentControlsBar.Children.Add(_refreshRecentHistoryBtn);

        _clearRecentHistoryBtn.Height = 32;
        _clearRecentHistoryBtn.Padding = new Thickness(14, 0);
        _clearRecentHistoryBtn.Foreground = new SolidColorBrush(Color.Parse("#B42318"));
        Grid.SetColumn(_clearRecentHistoryBtn, 2);
        recentControlsBar.Children.Add(_clearRecentHistoryBtn);

        var fullRecentCard = Card(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                recentControlsBar,
                new TextBlock
                {
                    Text = "Review past attended remote sessions, connection dates, and reconnect in one click.",
                    FontSize = 12,
                    Foreground = SecondaryTextBrush,
                },
                _fullRecentSessionsList,
            },
        });
        _recentSessionsTabPanel.Children.Add(fullRecentCard);

        // --- TAB 4: DISCOVERED TAB CONTENT ---
        var localDiscoveryCard = Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Local Machine Discovery Status",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                },
                new TextBlock
                {
                    Text = $"Computer Name: {Environment.MachineName}  •  Status: Broadcasting on Local Network (Attended)",
                    FontSize = 13,
                    Foreground = SecondaryTextBrush,
                },
            },
        });

        var scanBar = new Grid
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
                    Text = "Nearby & Signaling Network Devices",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        _scanDiscoveredBtn.Height = 34;
        _scanDiscoveredBtn.Padding = new Thickness(16, 0);
        Grid.SetColumn(_scanDiscoveredBtn, 1);
        scanBar.Children.Add(_scanDiscoveredBtn);

        var discoveredCard = Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                scanBar,
                _discoveredList,
            },
        });
        _discoveredTabPanel.Children.Add(localDiscoveryCard);
        _discoveredTabPanel.Children.Add(discoveredCard);

        // --- TAB 5: INVITATIONS TAB CONTENT ---
        var securityPolicyCard = Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Attended Consent & Security Policy",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                },
                new TextBlock
                {
                    Text = "SwiftDesk operates on a strict zero-trust model. Every incoming remote session requires explicit attended approval on this computer before any screen capture or input control is granted.",
                    FontSize = 13,
                    Foreground = SecondaryTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        });

        _copyInvitationBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _copyInvitationBtn.Padding = new Thickness(20, 0);
        var inviteShareCard = Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Share Attended Session Invitation",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                },
                new TextBlock
                {
                    Text = "Quickly send your SwiftDesk ID and connection instructions to an authorized support technician or colleague.",
                    FontSize = 13,
                    Foreground = SecondaryTextBrush,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    Children = { _copyInvitationBtn, _invitationStatusText },
                },
            },
        });

        var invitationsAuditCard = Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Incoming Requests & Approval Activity",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                },
                _invitationsHistoryList,
            },
        });

        _invitationsTabPanel.Children.Add(securityPolicyCard);
        _invitationsTabPanel.Children.Add(inviteShareCard);
        _invitationsTabPanel.Children.Add(invitationsAuditCard);

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
                heroGrid,
                navigation,
                _homeTabPanel,
                _favoritesTabPanel,
                _recentSessionsTabPanel,
                _discoveredTabPanel,
                _invitationsTabPanel,
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

        _addFavButton.Click += async (_, _) =>
        {
            var id = _favAddressInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }
            await _favoritesStore.AddOrUpdateAsync(id, _favNicknameInput.Text, _shutdown.Token);
            _favAddressInput.Text = string.Empty;
            _favNicknameInput.Text = string.Empty;
            await RefreshFavoritesViewAsync();
        };

        _clearRecentHistoryBtn.Click += async (_, _) =>
        {
            await _recentSessionHistory.ClearAsync(_shutdown.Token);
            await RefreshFullRecentSessionsViewAsync();
            await RefreshRecentSessionsAsync();
        };

        _refreshRecentHistoryBtn.Click += async (_, _) =>
        {
            await RefreshFullRecentSessionsViewAsync();
            await RefreshRecentSessionsAsync();
        };

        _scanDiscoveredBtn.Click += (_, _) =>
        {
            RefreshDiscoveredView();
        };

        _copyInvitationBtn.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                var text = $"SwiftDesk Remote Support Invitation\nSwiftDesk Address: {_thisDeviceId.Text}\nMachine: {Environment.MachineName}\nReady for secure attended session.";
                await clipboard.SetTextAsync(text);
                _copyInvitationBtn.Content = "✓ Copied to Clipboard!";
                _invitationStatusText.IsVisible = true;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    Dispatcher.UIThread.Post(() =>
                    {
                        _copyInvitationBtn.Content = "Copy Invitation Details";
                        _invitationStatusText.IsVisible = false;
                    });
                });
            }
        };

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
        _favoritesStore.Dispose();
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

    private Border CreateNavigationTab(string icon, string text, DashboardTab tab)
    {
        var label = new TextBlock
        {
            Text = $"{icon}  {text}",
            FontSize = 13.5,
            FontWeight = tab == _activeTab ? FontWeight.SemiBold : FontWeight.Medium,
            Foreground = tab == _activeTab ? AccentBrush : SecondaryTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var indicator = new Border
        {
            Height = 2.5,
            CornerRadius = new CornerRadius(1.25),
            Background = tab == _activeTab ? AccentBrush : Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var container = new Border
        {
            Background = tab == _activeTab ? new SolidColorBrush(Color.Parse("#FEF3F2")) : Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = new Thickness(14, 8, 14, 6),
            Child = new StackPanel
            {
                Spacing = 2,
                Children = { label, indicator },
            },
        };

        _navTabs.Add((tab, label, indicator, container));
        container.PointerPressed += (_, _) => SwitchTab(tab);
        return container;
    }

    private void SwitchTab(DashboardTab tab)
    {
        _activeTab = tab;
        foreach (var (t, label, indicator, container) in _navTabs)
        {
            var active = t == tab;
            label.Foreground = active ? AccentBrush : SecondaryTextBrush;
            label.FontWeight = active ? FontWeight.SemiBold : FontWeight.Medium;
            indicator.Background = active ? AccentBrush : Brushes.Transparent;
            container.Background = active ? new SolidColorBrush(Color.Parse("#FEF3F2")) : Brushes.Transparent;
        }

        _homeTabPanel.IsVisible = tab == DashboardTab.Home;
        _favoritesTabPanel.IsVisible = tab == DashboardTab.Favorites;
        _recentSessionsTabPanel.IsVisible = tab == DashboardTab.RecentSessions;
        _discoveredTabPanel.IsVisible = tab == DashboardTab.Discovered;
        _invitationsTabPanel.IsVisible = tab == DashboardTab.Invitations;

        if (tab == DashboardTab.Favorites)
        {
            _ = RefreshFavoritesViewAsync();
        }
        else if (tab == DashboardTab.RecentSessions)
        {
            _ = RefreshFullRecentSessionsViewAsync();
        }
        else if (tab == DashboardTab.Discovered)
        {
            RefreshDiscoveredView();
        }
        else if (tab == DashboardTab.Invitations)
        {
            RefreshInvitationsView();
        }
    }

    private static Border ModernFeatureTile(string icon, string eyebrow, string title, string description, string accentHex, string bgTintHex, Action? onClick = null)
    {
        var iconBadge = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse(bgTintHex)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = icon,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var eyebrowBlock = new TextBlock
        {
            Text = eyebrow,
            FontSize = 10.5,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse(accentHex)),
            LetterSpacing = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var headerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { iconBadge, eyebrowBlock },
        };

        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 16),
            Cursor = onClick != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    headerRow,
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 15,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = PrimaryTextBrush,
                    },
                    new TextBlock
                    {
                        Text = description,
                        FontSize = 12,
                        Foreground = SecondaryTextBrush,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };

        if (onClick != null)
        {
            card.PointerPressed += (_, _) => onClick();
        }

        return card;
    }

    private async Task RefreshFavoritesViewAsync()
    {
        try
        {
            var favorites = await _favoritesStore.LoadAsync(_shutdown.Token);
            _favoritesList.Children.Clear();
            if (favorites.Count == 0)
            {
                _favoritesList.Children.Add(new TextBlock
                {
                    Text = "⭐ No favorite devices saved yet. Add your frequently used SwiftDesk devices above for 1-click access.",
                    Foreground = SecondaryTextBrush,
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(12, 24),
                });
                return;
            }

            foreach (var fav in favorites)
            {
                var content = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(new GridLength(140)),
                        new ColumnDefinition(new GridLength(95)),
                        new ColumnDefinition(new GridLength(85)),
                    },
                    ColumnSpacing = 12,
                };

                content.Children.Add(new TextBlock
                {
                    Text = "⭐",
                    FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                var titleStack = new StackPanel
                {
                    Spacing = 2,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = string.IsNullOrEmpty(fav.Nickname) ? FormatDeviceId(fav.DeviceId) : fav.Nickname,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = PrimaryTextBrush,
                            FontSize = 14,
                        },
                        new TextBlock
                        {
                            Text = FormatDeviceId(fav.DeviceId),
                            Foreground = SecondaryTextBrush,
                            FontSize = 12,
                            IsVisible = !string.IsNullOrEmpty(fav.Nickname),
                        },
                    },
                };
                Grid.SetColumn(titleStack, 1);
                content.Children.Add(titleStack);

                var addedText = new TextBlock
                {
                    Text = fav.AddedAt == default ? "" : fav.AddedAt.ToLocalTime().ToString("MMM dd, yyyy", CultureInfo.CurrentCulture),
                    Foreground = SecondaryTextBrush,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(addedText, 2);
                content.Children.Add(addedText);

                var connectBtn = new Button
                {
                    Content = "⚡ Connect",
                    Height = 32,
                    Padding = new Thickness(10, 0),
                    Background = AccentBrush,
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(6),
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                connectBtn.Click += (_, _) =>
                {
                    _deviceId.Text = FormatDeviceId(fav.DeviceId);
                    ConnectClickedAsync(null, new Avalonia.Interactivity.RoutedEventArgs());
                };
                Grid.SetColumn(connectBtn, 3);
                content.Children.Add(connectBtn);

                var removeBtn = new Button
                {
                    Content = "Remove",
                    Height = 32,
                    Padding = new Thickness(10, 0),
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#FECDCA")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                removeBtn.Click += async (_, _) =>
                {
                    await _favoritesStore.RemoveAsync(fav.DeviceId, _shutdown.Token);
                    await RefreshFavoritesViewAsync();
                };
                Grid.SetColumn(removeBtn, 4);
                content.Children.Add(removeBtn);

                var cardRow = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = CardBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10),
                    Child = content,
                };
                _favoritesList.Children.Add(cardRow);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private async Task RefreshFullRecentSessionsViewAsync()
    {
        try
        {
            var sessions = await _recentSessionHistory.LoadAsync(_shutdown.Token);
            _fullRecentSessionsList.Children.Clear();
            if (sessions.Count == 0)
            {
                _fullRecentSessionsList.Children.Add(new TextBlock
                {
                    Text = "No recent remote connections recorded.",
                    Foreground = SecondaryTextBrush,
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(12, 24),
                });
                return;
            }

            foreach (var session in sessions)
            {
                var content = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(new GridLength(180)),
                        new ColumnDefinition(new GridLength(120)),
                        new ColumnDefinition(new GridLength(100)),
                    },
                    ColumnSpacing = 12,
                };

                content.Children.Add(new TextBlock
                {
                    Text = FormatDeviceId(session.DeviceId),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                var createdAt = new TextBlock
                {
                    Text = session.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                    Foreground = SecondaryTextBrush,
                    FontSize = 12,
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
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(state, 2);
                content.Children.Add(state);

                var connectBtn = new Button
                {
                    Content = "⚡ Connect",
                    Height = 32,
                    Padding = new Thickness(12, 0),
                    Background = AccentBrush,
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(6),
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                connectBtn.Click += (_, _) =>
                {
                    _deviceId.Text = FormatDeviceId(session.DeviceId);
                    ConnectClickedAsync(null, new Avalonia.Interactivity.RoutedEventArgs());
                };
                Grid.SetColumn(connectBtn, 3);
                content.Children.Add(connectBtn);

                var row = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = CardBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 10),
                    Child = content,
                };
                _fullRecentSessionsList.Children.Add(row);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private void RefreshDiscoveredView()
    {
        _discoveredList.Children.Clear();

        var localEntry = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(140)),
                new ColumnDefinition(new GridLength(100)),
            },
            ColumnSpacing = 12,
        };

        localEntry.Children.Add(new TextBlock
        {
            Text = "🖥️",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var titleStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = $"{Environment.MachineName} (This Computer)",
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                    FontSize = 14,
                },
                new TextBlock
                {
                    Text = $"Local Address: {_thisDeviceId.Text}  •  Subnet Broadcast",
                    Foreground = SecondaryTextBrush,
                    FontSize = 12,
                },
            },
        };
        Grid.SetColumn(titleStack, 1);
        localEntry.Children.Add(titleStack);

        var status = new TextBlock
        {
            Text = "🟢 Active (Local)",
            Foreground = new SolidColorBrush(Color.Parse("#15803D")),
            FontWeight = FontWeight.Medium,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(status, 2);
        localEntry.Children.Add(status);

        var readyBadge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#ECFDF3")),
            BorderBrush = new SolidColorBrush(Color.Parse("#A6F4C5")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "Host Ready",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#027A48")),
            },
        };
        Grid.SetColumn(readyBadge, 3);
        localEntry.Children.Add(readyBadge);

        _discoveredList.Children.Add(new Border
        {
            Background = Brushes.White,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Child = localEntry,
        });

        _discoveredList.Children.Add(new TextBlock
        {
            Text = "SwiftDesk scans local network subnets (mDNS/UDP broadcast) and connected signaling broker peers.\nComputers running SwiftDesk with attended mode enabled will automatically appear in this list.",
            Foreground = SecondaryTextBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 12, 4, 4),
        });
    }

    private void RefreshInvitationsView()
    {
        _invitationsHistoryList.Children.Clear();

        var consentCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E2E8F0")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = "🛡️ Attended Consent Engine",
                        FontWeight = FontWeight.Bold,
                        FontSize = 14,
                        Foreground = PrimaryTextBrush,
                    },
                    new TextBlock
                    {
                        Text = "All remote sessions are attended-first. When an operator connects with your address, a modal dialog will appear asking you to approve Screen View and Input Control. No unattended access is permitted without explicit permission.",
                        Foreground = SecondaryTextBrush,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };
        _invitationsHistoryList.Children.Add(consentCard);

        var activeRequest = new Border
        {
            Background = Brushes.White,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Child = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(new GridLength(140)),
                },
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"Current Host Session: {_thisDeviceId.Text}",
                                FontWeight = FontWeight.SemiBold,
                                FontSize = 13,
                                Foreground = PrimaryTextBrush,
                            },
                            new TextBlock
                            {
                                Text = $"Computer: {Environment.MachineName}  •  Status: Listening for incoming sessions",
                                FontSize = 12,
                                Foreground = SecondaryTextBrush,
                            },
                        },
                    },
                    new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#EFF8FF")),
                        BorderBrush = new SolidColorBrush(Color.Parse("#B2DDFF")),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(8, 4),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = "Attended Ready",
                            FontSize = 11,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = new SolidColorBrush(Color.Parse("#175CD3")),
                        },
                    },
                },
            },
        };
        _invitationsHistoryList.Children.Add(activeRequest);
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
