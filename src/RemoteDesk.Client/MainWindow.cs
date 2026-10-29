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

namespace RemoteDesk.Client;

internal sealed class MainWindow : Window, IAsyncDisposable
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush PageBrush = new SolidColorBrush(Color.Parse("#F4F6F9"));
    private static readonly IBrush CardBrush = Brushes.White;
    private static readonly IBrush CardBorderBrush = new SolidColorBrush(Color.Parse("#E4E8EE"));
    private static readonly IBrush PrimaryTextBrush = new SolidColorBrush(Color.Parse("#17212B"));
    private static readonly IBrush SecondaryTextBrush = new SolidColorBrush(Color.Parse("#667085"));

    private readonly TextBox _server = new()
    {
        Text = Environment.GetEnvironmentVariable("REMOTEDESK_SERVER") ?? "http://localhost:5080",
    };
    private readonly TextBox _operatorKey = new()
    {
        Text = Environment.GetEnvironmentVariable("REMOTEDESK_OPERATOR_KEY") ?? "local-operator-change-me",
        PasswordChar = '●',
    };
    private readonly TextBox _operatorName = new() { Text = Environment.UserName };
    private readonly TextBox _deviceId = new()
    {
        PlaceholderText = "Enter remote device ID",
        FontSize = 18,
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
        Text = "Start the Agent to create an ID",
        FontSize = 14,
        FontWeight = FontWeight.SemiBold,
        Foreground = PrimaryTextBrush,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Button _copyDeviceId = new()
    {
        Content = "Copy ID",
        IsEnabled = false,
    };

    private readonly Image _screenImage = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
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

    private readonly Border _screenContainer;
    private readonly CancellationTokenSource _shutdown = new();
    private HttpClient? _httpClient;
    private ClientSession? _session;
    private int _disposed;

    public MainWindow()
    {
        Title = "SwiftDesk — Remote Desktop";
        Width = 1180;
        Height = 820;
        MinWidth = 920;
        MinHeight = 640;
        Background = PageBrush;

        StyleTextBox(_server);
        StyleTextBox(_operatorKey);
        StyleTextBox(_operatorName);
        StyleTextBox(_deviceId, 50);
        StyleTextBox(_filePath);
        StylePrimaryButton(_connect, 50);
        StyleSecondaryButton(_sendFile);
        _copyDeviceId.Height = 36;
        _copyDeviceId.Padding = new Thickness(12, 0);
        _copyDeviceId.Background = new SolidColorBrush(Color.Parse("#FFF4F4"));
        _copyDeviceId.Foreground = AccentBrush;
        _copyDeviceId.BorderThickness = new Thickness(0);
        _copyDeviceId.CornerRadius = new CornerRadius(8);
        _copyDeviceId.FontWeight = FontWeight.SemiBold;

        var localIdGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
        };
        localIdGrid.Children.Add(_thisDeviceId);
        Grid.SetColumn(_copyDeviceId, 1);
        localIdGrid.Children.Add(_copyDeviceId);

        var permissions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 20,
            Children = { _viewScreen, _controlInput, _transferFiles, _reboot },
        };

        var quickConnectGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(168)),
            },
            ColumnSpacing = 12,
        };
        quickConnectGrid.Children.Add(_deviceId);
        Grid.SetColumn(_connect, 1);
        quickConnectGrid.Children.Add(_connect);

        var connectionCard = Card(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Eyebrow("REMOTE DESK"),
                new TextBlock
                {
                    Text = "Connect to another device",
                    FontSize = 24,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                },
                new TextBlock
                {
                    Text = "Enter the SwiftDesk ID shown on the remote computer.",
                    Foreground = SecondaryTextBrush,
                },
                quickConnectGrid,
                Label("Session permissions"),
                permissions,
            },
        });

        var thisDeviceCard = Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Eyebrow("THIS DEVICE"),
                new TextBlock
                {
                    Text = "Your SwiftDesk ID",
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                },
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#FFF4F4")),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12),
                    Child = localIdGrid,
                },
                new TextBlock
                {
                    Text = $"Device name: {Environment.MachineName}",
                    Foreground = SecondaryTextBrush,
                    FontSize = 12,
                },
                new TextBlock
                {
                    Text = "Every incoming session still needs approval on this device.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = SecondaryTextBrush,
                    FontSize = 13,
                },
            },
        });

        var homeGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(2, GridUnitType.Star)),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 18,
        };
        homeGrid.Children.Add(connectionCard);
        Grid.SetColumn(thisDeviceCard, 1);
        homeGrid.Children.Add(thisDeviceCard);

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

        var screenGrid = new Grid();
        screenGrid.Children.Add(_screenImage);
        screenGrid.Children.Add(_screenPlaceholderText);

        _screenContainer = new Border
        {
            Height = 470,
            Background = new SolidColorBrush(Color.Parse("#111827")),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Child = screenGrid,
        };

        _screenContainer.PointerMoved += OnScreenPointerMoved;
        _screenContainer.PointerPressed += OnScreenPointerPressed;
        _screenContainer.PointerReleased += OnScreenPointerReleased;
        _screenContainer.PointerWheelChanged += OnScreenPointerWheel;
        KeyDown += OnWindowKeyDown;
        KeyUp += OnWindowKeyUp;

        var screenHeader = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        screenHeader.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = "Session workspace",
                    FontSize = 19,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                },
                new TextBlock
                {
                    Text = "Encrypted screen stream and remote input",
                    FontSize = 13,
                    Foreground = SecondaryTextBrush,
                },
            },
        });

        var statusBadge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F2F4F7")),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(12, 7),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "●",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.Parse("#12B76A")),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    _status,
                },
            },
        };
        Grid.SetColumn(statusBadge, 1);
        screenHeader.Children.Add(statusBadge);

        var screenCard = Card(new StackPanel
        {
            Spacing = 16,
            Children = { screenHeader, _screenContainer },
        });

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
            Padding = new Thickness(28, 16),
            Child = headerGrid,
        };

        var pageContent = new StackPanel
        {
            Margin = new Thickness(28),
            Spacing = 18,
            Children =
            {
                homeGrid,
                advancedSettings,
                screenCard,
                filePanel,
            },
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = pageContent });
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
        Activated += (_, _) => RefreshLocalDeviceId();
        Closed += WindowClosed;

        RefreshLocalDeviceId();
    }

    private void OnScreenPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_session is null || !_session.Permissions.HasFlag(SessionPermission.ControlInput))
        {
            return;
        }

        var (normX, normY) = GetNormalizedCoordinates(e.GetPosition(_screenImage));
        _ = _session.SendInputAsync(RemoteInputKind.PointerMove, normX, normY);
    }

    private void OnScreenPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_session is null || !_session.Permissions.HasFlag(SessionPermission.ControlInput))
        {
            return;
        }

        var (normX, normY) = GetNormalizedCoordinates(e.GetPosition(_screenImage));
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

        var (normX, normY) = GetNormalizedCoordinates(e.GetPosition(_screenImage));
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

        var (normX, normY) = GetNormalizedCoordinates(e.GetPosition(_screenImage));
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

    private (double X, double Y) GetNormalizedCoordinates(Point pos)
    {
        var width = _screenImage.Bounds.Width;
        var height = _screenImage.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return (0.5, 0.5);
        }

        return (Math.Clamp(pos.X / width, 0.0, 1.0), Math.Clamp(pos.Y / height, 0.0, 1.0));
    }

    private async void ConnectClickedAsync(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _connect.IsEnabled = false;
        _connect.Content = "Connecting…";
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
                _deviceId.Text?.Trim() ?? string.Empty,
                _operatorName.Text?.Trim() ?? string.Empty,
                requested);
            ProtocolValidation.Validate(request);

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
                _shutdown.Token);

            SetStatus($"Waiting for approval ({created.SessionId})…");
            SessionStatusResponse status;
            do
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _shutdown.Token);
                status = await api.GetSessionStatusAsync(
                    _operatorKey.Text ?? string.Empty,
                    created.SessionId,
                    _shutdown.Token);
            }
            while (status.State == SessionState.Pending);

            if (status.State is not (SessionState.Approved or SessionState.Active)
                || status.ControllerTicket is null)
            {
                throw new InvalidOperationException($"Connection ended with state {status.State}.");
            }

            var socket = await api.ConnectSessionAsync(
                created.SessionId,
                status.ControllerTicket,
                _shutdown.Token);

            _session = new ClientSession(created.SessionId, status.GrantedPermissions, socket);
            _session.OnFrameReceived += OnRemoteFrameReceived;
            _session.OnStatusMessage += SetStatus;

            await _session.StartAsync(_shutdown.Token);

            _sendFile.IsEnabled = status.GrantedPermissions.HasFlag(SessionPermission.TransferFiles);
            SetStatus($"Connected with permissions: {status.GrantedPermissions}");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            SetStatus("Closed");
        }
        catch (Exception exception)
        {
            SetStatus($"Connection failed: {exception.Message}");
        }
        finally
        {
            _connect.IsEnabled = true;
            _connect.Content = "Connect  →";
        }
    }

    private void OnRemoteFrameReceived(byte[] frameBytes)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                using var stream = new MemoryStream(frameBytes);
                var bitmap = new Bitmap(stream);
                _screenImage.Source = bitmap;
                _screenPlaceholderText.IsVisible = false;
            }
            catch
            {
                // Ignore transient frame decoding errors
            }
        });
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
            SetStatus("Start the Agent once to create this device's SwiftDesk ID.");
            return;
        }

        await clipboard.SetTextAsync(deviceId);
        SetStatus("This device's SwiftDesk ID was copied.");
    }

    private void RefreshLocalDeviceId()
    {
        var deviceId = GetLocalDeviceId();
        _thisDeviceId.Text = deviceId ?? "Start the Agent to create an ID";
        _copyDeviceId.IsEnabled = deviceId is not null;
    }

    private static string? GetLocalDeviceId()
    {
        var configuredId = Environment.GetEnvironmentVariable("REMOTEDESK_DEVICE_ID");
        if (!string.IsNullOrWhiteSpace(configuredId))
        {
            return configuredId.Trim();
        }

        return DeviceIdentityStore.Load()?.DeviceId;
    }

    private async void WindowClosed(object? sender, EventArgs eventArgs)
    {
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync();
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        _httpClient?.Dispose();
        _httpClient = null;
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
}
