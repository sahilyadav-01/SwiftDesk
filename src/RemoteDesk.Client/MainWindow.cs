using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RemoteDesk.Core;
using RemoteDesk.Protocol;
using RemoteDesk.Transport;

namespace RemoteDesk.Client;

internal sealed class MainWindow : Window, IAsyncDisposable
{
    private readonly TextBox _server = new() { Text = "http://localhost:5080" };
    private readonly TextBox _operatorKey = new() { Text = "local-operator-change-me", PasswordChar = '●' };
    private readonly TextBox _operatorName = new() { Text = Environment.UserName };
    private readonly TextBox _deviceId = new() { PlaceholderText = "dev_..." };
    private readonly CheckBox _viewScreen = new() { Content = "View screen", IsChecked = true };
    private readonly CheckBox _controlInput = new() { Content = "Control input" };
    private readonly CheckBox _transferFiles = new() { Content = "Transfer files" };
    private readonly CheckBox _reboot = new() { Content = "Reboot device" };
    private readonly Button _connect = new() { Content = "Request connection", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = new() { Text = "Not connected", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _filePath = new() { PlaceholderText = "Absolute path to a file" };
    private readonly Button _sendFile = new() { Content = "Send file", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CancellationTokenSource _shutdown = new();
    private HttpClient? _httpClient;
    private ClientSession? _session;
    private int _disposed;

    public MainWindow()
    {
        Title = "RemoteDesk";
        Width = 960;
        Height = 720;
        MinWidth = 720;
        MinHeight = 560;

        var permissions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children = { _viewScreen, _controlInput, _transferFiles, _reboot },
        };

        var connectionPanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Label("Signaling server"), _server,
                Label("Operator key"), _operatorKey,
                Label("Operator name"), _operatorName,
                Label("Target device ID"), _deviceId,
                Label("Requested permissions"), permissions,
                _connect,
                _status,
            },
        };

        var screenPlaceholder = new Border
        {
            Height = 260,
            Background = new SolidColorBrush(Color.Parse("#10141D")),
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = "Screen stream placeholder\nNative capture adapters are intentionally not enabled in this foundation.",
                Foreground = Brushes.LightGray,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var filePanel = new StackPanel
        {
            Spacing = 8,
            Children = { Label("File transfer"), _filePath, _sendFile },
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = "RemoteDesk",
                        FontSize = 28,
                        FontWeight = FontWeight.SemiBold,
                    },
                    connectionPanel,
                    screenPlaceholder,
                    filePanel,
                },
            },
        };

        _controlInput.IsCheckedChanged += (_, _) =>
        {
            if (_controlInput.IsChecked == true)
            {
                _viewScreen.IsChecked = true;
            }
        };
        _connect.Click += ConnectClickedAsync;
        _sendFile.Click += SendFileClickedAsync;
        Closed += WindowClosed;
    }

    private async void ConnectClickedAsync(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _connect.IsEnabled = false;
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
            await _session.AnnounceAsync(_shutdown.Token);
            _sendFile.IsEnabled = status.GrantedPermissions.HasFlag(SessionPermission.TransferFiles);
            SetStatus($"Connected with: {status.GrantedPermissions}");
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

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
    };
}

