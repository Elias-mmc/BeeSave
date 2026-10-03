using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MinecraftSaves;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly BackupService _backupService = new(AppPaths.RepositoryDirectory);
    private readonly Icon _applicationIcon;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly HashSet<string> _pendingWorldPrompts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _backedUpWorlds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _incompleteBackups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource?> _cloudWorldIcons =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Media.Animation.Storyboard _statusPulse;
    private AppSettings _settings;
    private GameMonitor? _monitor;
    private bool _allowExit;
    private bool _pollInProgress;
    private bool _restoringStartupSetting;

    public MainWindow()
    {
        InitializeComponent();
        _applicationIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath
            ?? throw new InvalidOperationException("No se encontró el ejecutable de Minecraft Saves."))
            ?? throw new InvalidOperationException("No se pudo cargar el icono de Minecraft Saves.");
        _trayIcon = CreateTrayIcon();
        _statusPulse = CreateStatusPulse();

        try
        {
            _settings = SettingsStore.Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _settings = new AppSettings();
            MessageBox.Show(this, exception.Message, "Error de configuración",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        _settings.TrackedWorlds.RemoveAll(WorldFileTree.IsInternalStagingDirectory);
        RepositoryNameTextBox.Text = _settings.RepositoryName;
        StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        StartWithWindowsCheckBox.Checked += StartWithWindowsCheckBox_Changed;
        StartWithWindowsCheckBox.Unchecked += StartWithWindowsCheckBox_Changed;
        RestartMonitor();
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();
        RefreshWorlds();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(
            0, 1, TimeSpan.FromMilliseconds(280)));

        try
        {
            StartupRegistration.SetEnabled(_settings.StartWithWindows);
            SaveSettings();
        }
        catch (Exception exception)
        {
            SetStatus("No se pudo configurar el inicio automático.", exception.Message, Colors.IndianRed);
            MessageBox.Show(this, exception.Message, "Inicio automático",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (Environment.GetCommandLineArgs().Any(argument =>
                argument.Equals("--background", StringComparison.OrdinalIgnoreCase)))
        {
            Hide();
        }

        await RefreshGitHubStatusAsync();
        if (Directory.Exists(Path.Combine(AppPaths.RepositoryDirectory, ".git")))
        {
            await RefreshBackedUpWorldsAsync();
        }
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_pollInProgress)
        {
            return;
        }

        _pollInProgress = true;
        try
        {
            var monitor = _monitor;
            if (monitor is not null)
            {
                await Task.Run(monitor.Poll);
            }

            RefreshWorlds();
        }
        catch (Exception exception)
        {
            SetStatus("No se pudo revisar la partida.", exception.Message, Colors.IndianRed);
        }
        finally
        {
            _pollInProgress = false;
        }
    }

    private void RestartMonitor()
    {
        _monitor?.Dispose();
        _monitor = new GameMonitor(_settings.SavesDirectory, _settings.TrackedWorlds);
        _monitor.NewWorldDetected += Monitor_NewWorldDetected;
        _monitor.GameSessionEnded += Monitor_GameSessionEnded;

        if (Directory.Exists(_settings.SavesDirectory))
        {
            SettingsPathText.Text = $"Tus mundos viven en: {_settings.SavesDirectory}";
            SetStatus("Tu mundo está protegido",
                "Te avisaremos cuando haya un mundo nuevo o una partida cambie.", Colors.LightGreen);
        }
        else
        {
            SettingsPathText.Text = $"No encontramos esta carpeta: {_settings.SavesDirectory}";
            SetStatus("No se encuentra la carpeta de mundos.",
                "Selecciona la carpeta «saves» de Minecraft Java para activar la detección.", Colors.Goldenrod);
        }
    }

    private void Monitor_NewWorldDetected(object? sender, string worldName)
    {
        Dispatcher.BeginInvoke(new Action(() => AskToTrackWorld(worldName)));
    }

    private void Monitor_GameSessionEnded(object? sender, IReadOnlyList<string> changedWorlds)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            foreach (var worldName in changedWorlds)
            {
                var answer = MessageBox.Show(this,
                    $"Minecraft se cerró. ¿Quieres guardar el progreso de «{worldName}» en GitHub?",
                    "Guardar progreso",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    await SaveWorldAndReportAsync(worldName);
                }
            }
        }));
    }

    private void AskToTrackWorld(string worldName)
    {
        if (!_pendingWorldPrompts.Add(worldName))
        {
            return;
        }

        try
        {
            var answer = MessageBox.Show(this,
                $"Se detectó el mundo nuevo «{worldName}». ¿Quieres respaldarlo y guardar automáticamente sus próximas partidas?",
                "Nuevo mundo detectado",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                if (!_settings.TrackedWorlds.Contains(worldName, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.TrackedWorlds.Add(worldName);
                }

                _monitor?.TrackWorld(worldName, needsInitialBackup: true, waitForFirstSessionToClose: true);
                SaveSettings();
                RefreshWorlds();
                SetStatus($"«{worldName}» quedó añadido",
                    "La primera copia se guardará al cerrar ese mundo por primera vez.", Colors.Goldenrod);
            }
            else
            {
                _monitor?.IgnoreWorld(worldName);
            }
        }
        finally
        {
            _pendingWorldPrompts.Remove(worldName);
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            await _backupService.LoginAsync();
            await RefreshGitHubStatusAsync();
            if (Directory.Exists(Path.Combine(AppPaths.RepositoryDirectory, ".git")))
            {
                await RefreshBackedUpWorldsAsync();
            }
        }, "Esperando la autenticación de GitHub...");
    }

    private async void CreateRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.RepositoryName = RepositoryNameTextBox.Text.Trim();
        SaveSettings();
        await RunUiActionAsync(async () =>
        {
            await _backupService.CreatePrivateRepositoryAsync(_settings.RepositoryName);
            await RefreshGitHubStatusAsync();
            await RefreshBackedUpWorldsAsync();
            SetStatus("¡Tu rincón privado está listo!",
                $"Los respaldos se guardarán en GitHub como «{_settings.RepositoryName}».", Colors.LightGreen);
        }, "Creando el repositorio privado...");
    }

    private async void AddWorldButton_Click(object sender, RoutedEventArgs e)
    {
        if (WorldsListBox.SelectedItem is not WorldEntry selected)
        {
            return;
        }

        var isNewlyTracked = !_settings.TrackedWorlds.Contains(selected.Name, StringComparer.OrdinalIgnoreCase);
        if (isNewlyTracked)
        {
            _settings.TrackedWorlds.Add(selected.Name);
            var worldIsOpen = _monitor?.TrackWorld(selected.Name, needsInitialBackup: true) ?? false;
            SaveSettings();
            RefreshWorlds();
            if (worldIsOpen)
            {
                SetStatus($"«{selected.Name}» quedó añadido",
                    "La primera copia se guardará cuando cierres ese mundo.", Colors.Goldenrod);
                return;
            }
        }

        await SaveWorldAndReportAsync(selected.Name);
    }

    private async void RefreshCloudButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshBackedUpWorldsAsync();
    }

    private async void RestoreWorldButton_Click(object sender, RoutedEventArgs e)
    {
        if (CloudWorldsListBox.SelectedItem is not CloudWorldEntry selected)
        {
            return;
        }

        if (Directory.Exists(Path.Combine(_settings.SavesDirectory, selected.Name)))
        {
            var answer = MessageBox.Show(this,
                $"«{selected.Name}» ya está en este ordenador. Traerás la copia como otro mundo («{selected.Name} (restaurado 1)») y conservaremos tu partida actual. ¿Seguimos?",
                "Cuidaremos las dos partidas",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }
        else
        {
            var answer = MessageBox.Show(this,
                $"Vamos a descargar «{selected.Name}» desde tu nube privada. La partida aparecerá en tu lista de mundos de Minecraft. ¿Seguimos?",
                "Traer mi mundo",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        await RunUiActionAsync(async () =>
        {
            var restoredName = await _backupService.RestoreWorldAsync(
                selected.Name,
                _settings.SavesDirectory,
                worldName => _monitor?.TrackWorld(worldName));
            if (!_settings.TrackedWorlds.Contains(restoredName, StringComparer.OrdinalIgnoreCase))
            {
                _settings.TrackedWorlds.Add(restoredName);
                _monitor?.TrackWorld(restoredName);
                SaveSettings();
            }

            RefreshWorlds();
            await RefreshBackedUpWorldsAsync();
            SetStatus("¡Mundo de vuelta a casa!",
                $"«{restoredName}» ya aparece en tus mundos de Minecraft.", Colors.LightGreen);
            _trayIcon.ShowBalloonTip(3500, "Mundo restaurado",
                $"«{restoredName}» está listo para jugar.",
                Forms.ToolTipIcon.Info);
        }, $"Buscando «{selected.Name}» en tu nube...");
    }

    private async void DeleteCloudWorldButton_Click(object sender, RoutedEventArgs e)
    {
        var worldName = (sender as FrameworkElement)?.Tag as string ??
                        (CloudWorldsListBox.SelectedItem as CloudWorldEntry)?.Name;
        if (string.IsNullOrWhiteSpace(worldName))
        {
            return;
        }

        await ConfirmAndDeleteCloudWorldAsync(worldName);
    }

    private async Task ConfirmAndDeleteCloudWorldAsync(string worldName)
    {
        var confirmation = MessageBox.Show(this,
            $"¿Quitar «{worldName}» de la copia actual de la nube? No se borrará la partida de este ordenador. El historial anterior seguirá en GitHub. Se desactivarán los respaldos automáticos de este mundo hasta que lo guardes de nuevo.",
            "Quitar de la nube",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            await _backupService.DeleteCloudWorldAsync(worldName);
            _settings.TrackedWorlds.RemoveAll(
                name => name.Equals(worldName, StringComparison.OrdinalIgnoreCase));
            _monitor?.UntrackWorld(worldName);
            _backedUpWorlds.Remove(worldName);
            _incompleteBackups.Remove(worldName);
            _cloudWorldIcons.Remove(worldName);
            SaveSettings();
            RefreshWorlds();
            await RefreshBackedUpWorldsAsync();
            SetStatus("Copia quitada de la nube",
                $"«{worldName}» sigue intacto en el ordenador. Su historial anterior permanece en GitHub.",
                Colors.LightGreen);
        }, $"Quitando «{worldName}» de la copia actual...");
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Selecciona la carpeta «saves» de Minecraft Java",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_settings.SavesDirectory)
                ? _settings.SavesDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        _settings.SavesDirectory = dialog.SelectedPath;
        SaveSettings();
        RestartMonitor();
        RefreshWorlds();
    }

    private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_restoringStartupSetting)
        {
            return;
        }

        var enabled = StartWithWindowsCheckBox.IsChecked == true;
        var previousValue = _settings.StartWithWindows;
        try
        {
            StartupRegistration.SetEnabled(enabled);
            _settings.StartWithWindows = enabled;
            SaveSettings();
            SetStatus(enabled ? "Inicio automático activado" : "Inicio automático desactivado",
                enabled ? "El monitor se iniciará minimizado al entrar a Windows." : "Abre la aplicación antes de jugar para monitorizar tus mundos.",
                enabled ? Colors.LightGreen : Colors.Goldenrod);
        }
        catch (Exception exception)
        {
            _settings.StartWithWindows = previousValue;
            _restoringStartupSetting = true;
            StartWithWindowsCheckBox.IsChecked = previousValue;
            _restoringStartupSetting = false;

            var message = exception.Message;
            try
            {
                StartupRegistration.SetEnabled(previousValue);
            }
            catch (Exception rollbackException)
            {
                message += Environment.NewLine + "Tampoco se pudo restaurar el valor anterior: " +
                           rollbackException.Message;
            }

            SetStatus("No se pudo cambiar el inicio automático.", message, Colors.IndianRed);
            MessageBox.Show(this, message, "Inicio automático",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WorldsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorldsListBox.SelectedItem is not WorldEntry selected)
        {
            WorldDetailsText.Text = "Elige un mundo para guardarlo en tu nube privada.";
            AddWorldButton.IsEnabled = false;
            return;
        }

        var isTracked = _settings.TrackedWorlds.Contains(selected.Name, StringComparer.OrdinalIgnoreCase);
        WorldDetailsText.Text = !isTracked
            ? $"«{selected.Name}» es solo tuyo por ahora; guárdalo para tener una copia en la nube."
            : _incompleteBackups.Contains(selected.Name)
                ? $"La copia antigua de «{selected.Name}» está incompleta. Tu partida del ordenador está bien; pulsa Guardar para crear una copia recuperable."
                : _backedUpWorlds.Contains(selected.Name)
                    ? $"«{selected.Name}» se guarda automáticamente cuando terminas de jugar."
                    : $"«{selected.Name}» está preparado para guardarse al terminar la primera partida.";
        AddWorldButton.IsEnabled = true;
        AddWorldButton.Content = isTracked ? "☁  Actualizar copia ahora" : "☁  Guardar este mundo";
    }

    private void CloudWorldsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CloudWorldsListBox.SelectedItem is not CloudWorldEntry selected)
        {
            CloudWorldDetailsText.Text =
                "Conecta GitHub y tus copias te esperan aquí, incluso si borras un mundo del ordenador.";
            RestoreWorldButton.IsEnabled = false;
            DeleteCloudWorldButton.IsEnabled = false;
            return;
        }

        var localExists = Directory.Exists(Path.Combine(_settings.SavesDirectory, selected.Name));
        CloudWorldDetailsText.Text = selected.IsRestorable
            ? localExists
            ? "Hay una copia en el ordenador y otra en la nube. Al traerla, conservaremos las dos."
            : $"Encontramos «{selected.Name}» en tu nube. Puedes traerlo otra vez a Minecraft."
            : $"La copia antigua de «{selected.Name}» no contiene los archivos del mundo. Puedes quitar esa referencia de la copia actual; el historial se conserva.";
        RestoreWorldButton.IsEnabled = selected.IsRestorable;
        DeleteCloudWorldButton.IsEnabled = true;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _pollTimer.Stop();
        _monitor?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _applicationIcon.Dispose();
    }

    private async Task SaveWorldAndReportAsync(string worldName)
    {
        var source = Path.Combine(_settings.SavesDirectory, worldName);
        await RunUiActionAsync(async () =>
        {
            await _backupService.SaveWorldAsync(source, worldName, DateTime.Now);
            _monitor?.MarkWorldBackedUp(worldName);
            SetStatus("Respaldo guardado",
                $"«{worldName}» se confirmó y subió a GitHub.", Colors.LightGreen);
            await RefreshBackedUpWorldsAsync();
            _trayIcon.ShowBalloonTip(3500, "Minecraft Saves", $"Se guardó «{worldName}» en GitHub.",
                Forms.ToolTipIcon.Info);
        }, $"Guardando «{worldName}»...");
    }

    private async Task RunUiActionAsync(Func<Task> action, string progressMessage)
    {
        LoginButton.IsEnabled = false;
        CreateRepositoryButton.IsEnabled = false;
        AddWorldButton.IsEnabled = false;
        RestoreWorldButton.IsEnabled = false;
        RefreshCloudButton.IsEnabled = false;
        SetStatus(progressMessage, "La operación puede tardar según el tamaño del mundo.", Colors.Goldenrod);
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            SetStatus("La operación no se completó.", exception.Message, Colors.IndianRed);
            MessageBox.Show(this, exception.Message, "Minecraft Saves",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LoginButton.IsEnabled = true;
            CreateRepositoryButton.IsEnabled = true;
            AddWorldButton.IsEnabled = WorldsListBox.SelectedItem is WorldEntry;
            RestoreWorldButton.IsEnabled = CloudWorldsListBox.SelectedItem is CloudWorldEntry;
            DeleteCloudWorldButton.IsEnabled = CloudWorldsListBox.SelectedItem is CloudWorldEntry;
            RefreshCloudButton.IsEnabled = true;
        }
    }

    private async Task RefreshGitHubStatusAsync()
    {
        try
        {
            GitHubStatusText.Text = await _backupService.IsGitHubAuthenticatedAsync()
                ? "Sesión de GitHub iniciada."
                : "Inicia sesión antes de crear el repositorio privado.";
        }
        catch (InvalidOperationException exception)
        {
            GitHubStatusText.Text = exception.Message;
        }
    }

    private void RefreshWorlds()
    {
        if (!Directory.Exists(_settings.SavesDirectory))
        {
            WorldsListBox.ItemsSource = Array.Empty<WorldEntry>();
            return;
        }

        var selectedName = (WorldsListBox.SelectedItem as WorldEntry)?.Name;
        var entries = Directory.EnumerateDirectories(_settings.SavesDirectory)
            .Where(directory =>
            {
                var directoryName = Path.GetFileName(directory);
                return !directoryName.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
                       !WorldFileTree.IsInternalStagingDirectory(directoryName);
            })
            .Select(directory =>
            {
                var worldName = Path.GetFileName(directory);
                var isTracked = _settings.TrackedWorlds.Contains(
                    worldName, StringComparer.OrdinalIgnoreCase);
                return new WorldEntry(
                    worldName,
                    isTracked,
                    _backedUpWorlds.Contains(worldName),
                    _incompleteBackups.Contains(worldName),
                    LoadIcon(BackupService.ReadLocalWorldIcon(directory)));
            })
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        WorldsListBox.ItemsSource = entries;
        LocalCountText.Text = entries.Length switch
        {
            0 => "Aún no hay mundos en esta carpeta.",
            1 => "Hay 1 mundo en este ordenador.",
            _ => $"Hay {entries.Length} mundos en este ordenador."
        };

        if (selectedName is not null)
        {
            WorldsListBox.SelectedItem = entries.FirstOrDefault(entry => entry.Name == selectedName);
        }
    }

    private void SaveSettings()
    {
        SettingsStore.Save(_settings);
    }

    private void SetStatus(string title, string details, System.Windows.Media.Color color)
    {
        StatusText.Text = title;
        StatusDetails.Text = details;
        StatusIndicator.Fill = new SolidColorBrush(color);
        StatusHalo.Fill = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(75, color.R, color.G, color.B));
        _statusPulse.Begin(this, isControllable: true);
    }

    private async Task RefreshBackedUpWorldsAsync()
    {
        RefreshCloudButton.IsEnabled = false;
        CloudCountText.Text = "Buscando tus mundos guardados...";
        try
        {
            var selectedName = (CloudWorldsListBox.SelectedItem as CloudWorldEntry)?.Name;
            var catalog = await _backupService.ListBackedUpWorldsAsync();
            _backedUpWorlds.Clear();
            _backedUpWorlds.UnionWith(catalog.RestorableWorlds);
            _incompleteBackups.Clear();
            _incompleteBackups.UnionWith(catalog.IncompleteWorlds);
            _cloudWorldIcons.Clear();
            foreach (var (worldName, iconBytes) in catalog.Icons)
            {
                _cloudWorldIcons[worldName] = LoadIcon(iconBytes);
            }
            var localWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_settings.SavesDirectory))
            {
                foreach (var directory in Directory.EnumerateDirectories(_settings.SavesDirectory))
                {
                    var worldName = Path.GetFileName(directory);
                    if (!string.IsNullOrEmpty(worldName))
                    {
                        localWorlds.Add(worldName);
                    }
                }
            }
            var entries = catalog.RestorableWorlds
                .Select(world => new CloudWorldEntry(
                    world,
                    localWorlds.Contains(world) ? "EN PC" : "EN LA NUBE",
                    IsRestorable: true,
                    _cloudWorldIcons.GetValueOrDefault(world)))
                .Concat(catalog.IncompleteWorlds.Select(world => new CloudWorldEntry(
                    world,
                    "INCOMPLETO",
                    IsRestorable: false,
                    Icon: null)))
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            CloudWorldsListBox.ItemsSource = entries;
            CloudCountText.Text = entries.Length switch
            {
                0 => "Aún no hay mundos guardados en tu nube.",
                1 => "Hay 1 mundo esperándote en tu nube privada.",
                _ => $"Hay {catalog.RestorableWorlds.Count} copias recuperables y {catalog.IncompleteWorlds.Count} referencias antiguas."
            };

            if (selectedName is not null)
            {
                CloudWorldsListBox.SelectedItem =
                    entries.FirstOrDefault(entry => entry.Name == selectedName);
            }

            if (entries.Length == 0)
            {
                CloudWorldDetailsText.Text =
                    "Cuando guardes tu primer mundo, aparecerá aquí para que puedas recuperarlo cuando quieras.";
            }
            else if (catalog.IncompleteWorlds.Count > 0)
            {
                CloudWorldDetailsText.Text =
                    "También encontramos copias antiguas incompletas. En «En este ordenador» puedes actualizarlas desde tu partida local.";
            }

            RefreshWorlds();
        }
        catch (InvalidOperationException exception)
        {
            CloudWorldsListBox.ItemsSource = Array.Empty<CloudWorldEntry>();
            CloudCountText.Text = "Conecta GitHub para ver tus mundos.";
            CloudWorldDetailsText.Text = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CloudCountText.Text = "No pudimos leer los mundos guardados.";
            CloudWorldDetailsText.Text = exception.Message;
            SetStatus("No se pudieron buscar tus copias.", exception.Message, Colors.IndianRed);
        }
        finally
        {
            RefreshCloudButton.IsEnabled = true;
        }
    }

    private System.Windows.Media.Animation.Storyboard CreateStatusPulse()
    {
        var pulse = new System.Windows.Media.Animation.Storyboard { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
        var animation = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1,
            To = 1.18,
            Duration = TimeSpan.FromMilliseconds(950),
            AutoReverse = true
        };
        System.Windows.Media.Animation.Storyboard.SetTarget(animation, StatusHalo);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(
            animation,
            new System.Windows.PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        pulse.Children.Add(animation);

        var verticalAnimation = animation.Clone();
        System.Windows.Media.Animation.Storyboard.SetTarget(verticalAnimation, StatusHalo);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(
            verticalAnimation,
            new System.Windows.PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
        pulse.Children.Add(verticalAnimation);
        return pulse;
    }

    private void HideToTray()
    {
        Hide();
        _trayIcon.ShowBalloonTip(1800, "Minecraft Saves",
            "La detección sigue activa mientras la aplicación permanezca en la bandeja.",
            Forms.ToolTipIcon.Info);
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Minecraft Saves", null, (_, _) =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
        menu.Items.Add("Salir", null, (_, _) =>
        {
            _allowExit = true;
            Close();
        });

        var icon = new Forms.NotifyIcon
        {
            Text = "Minecraft Saves - monitorizando",
            Icon = _applicationIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        };
        return icon;
    }

    private static ImageSource? LoadIcon(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (
            exception is FileFormatException or NotSupportedException or InvalidDataException)
        {
            return null;
        }
    }

    private sealed record WorldEntry(
        string Name,
        bool IsTracked,
        bool HasBackup,
        bool HasIncompleteBackup,
        ImageSource? Icon)
    {
        public string Status => !IsTracked
            ? "Solo en este ordenador"
            : HasIncompleteBackup
                ? "La copia antigua necesita actualizarse"
                : HasBackup
                    ? "Se guarda al terminar de jugar"
                    : "Lista para la primera copia";

        public string Badge => !IsTracked
            ? "SOLO AQUÍ"
            : HasIncompleteBackup
                ? "ACTUALIZAR"
                : HasBackup
                    ? "EN NUBE"
                    : "POR GUARDAR";
    }

    private sealed record CloudWorldEntry(string Name, string Badge, bool IsRestorable, ImageSource? Icon)
    {
        public string Status => IsRestorable
            ? "Copia recuperable guardada en GitHub"
            : "Referencia antigua, sin archivos del mundo";
    }
}
