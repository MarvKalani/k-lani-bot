#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using KLaniBot.Fleet;
using KLaniBot.Startup;
using KLaniBot.Transport;
using KLaniBot.Guard;
using KLaniBot.Cartridges;
using KLaniBot.Visibility;
using KLaniBot.Localization;

namespace KLaniBot.Platforms.Windows;

// The person installs and pairs the Windows node through WPF. Existing enrollment, argument boundaries and
// configuration files remain the authority; the dialog changes only presentation and input controls.
public sealed class WindowsNodeSetup : Window, IDisposable
{
    private readonly ComboBox _master = new() { IsEditable = true };
    private readonly TextBox _nodeName = new() { Text = NodeName.SuggestForThisComputer() ?? "" };
    private readonly TextBox _code = new() { Width = 160, MaxLength = 6, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _connect = new() { Content = UiTexts.Get(UiText.SetupRequestPairing) };
    private readonly Button _finish = new() { Content = UiTexts.Get(UiText.SetupConfirmCodeAndConnect), IsEnabled = false };
    private readonly TextBlock _status = WindowsWpfTheme.Text(UiTexts.Get(UiText.SetupChooseMaster));
    private readonly CheckBox _autostart = new() { Content = UiTexts.Get(UiText.SetupStartAtSignIn) };
    private readonly CancellationTokenSource _closed = new();
    private readonly string _stateDirectory;
    private PairingClient? _pairing;
    private bool _started;
    private bool _disposed;
    private bool _resourcesDisposed;
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    private WindowsNodeSetup(string stateDirectory)
    {
        _stateDirectory = stateDirectory;
        string existingConfig = Path.Combine(stateDirectory, "node.json");
        if (File.Exists(existingConfig))
        {
            CartridgeManifest.RejectLinks(existingConfig);
            _nodeName.Text = NodePolicy.Load(NodeConfiguration.Load(existingConfig).PolicyPath).NodeId;
            _nodeName.IsReadOnly = true;
        }
        Title = ProductIdentity.NodeName + " " + ProductIdentity.DisplayVersion;
        Width = 580;
        Height = 700;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowsWpfTheme.Apply(this);
        Content = SetupLayout();
        _connect.Click += BeginPairing;
        _finish.Click += FinishPairing;
        Loaded += DiscoverMasters;
        Closed += (_, _) => { _disposed = true; _closed.Cancel(); };
    }

    private Border SetupLayout()
    {
        var layout = new StackPanel();
        layout.Children.Add(WindowsWpfTheme.Logo());
        layout.Children.Add(WindowsWpfTheme.Text("k-lani-bot · Marvin Kalani · k-lani.com"));
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Get(UiText.SetupAlphaNotice)));
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Get(UiText.SetupMasterLabel)));
        layout.Children.Add(_master);
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Get(UiText.SetupNodeNameLabel)));
        layout.Children.Add(_nodeName);
        layout.Children.Add(_connect);
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Get(UiText.SetupCodeLabel)));
        layout.Children.Add(_code);
        layout.Children.Add(_finish);
        layout.Children.Add(_autostart);
        layout.Children.Add(_status);
        var scroll = new ScrollViewer { Content = layout, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        return WindowsWpfTheme.Surface(this, scroll, 24);
    }

    // Throws InvalidOperationException outside a signed-in interactive desktop.
    public static int Run()
    {
        WindowsWpfRuntime.Initialize();
        FreeConsole();
        try
        {
            if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
            {
                throw new InvalidOperationException(UiTexts.Get(UiText.SetupStartInDesktop));
            }

            return StartOrSetUp();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, ProductIdentity.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    // A paired node is started, an invitation next to the setup enrolls silently, otherwise the dialog pairs by hand.
    private static int StartOrSetUp()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string directory = Path.Combine(localApplicationData, ProductIdentity.ProductName, "nodes", "desktop");
        string config = Path.Combine(directory, "node.json");
        string paired = Path.Combine(directory, "paired-master.json");
        if (HasRunningNode())
        {
            MessageBox.Show(
                UiTexts.Get(UiText.SetupNodeAlreadyRunning),
                ProductIdentity.ProductName);
            return 1;
        }
        if (File.Exists(paired))
        {
            EnsureInstalledExecutable();
            StartResidentNode(config);
            return 0;
        }
        string invitation = Path.Combine(AppContext.BaseDirectory, "deployment-invite.json");
        if (File.Exists(invitation))
        {
            return RunEnrollmentAsync(invitation).GetAwaiter().GetResult();
        }
        using var window = new WindowsNodeSetup(directory);
        window.ShowDialog();
        return window._started ? 0 : 1;
    }

    // Throws InvalidOperationException outside the interactive session or while a node runs.
    public static async Task<int> RunEnrollmentAsync(string invitationPath)
    {
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
        {
            throw new InvalidOperationException("Managed enrollment must run in the user's interactive session, for example at logon.");
        }
        if (HasRunningNode())
        {
            throw new InvalidOperationException(
                "Stop the existing node before initial managed deployment; subsequent updates use the update channel.");
        }
        string installed = EnsureInstalledExecutable();
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string state = Path.Combine(localApplicationData, "k-lani-bot", "nodes", "desktop");
        string config = await FleetEnrollment.EnrollAsync(invitationPath, state, installed, CancellationToken.None);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key.SetValue(ProductIdentity.NodeName, "\"" + installed + "\"");
        StartResidentNode(config);
        return 0;
    }

    private async void DiscoverMasters(object? sender, RoutedEventArgs arguments)
    {
        try
        {
            string[] masters = await LanDiscovery.FindAsync(_closed.Token);
            if (_disposed)
            {
                return;
            }
            foreach (string master in masters)
            {
                _master.Items.Add(master);
            }
            if (_master.Text == "" && masters.Length > 0)
            {
                _master.SelectedIndex = 0;
            }
            _status.Text = masters.Length == 0
                ? UiTexts.Get(UiText.SetupNoMasterFound)
                : UiTexts.Get(UiText.SetupMasterFound);
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            if (!_disposed)
            {
                _status.Text = UiTexts.Get(UiText.SetupDiscoveryUnavailable);
            }
        }
    }

    // Throws ArgumentException for an invalid node name and InvalidOperationException for a node set up under another name; both end in the
    // status line.
    private async void BeginPairing(object? sender, RoutedEventArgs arguments)
    {
        _connect.IsEnabled = false;
        _master.IsEnabled = false;
        _nodeName.IsEnabled = false;
        try
        {
            NodePolicy policy = await EnsureNodeAsync(_nodeName.Text.Trim());
            _pairing = new PairingClient(_master.Text.Trim());
            _status.Text = UiTexts.Get(UiText.SetupRequestingPairing);
            await _pairing.BeginAsync(policy.NodeId, _closed.Token);
            _code.IsEnabled = _pairing.CodeDigits > 0;
            _code.MaxLength = Math.Max(1, _pairing.CodeDigits);
            _finish.IsEnabled = true;
            _status.Text = _pairing.CodeDigits == 0
                ? UiTexts.Get(UiText.SetupCodeDisabledAtMaster)
                : UiTexts.Format(UiText.SetupEnterCode, _pairing.CodeDigits);
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _status.Text = UiTexts.Format(UiText.SetupPairingFailed, exception.Message);
            }
        }
    }

    // Creates the local node on first use; an existing one must already carry the entered name.
    // Throws ArgumentException for an invalid node name and InvalidOperationException for a node set up under another name.
    private async Task<NodePolicy> EnsureNodeAsync(string nodeName)
    {
        if (!NodeName.IsValid(nodeName))
        {
            throw new ArgumentException(UiTexts.Get(UiText.SetupNodeNameRule));
        }
        string config = Path.Combine(_stateDirectory, "node.json");
        string installedExecutable = EnsureInstalledExecutable();
        if (!File.Exists(config))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string workspace = Path.Combine(userProfile, "k-lani-bot-work", "desktop");
            await NodeInitializer.InitializeAsync([
                "--workspace", workspace, "--state", _stateDirectory,
                "--node", nodeName, "--bind", "127.0.0.1",
                "--worker-program", installedExecutable,
                // The guarded profile reaches only applications that rules name, and this dialog collects no
                // rules; the desktop profile keeps consent and the indicator. The operator narrows it in policy.json.
                "--scopes", "observe,ui,inventory.read", "--ui-guard", "desktop", "--consent-mode", "ask"
            ]);
        }
        NodePolicy policy = NodePolicy.Load(NodeConfiguration.Load(config).PolicyPath);
        if (policy.NodeId != nodeName)
        {
            throw new InvalidOperationException(UiTexts.Get(UiText.SetupNodeNamedDifferently));
        }
        return policy;
    }

    private async void FinishPairing(object? sender, RoutedEventArgs arguments)
    {
        _finish.IsEnabled = false;
        _code.IsEnabled = false;
        _status.Text = UiTexts.Get(UiText.SetupWaitingForMaster);
        try
        {
            await _pairing!.FinishAsync(_code.Text.Trim(), _stateDirectory, _closed.Token);
            string config = Path.Combine(_stateDirectory, "node.json");
            if (_autostart.IsChecked == true)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                key.SetValue(ProductIdentity.NodeName, "\"" + InstalledExecutable() + "\"");
            }
            StartResidentNode(config);
            _started = true;
            Close();
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _status.Text = UiTexts.Format(UiText.SetupNoConnection, exception.Message);
            }
        }
    }

    private static string InstalledExecutable()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", ProductIdentity.NodeName, ProductIdentity.NodeName + ".exe");
    }

    private static string EnsureInstalledExecutable()
    {
        string destination = InstalledExecutable();
        PairingClient.CreatePrivateDirectory(Path.GetDirectoryName(destination)!);
        CartridgeManifest.RejectLinks(destination);
        if (!Path.GetFullPath(Environment.ProcessPath!).Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(Environment.ProcessPath!, destination, true);
        }
        return destination;
    }

    // Throws IOException when the node does not start.
    private static void StartResidentNode(string config)
    {
        var start = new ProcessStartInfo
        {
            FileName = InstalledExecutable(),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--connect");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(config);
        // The bundle host needs its extraction path before managed Main can run.
        string directory = Path.GetDirectoryName(config)!;
        WorkerEnvironment.Configure(start, directory, WorkerPlatform.Windows);
        using Process process = Process.Start(start) ?? throw new IOException(UiTexts.Get(UiText.SetupNodeDidNotStart));
    }

    private static bool HasRunningNode()
    {
        foreach (Process process in Process.GetProcessesByName(ProductIdentity.NodeName))
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId)
                {
                    return true;
                }
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_resourcesDisposed)
        {
            return;
        }
        _resourcesDisposed = true;
        if (!_disposed)
        {
            Close();
        }
        _closed.Cancel();
        _pairing?.Dispose();
        _closed.Dispose();
    }
}
#endif
