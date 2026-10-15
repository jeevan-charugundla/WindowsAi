namespace WindowsAICompanion.Desktop;

public partial class App : System.Windows.Application
{
	private TrayIconService? _trayIconService;
	private GlobalHotkeyService? _globalHotkeyService;
	private MainWindow? _mainWindow;
	private BackendApiClient? _backendApiClient;

	protected override void OnStartup(System.Windows.StartupEventArgs e)
	{
		ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
		base.OnStartup(e);

		_backendApiClient = new BackendApiClient();
		_mainWindow = new MainWindow(_backendApiClient, new ScreenCaptureService());
		MainWindow = _mainWindow;
		_mainWindow.Show();
		_mainWindow.Hide();

		_globalHotkeyService = new GlobalHotkeyService();
		_globalHotkeyService.HotkeyPressed += OnHotkeyPressed;
		_globalHotkeyService.Register(_mainWindow);

		_trayIconService = new TrayIconService(
			OpenAssistant,
			ToggleAssistant,
			ShowSettingsPlaceholder,
			ExitApplication);
	}

	protected override void OnExit(System.Windows.ExitEventArgs e)
	{
		_trayIconService?.Dispose();

		if (_globalHotkeyService is not null)
		{
			_globalHotkeyService.HotkeyPressed -= OnHotkeyPressed;
			_globalHotkeyService.Dispose();
		}

		_backendApiClient?.Dispose();

		base.OnExit(e);
	}

	private void OnHotkeyPressed(object? sender, EventArgs e)
	{
		OpenAssistant();
	}

	private void OpenAssistant()
	{
		if (_mainWindow is null)
		{
			return;
		}

		if (_mainWindow.WindowState == System.Windows.WindowState.Minimized)
		{
			_mainWindow.WindowState = System.Windows.WindowState.Normal;
		}

		_mainWindow.Show();
		_mainWindow.Activate();
		_mainWindow.Topmost = true;
		_mainWindow.Topmost = false;
		_mainWindow.Focus();
	}

	private void ToggleAssistant()
	{
		if (_mainWindow?.IsVisible == true)
		{
			_mainWindow.Hide();
			return;
		}

		OpenAssistant();
	}

	private void ShowSettingsPlaceholder()
	{
		System.Windows.MessageBox.Show(
			"Settings are not available in this version.",
			"Windows AI Companion",
			System.Windows.MessageBoxButton.OK,
			System.Windows.MessageBoxImage.Information);
	}

	private void ExitApplication()
	{
		_mainWindow?.AllowClose();
		Shutdown();
	}
}


