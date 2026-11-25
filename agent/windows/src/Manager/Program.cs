using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic;
using System.Text;
using System.Runtime.InteropServices;
using ProjectR.Agent.Common;

namespace ProjectR.Agent.Manager
{
    public class ManagerApp : Application
    {
        [STAThread]
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try
                {
                    string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProjectR", "Agent");
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(Path.Combine(logDir, "mgr_err.txt"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] AppDomain UnhandledException: " + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "null") + Environment.NewLine);
                }
                catch { }
            };

            ManagerApp app = new ManagerApp();
            app.DispatcherUnhandledException += delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                try
                {
                    string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProjectR", "Agent");
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(Path.Combine(logDir, "mgr_err.txt"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] DispatcherUnhandledException: " + (e.Exception != null ? e.Exception.ToString() : "null") + Environment.NewLine);
                }
                catch { }
                e.Handled = true; // Prevent crash!
            };

            bool isWatcher = false;
            bool isExplicitLock = false;
            bool isRemoteSession = false;
            string remoteAdmin = "Admin IT";
            string remoteRole = "Administrator";
            int remoteTimeout = 120;

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (a.Equals("--remote-session", StringComparison.OrdinalIgnoreCase) || a.Equals("-remote", StringComparison.OrdinalIgnoreCase))
                        isRemoteSession = true;
                    else if (a.Equals("--admin", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        remoteAdmin = args[++i];
                    else if (a.Equals("--role", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        remoteRole = args[++i];
                    else if (a.Equals("--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        int.TryParse(args[++i], out remoteTimeout);
                    else if (a.Equals("--watcher", StringComparison.OrdinalIgnoreCase) || a.Equals("-w", StringComparison.OrdinalIgnoreCase))
                        isWatcher = true;
                    else if (a.Equals("--lock", StringComparison.OrdinalIgnoreCase) || a.Equals("-lock", StringComparison.OrdinalIgnoreCase))
                        isExplicitLock = true;
                }
            }

            if (isRemoteSession)
            {
                app.Run(new RemoteSessionWindow(remoteAdmin, remoteRole, remoteTimeout));
                return;
            }

            if (isWatcher)
            {
                // Watcher MUST run in an interactive user session (Session > 0)
                int currentSessionId = Process.GetCurrentProcess().SessionId;
                if (currentSessionId == 0)
                {
                    return;
                }

                bool hasHandle = false;
                System.Threading.Mutex mutex = null;
                try
                {
                    mutex = new System.Threading.Mutex(true, "Local\\ProjectR_Manager_Watcher_Mutex", out hasHandle);
                    if (!hasHandle)
                    {
                        try
                        {
                            hasHandle = mutex.WaitOne(0, false);
                        }
                        catch (System.Threading.AbandonedMutexException)
                        {
                            hasHandle = true;
                        }
                    }
                }
                catch { }

                if (!hasHandle)
                {
                    try
                    {
                        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProjectR", "Agent");
                        File.AppendAllText(Path.Combine(logDir, "mgr_err.txt"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] Watcher exiting: another instance holds mutex." + Environment.NewLine);
                    }
                    catch { }
                    return;
                }

                try
                {
                    string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProjectR", "Agent");
                    File.AppendAllText(Path.Combine(logDir, "mgr_err.txt"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] Watcher starting in Session " + currentSessionId + "..." + Environment.NewLine);
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    LockWatcherContext watcher = new LockWatcherContext();
                    app.Run();
                }
                catch (Exception ex)
                {
                    try
                    {
                        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProjectR", "Agent");
                        File.AppendAllText(Path.Combine(logDir, "mgr_err.txt"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] Watcher Exception: " + ex.ToString() + Environment.NewLine);
                    }
                    catch { }
                }
                finally
                {
                    if (mutex != null)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                        mutex.Dispose();
                    }
                }
                return;
            }

            LockState lockState = AgentConfig.LoadLockState();
            if (lockState.IsLocked || isExplicitLock)
            {
                app.Run(new LockWindow(lockState.UnlockPassword));
                return;
            }

            app.Run(new ManagerWindow());
        }
    }

    public class ManagerWindow : Window
    {
        private Grid _mainContainer;
        private Border _statusBanner;
        private TextBlock _statusPill;
        private TextBlock _headerSubtitle;
        private TextBlock _statusBarText;
        private System.Windows.Threading.DispatcherTimer _statusTimer;
        private bool _isCheckingStatus = false;

        // Controls for Installed Mode
        private TextBox _txtAssetTag;
        private Button _btnSaveTag;
        private Button _btnForceDeploy;
        private ProgressBar _deployProgressBar;
        private TextBlock _deployProgressText;
        private Button _btnUninstallAgent;

        // Controls for Fresh Setup Mode
        private TextBox _txtSetupServerUrl;
        private TextBox _txtSetupToken;
        private TextBox _txtSetupTag;
        private Button _btnSetupInstall;

        public ManagerWindow()
        {
            this.Title = "Project R Endpoint Agent";
            this.Width = 500;
            this.SizeToContent = SizeToContent.Height;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#0b0f19"); // Deep Obsidian
            this.ResizeMode = ResizeMode.CanMinimize;
            this.FontFamily = new FontFamily("Segoe UI, -apple-system, sans-serif");

            SetWindowIcon();
            BuildUI();
            RefreshView();

            // Periodic connection health monitor (every 10 seconds)
            _statusTimer = new System.Windows.Threading.DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(10);
            _statusTimer.Tick += delegate
            {
                UpdateConnectionStatusAsync();
            };
            _statusTimer.Start();

            // Run first live connection check
            UpdateConnectionStatusAsync();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_statusTimer != null)
            {
                _statusTimer.Stop();
            }
            base.OnClosed(e);
        }

        private void SetWindowIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (!File.Exists(iconPath))
                {
                    iconPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Project R", "Agent", "app.ico");
                }
                if (File.Exists(iconPath))
                {
                    this.Icon = BitmapFrame.Create(new Uri(iconPath));
                }
            }
            catch { }
        }

        private void BuildUI()
        {
            Grid root = new Grid();
            root.Margin = new Thickness(16, 14, 16, 12);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: Top Header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: Dynamic Body (compact)
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: Bottom Status Bar

            // --- Row 0: Top Header ---
            Grid headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Logo Image
            Image logoImg = null;
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (!File.Exists(iconPath))
                {
                    iconPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Project R", "Agent", "app.ico");
                }
                if (File.Exists(iconPath))
                {
                    BitmapImage bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(iconPath);
                    bmp.DecodePixelWidth = 48;
                    bmp.DecodePixelHeight = 48;
                    bmp.EndInit();
                    logoImg = new Image { Source = bmp, Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                }
            }
            catch { }

            if (logoImg != null)
            {
                Grid.SetColumn(logoImg, 0);
                headerGrid.Children.Add(logoImg);
            }

            // Title & Device details
            StackPanel titlePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock titleText = new TextBlock
            {
                Text = "Project R Endpoint Agent",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#f8fafc")
            };
            titlePanel.Children.Add(titleText);

            _headerSubtitle = new TextBlock
            {
                Text = Environment.MachineName + "  -  v" + AgentConfig.CurrentVersion,
                FontSize = 10,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#94a3b8"),
                Margin = new Thickness(0, 2, 0, 0)
            };
            titlePanel.Children.Add(_headerSubtitle);

            Grid.SetColumn(titlePanel, logoImg != null ? 1 : 0);
            headerGrid.Children.Add(titlePanel);

            // Status Pill (Right)
            _statusBanner = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#064e3b"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#059669"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Klik untuk memeriksa ulang koneksi ke server Project R."
            };
            _statusPill = new TextBlock
            {
                Text = "● Memeriksa...",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#34d399")
            };
            _statusBanner.Child = _statusPill;
            _statusBanner.MouseLeftButtonUp += delegate
            {
                _statusPill.Text = "◌ Memeriksa...";
                UpdateConnectionStatusAsync();
            };
            Grid.SetColumn(_statusBanner, 2);
            headerGrid.Children.Add(_statusBanner);

            Grid.SetRow(headerGrid, 0);
            root.Children.Add(headerGrid);

            // --- Row 1: Dynamic Body Container ---
            _mainContainer = new Grid();
            Grid.SetRow(_mainContainer, 1);
            root.Children.Add(_mainContainer);

            // --- Row 2: Compact Status Bar ---
            Border footerBorder = new Border
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#111827"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 10, 0, 0),
                BorderThickness = new Thickness(1),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#1f2937")
            };
            _statusBarText = new TextBlock
            {
                Text = "Siap.",
                FontSize = 10,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#94a3b8")
            };
            footerBorder.Child = _statusBarText;
            Grid.SetRow(footerBorder, 2);
            root.Children.Add(footerBorder);

            this.Content = root;
        }

        private void RefreshView()
        {
            AgentConfig cfg = AgentConfig.Load();
            bool isInstalled = AgentConfig.IsInstalled();
            bool isServiceRunning = false;

            try
            {
                ServiceController sc = new ServiceController("ProjectRAgent");
                isServiceRunning = (sc.Status == ServiceControllerStatus.Running);
            }
            catch { }

            AgentRuntimeStatus runtime = AgentConfig.LoadStatus();

            // Update status badge from runtime state
            if (isInstalled && isServiceRunning)
            {
                if (runtime.IsConnected)
                {
                    _statusBanner.Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#064e3b");
                    _statusBanner.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#059669");
                    _statusPill.Text = "● Terhubung (Server Online)";
                    _statusPill.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#34d399");
                }
                else
                {
                    _statusBanner.Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#451a03");
                    _statusBanner.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#d97706");
                    _statusPill.Text = "▲ Server Terputus (Offline)";
                    _statusPill.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#fbbf24");
                }
            }
            else if (isInstalled)
            {
                _statusBanner.Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#450a0a");
                _statusBanner.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#dc2626");
                _statusPill.Text = "■ Service Berhenti";
                _statusPill.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#fca5a5");
            }
            else
            {
                _statusBanner.Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#111827");
                _statusBanner.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#374151");
                _statusPill.Text = "○ Belum Terpasang";
                _statusPill.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#9ca3af");
            }

            _mainContainer.Children.Clear();

            if (isInstalled)
            {
                RenderInstalledView(cfg);
            }
            else
            {
                RenderSetupView(cfg);
            }
        }

        private void UpdateConnectionStatusAsync()
        {
            if (_isCheckingStatus) return;
            _isCheckingStatus = true;

            AgentConfig cfg = AgentConfig.Load();
            bool isInstalled = AgentConfig.IsInstalled();
            bool isServiceRunning = false;

            try
            {
                ServiceController sc = new ServiceController("ProjectRAgent");
                isServiceRunning = (sc.Status == ServiceControllerStatus.Running);
            }
            catch { }

            if (!isInstalled)
            {
                _statusBanner.Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#111827");
                _statusBanner.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#374151");
                _statusPill.Text = "○ Belum Terpasang";
                _statusPill.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#9ca3af");
                _isCheckingStatus = false;
                return;
            }

            if (!isServiceRunning)
            {
                _statusBanner.Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#450a0a");
                _statusBanner.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#dc2626");
                _statusPill.Text = "■ Service Berhenti";
                _statusPill.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#fca5a5");
                _statusBarText.Text = "Service Windows 'ProjectRAgent' di komputer ini sedang mati.";
                _isCheckingStatus = false;
                return;
            }

            // Service is running: ping server asynchronously
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool serverReachable = ApiClient.PingServer(cfg.ServerUrl, 2500);
                AgentRuntimeStatus runtime = AgentConfig.LoadStatus();

                this.Dispatcher.Invoke(new Action(delegate
                {
                    try
                    {
                        if (serverReachable)
                        {
                            _statusBanner.Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#064e3b");
                            _statusBanner.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#059669");
                            _statusPill.Text = "● Terhubung (Server Online)";
                            _statusPill.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#34d399");

                            string lastSync = !string.IsNullOrEmpty(runtime.LastHeartbeatAt) ? runtime.LastHeartbeatAt : DateTime.Now.ToString("HH:mm:ss");
                            _statusBarText.Text = "Terhubung: " + cfg.ServerUrl + "  |  Detak terakhir: " + lastSync;
                        }
                        else
                        {
                            _statusBanner.Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#451a03");
                            _statusBanner.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#d97706");
                            _statusPill.Text = "▲ Server Terputus (Offline)";
                            _statusPill.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#fbbf24");

                            string lastSync = !string.IsNullOrEmpty(runtime.LastHeartbeatAt) ? ("Terakhir sukses: " + runtime.LastHeartbeatAt) : "Belum pernah tersambung";
                            _statusBarText.Text = "Server mati / terputus: " + cfg.ServerUrl + " (" + lastSync + ")";
                        }
                    }
                    catch { }
                    finally
                    {
                        _isCheckingStatus = false;
                    }
                }));
            });
        }

        /// <summary>
        /// When already installed: Clean compact view with Tag, Sync, and Administrative footer.
        /// </summary>
        private void RenderInstalledView(AgentConfig cfg)
        {
            StackPanel panel = new StackPanel();

            // CARD 1: Identitas Aset & Edit TAG
            Border cardTag = CreateCard();
            StackPanel stackTag = new StackPanel();

            stackTag.Children.Add(new TextBlock
            {
                Text = "Nomor Asset (TAG Perangkat)",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#f1f5f9")
            });
            stackTag.Children.Add(new TextBlock
            {
                Text = "Identitas unik perangkat yang disinkronkan ke server web Project R.",
                FontSize = 10,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#94a3b8"),
                Margin = new Thickness(0, 1, 0, 8)
            });

            Grid tagRow = new Grid();
            tagRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tagRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            tagRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _txtAssetTag = CreateTextBox(cfg.Tag);
            _txtAssetTag.Height = 32;
            Grid.SetColumn(_txtAssetTag, 0);
            tagRow.Children.Add(_txtAssetTag);

            _btnSaveTag = new Button
            {
                Content = "Simpan TAG",
                Width = 95,
                Height = 32,
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#2563eb"),
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#ffffff"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnSaveTag.Click += BtnSaveTag_Click;
            Grid.SetColumn(_btnSaveTag, 2);
            tagRow.Children.Add(_btnSaveTag);

            stackTag.Children.Add(tagRow);
            cardTag.Child = stackTag;
            panel.Children.Add(cardTag);

            // CARD 2: Inventaris & Sinkronisasi
            Border cardDeploy = CreateCard();
            cardDeploy.Margin = new Thickness(0, 10, 0, 0);
            StackPanel stackDeploy = new StackPanel();

            stackDeploy.Children.Add(new TextBlock
            {
                Text = "Telemetri & Inventaris Lengkap",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#f1f5f9")
            });
            stackDeploy.Children.Add(new TextBlock
            {
                Text = "Kirimkan seluruh spesifikasi hardware, BIOS, software, antivirus, dan evaluasi kepatuhan MDM.",
                FontSize = 10,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#94a3b8"),
                Margin = new Thickness(0, 1, 0, 10)
            });

            _btnForceDeploy = new Button
            {
                Content = "⚡  Kirim Inventaris Lengkap Sekarang",
                Height = 36,
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#059669"),
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#ffffff"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnForceDeploy.Click += BtnForceDeploy_Click;
            stackDeploy.Children.Add(_btnForceDeploy);

            // Animated progress bar
            _deployProgressBar = new ProgressBar
            {
                Height = 4,
                IsIndeterminate = true,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#10b981"),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1f2937"),
                BorderThickness = new Thickness(0),
                Visibility = Visibility.Collapsed
            };
            stackDeploy.Children.Add(_deployProgressBar);

            // Progress status description
            _deployProgressText = new TextBlock
            {
                FontSize = 10,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#38bdf8"),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            stackDeploy.Children.Add(_deployProgressText);

            cardDeploy.Child = stackDeploy;
            panel.Children.Add(cardDeploy);

            // Administrative Action Row (Server Config & Uninstall)
            Grid adminRow = new Grid();
            adminRow.Margin = new Thickness(2, 14, 2, 0);
            adminRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            adminRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock linkConfig = new TextBlock
            {
                Text = "⚙️ Pengaturan Server & Token",
                FontSize = 10,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#60a5fa"),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            linkConfig.MouseLeftButtonUp += delegate { OpenConfigDialog(cfg); };
            Grid.SetColumn(linkConfig, 0);
            adminRow.Children.Add(linkConfig);

            _btnUninstallAgent = new Button
            {
                Content = "🗑️ Uninstall Agent",
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#181111"),
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#f87171"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#7f1d1d"),
                BorderThickness = new Thickness(1),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnUninstallAgent.Click += BtnUninstall_Click;
            Grid.SetColumn(_btnUninstallAgent, 1);
            adminRow.Children.Add(_btnUninstallAgent);

            panel.Children.Add(adminRow);

            _mainContainer.Children.Add(panel);
        }

        /// <summary>
        /// When NOT installed: Show clean setup input form with mandatory validation.
        /// </summary>
        private void RenderSetupView(AgentConfig cfg)
        {
            Border card = CreateCard();
            StackPanel stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text = "Aktivasi & Pendaftaran Agent",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#f8fafc")
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Lengkapi seluruh konfigurasi di bawah untuk mengaktifkan agent di komputer ini.",
                FontSize = 10,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#94a3b8"),
                Margin = new Thickness(0, 2, 0, 12)
            });

            stack.Children.Add(new TextBlock { Text = "URL Endpoint Server:", FontSize = 10, Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#cbd5e1"), Margin = new Thickness(0, 0, 0, 3) });
            _txtSetupServerUrl = CreateTextBox(cfg.ServerUrl);
            _txtSetupServerUrl.Height = 32;
            _txtSetupServerUrl.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(_txtSetupServerUrl);

            stack.Children.Add(new TextBlock { Text = "Deployment Token Otorisasi:", FontSize = 10, Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#cbd5e1"), Margin = new Thickness(0, 0, 0, 3) });
            _txtSetupToken = CreateTextBox(cfg.Token);
            _txtSetupToken.Height = 32;
            _txtSetupToken.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(_txtSetupToken);

            stack.Children.Add(new TextBlock { Text = "Nomor Asset TAG Perangkat:", FontSize = 10, Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#cbd5e1"), Margin = new Thickness(0, 0, 0, 3) });
            _txtSetupTag = CreateTextBox(cfg.Tag);
            _txtSetupTag.Height = 32;
            _txtSetupTag.Margin = new Thickness(0, 0, 0, 14);
            stack.Children.Add(_txtSetupTag);

            _btnSetupInstall = new Button
            {
                Content = "Pasang & Aktifkan Agent Sekarang",
                Height = 36,
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#2563eb"),
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#ffffff"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnSetupInstall.Click += BtnSetupInstall_Click;
            stack.Children.Add(_btnSetupInstall);

            card.Child = stack;
            _mainContainer.Children.Add(card);
        }

        private Border CreateCard()
        {
            return new Border
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#111827"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12, 14, 12),
                BorderThickness = new Thickness(1),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#1f2937")
            };
        }

        private TextBox CreateTextBox(string text)
        {
            return new TextBox
            {
                Text = text ?? "",
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#0b0f19"),
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#f8fafc"),
                CaretBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#f8fafc"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#374151"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 11,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        // --- Action Handlers ---

        private void BtnSaveTag_Click(object sender, RoutedEventArgs e)
        {
            string newTag = _txtAssetTag.Text.Trim();
            if (string.IsNullOrEmpty(newTag))
            {
                MessageBox.Show("Nomor Asset (TAG) tidak boleh kosong!", "Peringatan", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AgentConfig cfg = AgentConfig.Load();
            _statusBarText.Text = "Menyimpan TAG dan menghubungi server Project R...";

            // 1. Simpan konfigurasi TAG lokal terlebih dahulu
            try
            {
                cfg.Tag = newTag;
                cfg.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal menyimpan konfigurasi TAG lokal: " + ex.Message + "\n\nPastikan izin folder konfigurasi tidak dibatasi.", "Error Izin File", MessageBoxButton.OK, MessageBoxImage.Error);
                _statusBarText.Text = "Gagal simpan TAG lokal: " + ex.Message;
                return;
            }

            // 2. Sinkronisasikan perubahan TAG ke server web
            try
            {
                string srv = ApiClient.SanitizeUrl(cfg.ServerUrl);
                string deviceId = "";
                try { deviceId = SystemInfo.Collect(cfg.Token, newTag, cfg.Version).device_id; } catch { }
                GenericApiResponse resp = ApiClient.UpdateTag(srv, cfg.Token, newTag, deviceId, Environment.MachineName);

                _statusBarText.Text = "TAG berhasil diperbarui menjadi '" + newTag + "' (" + DateTime.Now.ToString("HH:mm:ss") + ").";
                MessageBox.Show("Nomor Asset (TAG) berhasil diperbarui menjadi: " + newTag + "\n\nServer web Project R telah diperbarui.", "Berhasil", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _statusBarText.Text = "TAG tersimpan lokal. Gagal sinkron ke server: " + ex.Message;
                MessageBox.Show("TAG '" + newTag + "' berhasil disimpan di komputer ini.\n\nNamun belum berhasil terhubung ke server: " + ex.Message + "\nSinkronisasi ke web akan dicoba ulang secara otomatis pada detak jantung (heartbeat) berikutnya.", "Tersimpan Lokal", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnForceDeploy_Click(object sender, RoutedEventArgs e)
        {
            AgentConfig cfg = AgentConfig.Load();
            if (string.IsNullOrEmpty(cfg.ServerUrl))
            {
                MessageBox.Show("URL Endpoint Server belum disetel. Silakan atur melalui menu pengaturan server di bawah.", "Peringatan", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string sanitized = ApiClient.SanitizeUrl(cfg.ServerUrl);
            if (!string.Equals(sanitized, cfg.ServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                cfg.ServerUrl = sanitized;
                try { cfg.Save(); } catch { }
            }

            _btnForceDeploy.IsEnabled = false;
            _deployProgressBar.Visibility = Visibility.Visible;
            _deployProgressText.Visibility = Visibility.Visible;
            _deployProgressText.Text = "Mengumpulkan spesifikasi hardware, BIOS, dan sistem...";
            _statusBarText.Text = "Sedang sinkronisasi data inventaris ke server...";

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    this.Dispatcher.Invoke(new Action(delegate
                    {
                        _deployProgressText.Text = "Memindai daftar aplikasi terinstal dan proteksi keamanan...";
                    }));

                    DeviceInventoryData inv = SystemInfo.Collect(cfg.Token, cfg.Tag, cfg.Version);

                    this.Dispatcher.Invoke(new Action(delegate
                    {
                        _deployProgressText.Text = "Mengirimkan data inventaris lengkap ke server...";
                    }));

                    GenericApiResponse resp = ApiClient.RegisterDevice(cfg.ServerUrl, inv);

                    this.Dispatcher.Invoke(new Action(delegate
                    {
                        _deployProgressText.Text = "Mengevaluasi kepatuhan MDM & mengirim heartbeat...";
                    }));

                    HeartbeatRequest hb = new HeartbeatRequest();
                    hb.device_id = inv.device_id;
                    hb.hostname = Environment.MachineName;
                    hb.tag = cfg.Tag;
                    hb.version = cfg.Version;
                    hb.logged_user = SystemInfo.GetLoggedInUser();
                    hb.battery_percent = inv.battery.percent;
                    hb.token = cfg.Token;
                    hb.evaluations = PolicyEngine.EvaluateCompliance(new PolicyData());
                    HeartbeatResponse hbResp = ApiClient.SendHeartbeat(cfg.ServerUrl, hb);
                    if (hbResp != null && hbResp.deploy_tasks != null && hbResp.deploy_tasks.Count > 0)
                    {
                        this.Dispatcher.Invoke(new Action(delegate
                        {
                            _deployProgressText.Text = "Menjalankan " + hbResp.deploy_tasks.Count + " tugas deployment (Wallpaper, dll)...";
                        }));
                        DeployExecutor.ExecuteTasks(hbResp.deploy_tasks, cfg, null);
                    }

                    this.Dispatcher.Invoke(new Action(delegate
                    {
                        _deployProgressBar.Visibility = Visibility.Collapsed;
                        _deployProgressText.Visibility = Visibility.Collapsed;
                        _btnForceDeploy.IsEnabled = true;

                        try
                        {
                            AgentRuntimeStatus st = new AgentRuntimeStatus
                            {
                                IsConnected = true,
                                LastHeartbeatAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                                LastError = "",
                                ConsecutiveFailures = 0
                            };
                            AgentConfig.SaveStatus(st);
                        }
                        catch { }
                        UpdateConnectionStatusAsync();

                        string countMsg = "(" + inv.softwares.Count + " aplikasi, " + inv.monitors.Count + " monitor, " + inv.antivirus.Count + " antivirus)";
                        _statusBarText.Text = "Inventaris berhasil dikirimkan " + DateTime.Now.ToString("HH:mm:ss") + " " + countMsg;

                        MessageBox.Show("Inventaris Lengkap Berhasil Dikirimkan ke Web!\n\n" +
                                        "• Komputer : " + Environment.MachineName + "\n" +
                                        "• Asset TAG : " + cfg.Tag + "\n" +
                                        "• Aplikasi : " + inv.softwares.Count + " item terdata\n" +
                                        "• Antivirus & Monitor : " + inv.antivirus.Count + " AV, " + inv.monitors.Count + " monitor\n\n" +
                                        "Data langsung diperbarui di portal Project R.",
                                        "Sinkronisasi Sukses", MessageBoxButton.OK, MessageBoxImage.Information);
                    }));
                }
                catch (Exception ex)
                {
                    this.Dispatcher.Invoke(new Action(delegate
                    {
                        _deployProgressBar.Visibility = Visibility.Collapsed;
                        _deployProgressText.Visibility = Visibility.Collapsed;
                        _btnForceDeploy.IsEnabled = true;
                        _statusBarText.Text = "Gagal kirim inventaris: " + ex.Message;

                        try
                        {
                            AgentRuntimeStatus prev = AgentConfig.LoadStatus();
                            AgentRuntimeStatus st = new AgentRuntimeStatus
                            {
                                IsConnected = false,
                                LastHeartbeatAt = prev != null ? prev.LastHeartbeatAt : "",
                                LastError = ex.Message,
                                ConsecutiveFailures = (prev != null ? prev.ConsecutiveFailures : 0) + 1
                            };
                            AgentConfig.SaveStatus(st);
                        }
                        catch { }
                        UpdateConnectionStatusAsync();

                        MessageBox.Show("Gagal mengirimkan inventaris: " + ex.Message, "Error Koneksi", MessageBoxButton.OK, MessageBoxImage.Error);
                    }));
                }
            });
        }

        private void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Apakah Anda yakin ingin menghapus Project R Agent dari komputer ini?\n\n" +
                                "Tindakan ini memerlukan Token Otorisasi IT Uninstalasi resmi. Background service akan dihentikan, seluruh proteksi GPO dicabut, dan data PC akan dihapus dari web inventory.",
                                "Konfirmasi Uninstalasi", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            string uninstToken = Interaction.InputBox(
                "Masukkan Token Otorisasi IT Uninstalasi resmi komputer ini:\n(Contoh: PR-UNINST-XXXXXXXX)",
                "Proteksi Keamanan IT - Token Uninstall",
                ""
            );

            if (string.IsNullOrWhiteSpace(uninstToken))
            {
                MessageBox.Show("Uninstalasi Dibatalkan: Token uninstalasi wajib diisi untuk menghapus agent.", "Dibatalkan", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            uninstToken = uninstToken.Trim();
            AgentConfig cfg = AgentConfig.Load();
            string srv = ApiClient.SanitizeUrl(cfg.ServerUrl);

            _statusBarText.Text = "Memverifikasi token uninstalasi ke server...";

            try
            {
                string deviceId = "";
                try { deviceId = SystemInfo.Collect(cfg.Token, cfg.Tag, cfg.Version).device_id; } catch { }
                string uuid = "";
                try { uuid = SystemInfo.GetHardwareUuid(); } catch { }

                VerifyUninstallResponse vResp = ApiClient.VerifyUninstall(srv, uninstToken, Environment.MachineName, deviceId, uuid);
                if (vResp == null || !vResp.authorized)
                {
                    MessageBox.Show("Akses Ditolak: Token uninstalasi tidak valid atau ditolak oleh server Project R.\n\nHubungi Administrator IT untuk mendapatkan token uninstalasi resmi perangkat ini.", "Ditolak", MessageBoxButton.OK, MessageBoxImage.Error);
                    _statusBarText.Text = "Uninstalasi ditolak: Token tidak valid.";
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal memverifikasi token ke server: " + ex.Message + "\n\nUninstalasi dibatalkan demi keamanan.", "Error Verifikasi", MessageBoxButton.OK, MessageBoxImage.Error);
                _statusBarText.Text = "Gagal verifikasi server: " + ex.Message;
                return;
            }

            _statusBarText.Text = "Menghapus service dan membersihkan data agent...";

            try
            {
                string agentExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectRAgent.exe");
                if (File.Exists(agentExe))
                {
                    Process p = Process.Start(new ProcessStartInfo(agentExe, "--uninstall-service") { WindowStyle = ProcessWindowStyle.Hidden });
                    if (p != null) p.WaitForExit();
                }

                string configDir = AgentConfig.GetConfigDirectory();
                if (Directory.Exists(configDir))
                {
                    Directory.Delete(configDir, true);
                }
                string parent = Path.GetDirectoryName(configDir);
                if (Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
                {
                    Directory.Delete(parent);
                }

                PolicyEngine.RestoreDefaultPolicies();
                RefreshView();

                MessageBox.Show("Project R Agent telah berhasil di-uninstall secara bersih tanpa meninggalkan jejak di laptop ini.\n\nData perangkat juga telah dihapus dari web inventory Project R.", "Uninstall Berhasil", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal menghapus komponen agent: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSetupInstall_Click(object sender, RoutedEventArgs e)
        {
            string srv = ApiClient.SanitizeUrl(_txtSetupServerUrl.Text);
            string tok = _txtSetupToken.Text.Trim();
            string tag = _txtSetupTag.Text.Trim();

            if (string.IsNullOrEmpty(srv))
            {
                MessageBox.Show("URL Endpoint Server wajib diisi!", "Validasi Form", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(tok))
            {
                MessageBox.Show("Deployment Token Otorisasi wajib diisi!", "Validasi Form", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(tag))
            {
                MessageBox.Show("Nomor Asset TAG Perangkat wajib diisi!", "Validasi Form", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Strictly verify deployment token against server before saving!
            _statusBarText.Text = "Memvalidasi Deployment Token ke server...";
            try
            {
                VerifyTokenResponse vResp = ApiClient.VerifyToken(srv, tok);
                if (vResp == null || !vResp.valid)
                {
                    MessageBox.Show("Akses Ditolak: Deployment Token Otorisasi tidak valid atau tidak dikenali oleh server Project R.\n\nSilakan periksa kembali Deployment Token resmi di web admin sebelum melanjutkan.", "Token Tidak Valid", MessageBoxButton.OK, MessageBoxImage.Error);
                    _statusBarText.Text = "Aktivasi ditolak: Token tidak valid.";
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal memvalidasi token ke server: " + ex.Message + "\n\nPastikan URL server benar dan komputer terhubung ke internet.", "Koneksi Gagal", MessageBoxButton.OK, MessageBoxImage.Error);
                _statusBarText.Text = "Gagal menghubungi server.";
                return;
            }

            AgentConfig cfg = new AgentConfig();
            cfg.ServerUrl = srv;
            cfg.Token = tok;
            cfg.Tag = tag;
            cfg.Save();

            try
            {
                DeviceInventoryData inv = SystemInfo.Collect(tok, tag, cfg.Version);
                ApiClient.RegisterDevice(srv, inv);

                string agentExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectRAgent.exe");
                if (File.Exists(agentExe))
                {
                    Process p = Process.Start(new ProcessStartInfo(agentExe, "--install-service") { WindowStyle = ProcessWindowStyle.Hidden });
                    if (p != null) p.WaitForExit();
                }
            }
            catch { }

            RefreshView();
            MessageBox.Show("Pengaturan agent berhasil disimpan dan diaktifkan!", "Sukses", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenConfigDialog(AgentConfig cfg)
        {
            string newSrv = Interaction.InputBox("Masukkan URL Endpoint Server Project R:", "Pengaturan Server Endpoint", cfg.ServerUrl);
            if (string.IsNullOrEmpty(newSrv)) return;

            string newTok = Interaction.InputBox("Masukkan Deployment Token Otorisasi:", "Deployment Token", cfg.Token);
            if (string.IsNullOrEmpty(newTok)) return;

            string srvClean = ApiClient.SanitizeUrl(newSrv);
            string tokClean = newTok.Trim();

            // Verify before saving
            try
            {
                VerifyTokenResponse vResp = ApiClient.VerifyToken(srvClean, tokClean);
                if (vResp == null || !vResp.valid)
                {
                    MessageBox.Show("Akses Ditolak: Deployment Token Otorisasi tidak valid atau tidak dikenali oleh server Project R.", "Token Ditolak", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal memvalidasi token ke server: " + ex.Message, "Error Koneksi", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            cfg.ServerUrl = srvClean;
            cfg.Token = tokClean;
            cfg.Save();

            _statusBarText.Text = "Pengaturan endpoint server diperbarui: " + cfg.ServerUrl;
            MessageBox.Show("Server Endpoint berhasil disimpan:\n" + cfg.ServerUrl, "Tersimpan", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public class LockWindow : Window
    {
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        private string _expectedPassword;
        private TextBox _txtPin;
        private TextBlock _lblError;
        private System.Windows.Threading.DispatcherTimer _checkTimer;

        public LockWindow(string password)
        {
            _expectedPassword = password ?? "";

            this.Title = "Project R - Keamanan Aset (Terkunci)";
            this.WindowStyle = WindowStyle.None;
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;
            this.Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#0b0f19");
            this.ShowInTaskbar = false;
            this.FontFamily = new FontFamily("Segoe UI, -apple-system, sans-serif");

            this.Loaded += delegate
            {
                try
                {
                    this.Activate();
                    this.Topmost = true;
                    this.Focus();
                    IntPtr hWnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hWnd != IntPtr.Zero)
                    {
                        ShowWindow(hWnd, 3); // SW_MAXIMIZE
                        BringWindowToTop(hWnd);
                        SetForegroundWindow(hWnd);
                    }
                }
                catch { }
            };

            // Prevent closing via Alt+F4
            this.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                LockState state = AgentConfig.LoadLockState();
                if (state.IsLocked)
                {
                    e.Cancel = true;
                }
            };

            BuildUI();

            // Periodic check every 3s: if server unlocked, close automatically
            _checkTimer = new System.Windows.Threading.DispatcherTimer();
            _checkTimer.Interval = TimeSpan.FromSeconds(3);
            _checkTimer.Tick += delegate(object s, EventArgs e)
            {
                LockState state = AgentConfig.LoadLockState();
                if (!state.IsLocked)
                {
                    _checkTimer.Stop();
                    this.Close();
                }
                else
                {
                    try
                    {
                        this.Topmost = true;
                        this.Activate();
                    }
                    catch { }
                }
            };
            _checkTimer.Start();
        }

        private void BuildUI()
        {
            Grid root = new Grid();
            root.Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#0b0f19");

            Border card = new Border
            {
                Width = 480,
                Padding = new Thickness(28),
                CornerRadius = new CornerRadius(16),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#111827"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#dc2626"),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            StackPanel panel = new StackPanel();

            // Red Warning Badge / Icon
            Border iconBadge = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(28),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#450a0a"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#dc2626"),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            TextBlock iconText = new TextBlock
            {
                Text = "🔒",
                FontSize = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBadge.Child = iconText;
            panel.Children.Add(iconBadge);

            // Title
            panel.Children.Add(new TextBlock
            {
                Text = "PERANGKAT INI TELAH DIKUNCI",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#f87171"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });

            // Subtitle
            panel.Children.Add(new TextBlock
            {
                Text = "Akses pada komputer ini telah dibatasi sementara oleh Administrator IT melalui portal Project R Security.",
                FontSize = 11,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#94a3b8"),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 18)
            });

            // Device Info Box
            Border infoBox = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1e293b"),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 18)
            };
            StackPanel infoPanel = new StackPanel();
            infoPanel.Children.Add(new TextBlock
            {
                Text = "Komputer: " + Environment.MachineName + "  •  User: " + Environment.UserName,
                FontSize = 10,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#cbd5e1"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            infoBox.Child = infoPanel;
            panel.Children.Add(infoBox);

            // PIN Label
            panel.Children.Add(new TextBlock
            {
                Text = "Masukkan Password / PIN Buka Kunci IT:",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#f1f5f9"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });

            // PIN Input
            _txtPin = new TextBox
            {
                Width = 240,
                Height = 44,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                MaxLength = 10,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#0f172a"),
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#f8fafc"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#475569"),
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 0, 0, 14)
            };
            _txtPin.KeyDown += delegate(object s, System.Windows.Input.KeyEventArgs e)
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    BtnUnlock_Click(null, null);
                }
            };
            panel.Children.Add(_txtPin);

            // Unlock Button
            Button btnUnlock = new Button
            {
                Width = 240,
                Height = 38,
                Content = "🔓  Buka Kunci Layar",
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#dc2626"),
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#ffffff"),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 10)
            };
            btnUnlock.Click += BtnUnlock_Click;
            panel.Children.Add(btnUnlock);

            // Error Label
            _lblError = new TextBlock
            {
                Text = "",
                FontSize = 11,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#f87171"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 0)
            };
            panel.Children.Add(_lblError);

            // Footer note
            panel.Children.Add(new TextBlock
            {
                Text = "Hubungi Helpdesk IT Infrastructure jika Anda tidak memiliki kode PIN ini.",
                FontSize = 9,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#64748b"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0)
            });

            card.Child = panel;
            root.Children.Add(card);
            this.Content = root;
        }

        private void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            string entered = _txtPin.Text.Trim();
            if (string.IsNullOrEmpty(entered))
            {
                _lblError.Text = "Harap masukkan Password Unlock.";
                _lblError.Visibility = Visibility.Visible;
                return;
            }

            // Reload lock state in case password was updated
            LockState state = AgentConfig.LoadLockState();
            string expected = !string.IsNullOrEmpty(state.UnlockPassword) ? state.UnlockPassword : _expectedPassword;

            if (entered.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                // Unlocked successfully!
                AgentConfig.SaveLockState(new LockState { IsLocked = false });
                if (_checkTimer != null) _checkTimer.Stop();

                // 1. Drop screen_unlocked.flag for ProjectRAgent.exe service heartbeat
                try
                {
                    string flagPath = Path.Combine(AgentConfig.GetConfigDirectory(), "screen_unlocked.flag");
                    File.WriteAllText(flagPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "|" + entered, Encoding.UTF8);
                }
                catch { }

                // 2. Notify server immediately via API
                try
                {
                    AgentConfig cfg = AgentConfig.Load();
                    if (!string.IsNullOrEmpty(cfg.ServerUrl))
                    {
                        string host = Environment.MachineName;
                        string devId = null;
                        try { devId = SystemInfo.Collect(cfg.Token, cfg.Tag, cfg.Version).device_id; } catch { }
                        ApiClient.UnlockScreen(cfg.ServerUrl, cfg.Token, entered, host, devId);
                    }
                }
                catch { }

                MessageBox.Show("Password Unlock Benar!\n\nPerangkat telah berhasil dibuka kuncinya.", "Kunci Dibuka", MessageBoxButton.OK, MessageBoxImage.Information);
                this.Close();
            }
            else
            {
                _lblError.Text = "Password Unlock Salah! Hubungi Administrator IT.";
                _lblError.Visibility = Visibility.Visible;
                _txtPin.SelectAll();
                _txtPin.Focus();
            }
        }
    }

    public class LockWatcherContext
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        private LockWindow _activeLockWindow = null;
        private bool _isLockWindowActive = false;
        private System.Windows.Threading.DispatcherTimer _timer;
        private DateTime _lastWallpaperCheck = DateTime.MinValue;
        private string _lastAppliedWallpaper = "";

        public LockWatcherContext()
        {
            _timer = new System.Windows.Threading.DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += CheckLockState;
            _timer.Start();

            CheckLockState(null, null);
        }

        private void CheckLockState(object sender, EventArgs e)
        {
            try
            {
                LockState state = AgentConfig.LoadLockState();

                // 1. Enforce Screen Warning Lock
                if (state.IsLocked)
                {
                    if (_activeLockWindow == null && !_isLockWindowActive)
                    {
                        _isLockWindowActive = true;
                        _activeLockWindow = new LockWindow(state.UnlockPassword);
                        _activeLockWindow.Closed += delegate
                        {
                            _activeLockWindow = null;
                            _isLockWindowActive = false;
                        };
                        _activeLockWindow.Show();
                        try
                        {
                            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProjectR", "Agent");
                            File.AppendAllText(Path.Combine(logDir, "mgr_err.txt"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] LockWindow shown on display. PIN=" + state.UnlockPassword + Environment.NewLine);
                        }
                        catch { }
                    }
                    else if (_activeLockWindow != null)
                    {
                        try
                        {
                            if (!_activeLockWindow.IsVisible)
                            {
                                _activeLockWindow.Show();
                            }
                            _activeLockWindow.Topmost = true;
                        }
                        catch { }
                    }
                }
                else
                {
                    if (_activeLockWindow != null)
                    {
                        try { _activeLockWindow.Close(); } catch { }
                        _activeLockWindow = null;
                        _isLockWindowActive = false;
                    }
                }

                // 2. Active Process Termination for Blocked Tools (CMD, PowerShell, and Windows Terminal)
                if (state.IsCmdDisabled)
                {
                    string[] termApps = new string[] { "WindowsTerminal", "wt", "OpenConsole" };
                    foreach (string term in termApps)
                    {
                        foreach (Process p in Process.GetProcessesByName(term))
                        {
                            try { p.Kill(); } catch { }
                        }
                    }

                    string[] procNames = new string[] { "powershell", "pwsh", "powershell_ise", "cmd" };
                    int currentSession = Process.GetCurrentProcess().SessionId;
                    foreach (string name in procNames)
                    {
                        foreach (Process p in Process.GetProcessesByName(name))
                        {
                            try
                            {
                                if (p.SessionId == currentSession && (p.MainWindowHandle != IntPtr.Zero || !string.IsNullOrEmpty(p.MainWindowTitle)))
                                {
                                    p.Kill();
                                }
                            }
                            catch { }
                        }
                    }
                }

                // 3. Active Process Termination for Task Manager
                if (state.IsTaskMgrDisabled)
                {
                    foreach (Process p in Process.GetProcessesByName("Taskmgr"))
                    {
                        try { p.Kill(); } catch { }
                    }
                }

                // 4. Suppress and kill legacy wscript error popups if any
                foreach (Process p in Process.GetProcessesByName("wscript"))
                {
                    try { p.Kill(); } catch { }
                }

                // 5. Active Browser Window Blacklist Inspection (Native User Desktop Session)
                try
                {
                    PolicyData pol = AgentConfig.LoadPolicies();
                    if (pol != null && pol.browser_monitor_enabled && pol.browser_blacklist != null && pol.browser_blacklist.Count > 0)
                    {
                        BrowserAlertItem alert = BrowserMonitor.ScanForegroundWindow(pol.browser_blacklist, Environment.UserName);
                        if (alert != null)
                        {
                            BrowserMonitor.EnqueueAlert(alert);
                        }
                    }
                }
                catch { }

                // 6. Interactive User Wallpaper Enforcement
                if ((DateTime.Now - _lastWallpaperCheck).TotalSeconds >= 15)
                {
                    _lastWallpaperCheck = DateTime.Now;
                    try
                    {
                        string wp = @"C:\Windows\Web\Wallpaper\company_wallpaper.jpg";
                        if (File.Exists(wp) && _lastAppliedWallpaper != wp)
                        {
                            SystemParametersInfo(20, 0, wp, 3);
                            _lastAppliedWallpaper = wp;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    public class RemoteSessionWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private string _adminName;
        private string _adminRole;
        private int _timeoutRemaining;
        private System.Windows.Threading.DispatcherTimer _countdownTimer;
        private System.Windows.Threading.DispatcherTimer _sessionStopwatch;
        private int _sessionSeconds = 0;
        private bool _isApproved = false;
        private bool _isClosing = false;
        private Thread _stdinThread;

        // UI containers
        private Grid _rootGrid;
        private Border _consentCard;
        private TextBlock _lblCountdown;
        private ProgressBar _pbCountdown;

        // Dock UI
        private Border _dockCard;
        private TextBlock _lblTimer;
        private Border _chatFlyout;
        private StackPanel _chatMessagesPanel;
        private ScrollViewer _chatScrollViewer;
        private TextBox _txtChatInput;
        private Border _unreadBadge;
        private TextBlock _lblUnreadCount;
        private int _unreadCount = 0;
        private bool _isChatExpanded = false;

        public RemoteSessionWindow(string adminName, string adminRole, int timeoutSec)
        {
            _adminName = string.IsNullOrEmpty(adminName) ? "Admin IT" : adminName;
            _adminRole = string.IsNullOrEmpty(adminRole) ? "IT Support" : adminRole;
            _timeoutRemaining = timeoutSec > 0 ? timeoutSec : 120;

            this.Title = "Project R - Remote Desktop Session";
            this.WindowStyle = WindowStyle.None;
            this.AllowsTransparency = false;
            this.Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#18181b");
            this.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#10b981");
            this.BorderThickness = new Thickness(2);
            this.Topmost = true;
            this.ShowInTaskbar = true;
            this.FontFamily = new FontFamily("Segoe UI, -apple-system, sans-serif");

            Rect workArea = SystemParameters.WorkArea;
            this.Width = 560;
            this.MinHeight = 440;
            this.SizeToContent = SizeToContent.Height;
            this.Left = workArea.Left + (workArea.Width - 560) / 2;
            this.Top = workArea.Top + (workArea.Height - 440) / 2;
            this.WindowStartupLocation = WindowStartupLocation.Manual;

            _rootGrid = new Grid();
            this.Content = _rootGrid;

            BuildConsentUI();
            StartCountdown();

            this.Loaded += delegate
            {
                try
                {
                    Rect wa = SystemParameters.WorkArea;
                    this.Left = wa.Left + (wa.Width - this.ActualWidth) / 2;
                    this.Top = wa.Top + (wa.Height - this.ActualHeight) / 2;

                    this.Activate();
                    this.Topmost = true;
                    this.Focus();

                    IntPtr hWnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hWnd != IntPtr.Zero)
                    {
                        ShowWindow(hWnd, 5); // SW_SHOW
                        ShowWindow(hWnd, 9); // SW_RESTORE
                        BringWindowToTop(hWnd);
                        SetForegroundWindow(hWnd);
                        SetWindowPos(hWnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW
                    }

                    try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                }
                catch { }
            };

            this.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (!_isApproved && !_isClosing)
                {
                    RejectConsent("Jendela ditutup pengguna");
                }
            };
        }

        private string FormatTimeout(int seconds)
        {
            if (seconds <= 0) return "0 detik";
            int m = seconds / 60;
            int s = seconds % 60;
            if (m > 0)
            {
                return s > 0 ? string.Format("{0} menit {1} detik", m, s) : string.Format("{0} menit", m);
            }
            return string.Format("{0} detik", s);
        }

        private void BuildConsentUI()
        {
            _rootGrid.Children.Clear();

            _consentCard = new Border
            {
                Padding = new Thickness(28, 24, 28, 24),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#18181b"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            StackPanel sp = new StackPanel();

            // Header Row: Icon + Title
            Grid headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border iconBorder = new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(12),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#064e3b"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#059669"),
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 0, 16, 0)
            };
            TextBlock iconText = new TextBlock
            {
                Text = "🖥️",
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBorder.Child = iconText;
            Grid.SetColumn(iconBorder, 0);

            StackPanel titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock titleText = new TextBlock
            {
                Text = "Permintaan Remote Desktop",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap
            };
            TextBlock subtitleText = new TextBlock
            {
                Text = "Bantuan Teknis Langsung • Project R ITAM & MDM",
                FontSize = 11.5,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#a1a1aa"),
                Margin = new Thickness(0, 3, 0, 0)
            };
            titleStack.Children.Add(titleText);
            titleStack.Children.Add(subtitleText);
            Grid.SetColumn(titleStack, 1);

            headerGrid.Children.Add(iconBorder);
            headerGrid.Children.Add(titleStack);
            sp.Children.Add(headerGrid);

            // Divider
            Border div = new Border
            {
                Height = 1,
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#27272a"),
                Margin = new Thickness(0, 16, 0, 16)
            };
            sp.Children.Add(div);

            // Admin Identity Box
            Border adminBox = new Border
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#27272a"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid adminGrid = new Grid();
            adminGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            adminGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel adminInfo = new StackPanel();
            TextBlock lblAdminName = new TextBlock
            {
                Text = _adminName,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            };
            TextBlock lblDesc = new TextBlock
            {
                Text = "Meminta izin akses kendali mouse, keyboard, dan visual layar",
                FontSize = 11,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#94a3b8"),
                Margin = new Thickness(0, 3, 0, 0)
            };
            adminInfo.Children.Add(lblAdminName);
            adminInfo.Children.Add(lblDesc);
            Grid.SetColumn(adminInfo, 0);

            Border roleBadge = new Border
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#064e3b"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#059669"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock lblRole = new TextBlock
            {
                Text = _adminRole,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#34d399")
            };
            roleBadge.Child = lblRole;
            Grid.SetColumn(roleBadge, 1);

            adminGrid.Children.Add(adminInfo);
            adminGrid.Children.Add(roleBadge);
            adminBox.Child = adminGrid;
            sp.Children.Add(adminBox);

            // Privacy & Security Explanation Box
            Border secBox = new Border
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1e293b"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 16)
            };
            TextBlock explText = new TextBlock
            {
                Text = "🔒 Administrator IT akan dapat melihat tampilan layar dan membantu kendali jarak jauh. Seluruh sesi diaudit untuk kepatuhan keamanan. Anda memegang kendali penuh dan dapat memutuskan sesi kapan saja melalui toolbar.",
                FontSize = 11.5,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#cbd5e1"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17
            };
            secBox.Child = explText;
            sp.Children.Add(secBox);

            // Countdown Indicator
            Grid cdGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            _lblCountdown = new TextBlock
            {
                Text = "⏱️ Waktu konfirmasi tersisa: " + FormatTimeout(_timeoutRemaining),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#fbbf24"),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            cdGrid.Children.Add(_lblCountdown);
            sp.Children.Add(cdGrid);

            _pbCountdown = new ProgressBar
            {
                Height = 4,
                Maximum = _timeoutRemaining,
                Value = _timeoutRemaining,
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#27272a"),
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#10b981"),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 18)
            };
            sp.Children.Add(_pbCountdown);

            // Button Row
            Grid btnGrid = new Grid();
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });

            Button btnReject = new Button
            {
                Content = "✕  Tolak Akses",
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#27272a"),
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#f87171"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#ef4444"),
                BorderThickness = new Thickness(1),
                Height = 42,
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 8, 0)
            };
            btnReject.Click += delegate { RejectConsent("Ditolak oleh pengguna klien"); };
            Grid.SetColumn(btnReject, 0);

            Button btnAccept = new Button
            {
                Content = "✓  Izinkan Akses Sekarang",
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#059669"),
                Foreground = Brushes.White,
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#10b981"),
                BorderThickness = new Thickness(1),
                Height = 42,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                Margin = new Thickness(8, 0, 0, 0)
            };
            btnAccept.Click += delegate { AcceptConsent(); };
            Grid.SetColumn(btnAccept, 1);

            btnGrid.Children.Add(btnReject);
            btnGrid.Children.Add(btnAccept);
            sp.Children.Add(btnGrid);

            _consentCard.Child = sp;
            _rootGrid.Children.Add(_consentCard);
        }

        private void StartCountdown()
        {
            _countdownTimer = new System.Windows.Threading.DispatcherTimer();
            _countdownTimer.Interval = TimeSpan.FromSeconds(1);
            _countdownTimer.Tick += delegate
            {
                _timeoutRemaining--;
                if (_lblCountdown != null)
                {
                    _lblCountdown.Text = "⏱️ Waktu konfirmasi tersisa: " + FormatTimeout(_timeoutRemaining);
                }
                if (_pbCountdown != null)
                {
                    _pbCountdown.Value = _timeoutRemaining;
                }
                if (_timeoutRemaining <= 0)
                {
                    _countdownTimer.Stop();
                    RejectConsent("Waktu persetujuan habis (timeout 2 menit)");
                }
            };
            _countdownTimer.Start();
        }

        private void AcceptConsent()
        {
            if (_isApproved) return;
            _isApproved = true;

            if (_countdownTimer != null)
            {
                _countdownTimer.Stop();
                _countdownTimer = null;
            }

            // Output JSON result to stdout for parent ProjectRRemoteAgent
            Console.WriteLine("{\"type\":\"consent_result\",\"approved\":true}");

            // Switch to Docked Mode in Bottom-Right
            BuildDockedUI();
        }

        private void RejectConsent(string reason)
        {
            if (_isApproved || _isClosing) return;
            _isClosing = true;

            if (_countdownTimer != null)
            {
                try { _countdownTimer.Stop(); } catch { }
                _countdownTimer = null;
            }

            string jsonReason = EscapeJson(reason);
            try { Console.WriteLine("{\"type\":\"consent_result\",\"approved\":false,\"reason\":\"" + jsonReason + "\"}"); } catch { }
            try { this.Close(); } catch { }
        }

        private void BuildDockedUI()
        {
            _rootGrid.Children.Clear();
            this.SizeToContent = SizeToContent.Manual;
            this.MinHeight = 0;
            this.MinWidth = 0;
            this.BorderThickness = new Thickness(0);

            Rect workArea = SystemParameters.WorkArea;
            this.Width = 430;
            this.Height = 52;
            this.Left = workArea.Right - 448;
            this.Top = workArea.Bottom - 60;

            Grid container = new Grid();
            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Chat flyout row
            container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Mini bar row

            // Main Mini Bar Border
            _dockCard = new Border
            {
                Height = 48,
                VerticalAlignment = VerticalAlignment.Bottom,
                Padding = new Thickness(12, 6, 12, 6),
                CornerRadius = new CornerRadius(8),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#18181b"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#10b981"),
                BorderThickness = new Thickness(1.5)
            };

            Grid barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Dot + Title + Timer
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Spacer
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Chat Button
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // End Button

            // Status + Name + Timer
            StackPanel statusStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            TextBlock dot = new TextBlock
            {
                Text = "● ",
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#10b981"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            statusStack.Children.Add(dot);

            StackPanel textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock lblTitle = new TextBlock
            {
                Text = "Sesi: " + _adminName,
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                MaxWidth = 170,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _lblTimer = new TextBlock
            {
                Text = "00:00:00",
                FontSize = 10,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#94a3b8"),
                Margin = new Thickness(0, 1, 0, 0)
            };
            textStack.Children.Add(lblTitle);
            textStack.Children.Add(_lblTimer);
            statusStack.Children.Add(textStack);

            Grid.SetColumn(statusStack, 0);
            barGrid.Children.Add(statusStack);

            // Chat Button with Unread Badge
            Grid chatBtnGrid = new Grid { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            Button btnChat = new Button
            {
                Content = "💬 Chat",
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#27272a"),
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#38bdf8"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#3f3f46"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 5, 12, 5),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand
            };
            btnChat.Click += delegate { ToggleChatFlyout(); };
            chatBtnGrid.Children.Add(btnChat);

            _unreadBadge = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#ef4444"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -6, -6, 0),
                Visibility = Visibility.Collapsed
            };
            _lblUnreadCount = new TextBlock
            {
                Text = "1",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _unreadBadge.Child = _lblUnreadCount;
            chatBtnGrid.Children.Add(_unreadBadge);

            Grid.SetColumn(chatBtnGrid, 2);
            barGrid.Children.Add(chatBtnGrid);

            // End Session Button
            Button btnEnd = new Button
            {
                Content = "🔴 Akhiri",
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#450a0a"),
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#f87171"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#dc2626"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 5, 12, 5),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            btnEnd.Click += delegate
            {
                MessageBoxResult res = MessageBox.Show(this, "Akhiri sesi remote desktop dari admin IT sekarang?", "Konfirmasi", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    Console.WriteLine("{\"type\":\"end_session\"}");
                    this.Close();
                }
            };
            Grid.SetColumn(btnEnd, 3);
            barGrid.Children.Add(btnEnd);

            _dockCard.Child = barGrid;
            Grid.SetRow(_dockCard, 1);

            // Chat Flyout Border (Positioned in Row 0)
            _chatFlyout = new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                CornerRadius = new CornerRadius(10),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#18181b"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#27272a"),
                BorderThickness = new Thickness(1.5),
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(_chatFlyout, 0);

            BuildChatFlyoutContent();

            container.Children.Add(_chatFlyout);
            container.Children.Add(_dockCard);
            _rootGrid.Children.Add(container);

            // Start Stopwatch
            _sessionStopwatch = new System.Windows.Threading.DispatcherTimer();
            _sessionStopwatch.Interval = TimeSpan.FromSeconds(1);
            _sessionStopwatch.Tick += delegate
            {
                _sessionSeconds++;
                TimeSpan t = TimeSpan.FromSeconds(_sessionSeconds);
                if (_lblTimer != null)
                {
                    _lblTimer.Text = t.ToString(@"hh\:mm\:ss");
                }
            };
            _sessionStopwatch.Start();

            // Start Stdin Reader Thread
            _stdinThread = new Thread(ReadStdinLoop);
            _stdinThread.IsBackground = true;
            _stdinThread.Start();
        }

        private void BuildChatFlyoutContent()
        {
            Grid chatGrid = new Grid();
            chatGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) }); // Header
            chatGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Messages
            chatGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) }); // Input

            // Header
            Border headerBorder = new Border
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#27272a"),
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding = new Thickness(14, 0, 10, 0)
            };
            Grid headerGrid = new Grid();
            TextBlock chatTitle = new TextBlock
            {
                Text = "💬 Obrolan Langsung • " + _adminName,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            Button btnCloseChat = new Button
            {
                Content = "✕",
                Background = Brushes.Transparent,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#a1a1aa"),
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 4, 8, 4)
            };
            btnCloseChat.Click += delegate { ToggleChatFlyout(); };
            headerGrid.Children.Add(chatTitle);
            headerGrid.Children.Add(btnCloseChat);
            headerBorder.Child = headerGrid;
            Grid.SetRow(headerBorder, 0);
            chatGrid.Children.Add(headerBorder);

            // Message ScrollViewer
            _chatScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(12, 10, 12, 10),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#141416")
            };
            _chatMessagesPanel = new StackPanel();
            _chatScrollViewer.Content = _chatMessagesPanel;
            Grid.SetRow(_chatScrollViewer, 1);
            chatGrid.Children.Add(_chatScrollViewer);

            // Welcome bubble
            AddChatBubble("Sistem", "Sesi remote terhubung. Anda dapat berkirim pesan langsung dengan Admin IT di sini.", DateTime.Now.ToString("HH:mm"), false);

            // Input Row
            Border inputBorder = new Border
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#202023"),
                CornerRadius = new CornerRadius(0, 0, 10, 10),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#27272a"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };
            Grid inputGrid = new Grid();
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _txtChatInput = new TextBox
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#27272a"),
                Foreground = Brushes.White,
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#3f3f46"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6, 10, 6),
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _txtChatInput.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    SendClientChatMessage();
                    e.Handled = true;
                }
            };
            Grid.SetColumn(_txtChatInput, 0);

            Button btnSend = new Button
            {
                Content = "Kirim",
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#059669"),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 6, 14, 6),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                Margin = new Thickness(8, 0, 0, 0)
            };
            btnSend.Click += delegate { SendClientChatMessage(); };
            Grid.SetColumn(btnSend, 1);

            inputGrid.Children.Add(_txtChatInput);
            inputGrid.Children.Add(btnSend);
            inputBorder.Child = inputGrid;
            Grid.SetRow(inputBorder, 2);
            chatGrid.Children.Add(inputBorder);

            _chatFlyout.Child = chatGrid;
        }

        private void ToggleChatFlyout()
        {
            _isChatExpanded = !_isChatExpanded;
            Rect wa = SystemParameters.WorkArea;

            if (_isChatExpanded)
            {
                this.Height = 460;
                this.Top = wa.Bottom - 470;
                _chatFlyout.Visibility = Visibility.Visible;
                _unreadCount = 0;
                if (_unreadBadge != null) _unreadBadge.Visibility = Visibility.Collapsed;
                if (_txtChatInput != null) _txtChatInput.Focus();
                if (_chatScrollViewer != null) _chatScrollViewer.ScrollToEnd();
            }
            else
            {
                _chatFlyout.Visibility = Visibility.Collapsed;
                this.Height = 52;
                this.Top = wa.Bottom - 60;
            }
        }

        private void SendClientChatMessage()
        {
            if (_txtChatInput == null) return;
            string text = _txtChatInput.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            _txtChatInput.Text = "";
            string now = DateTime.Now.ToString("HH:mm");

            AddChatBubble("Anda", text, now, true);

            string json = string.Format(
                "{{\"type\":\"chat_message\",\"sender\":\"client\",\"sender_name\":\"Pengguna\",\"text\":\"{0}\",\"timestamp\":\"{1}\"}}",
                EscapeJson(text),
                now
            );
            Console.WriteLine(json);
        }

        private void AddChatBubble(string sender, string text, string time, bool isClient)
        {
            Border bubble = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString(isClient ? "#064e3b" : (sender == "Sistem" ? "#1e293b" : "#27272a")),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(isClient ? "#059669" : (sender == "Sistem" ? "#334155" : "#3f3f46")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(isClient ? 40 : 0, 4, isClient ? 0 : 40, 4),
                HorizontalAlignment = isClient ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 310
            };

            StackPanel sp = new StackPanel();
            TextBlock header = new TextBlock
            {
                Text = sender + " • " + time,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(isClient ? "#a7f3d0" : (sender == "Sistem" ? "#94a3b8" : "#38bdf8")),
                Margin = new Thickness(0, 0, 0, 3)
            };
            TextBlock body = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16
            };

            sp.Children.Add(header);
            sp.Children.Add(body);
            bubble.Child = sp;

            _chatMessagesPanel.Children.Add(bubble);
            if (_chatScrollViewer != null) _chatScrollViewer.ScrollToEnd();
        }

        private void ReadStdinLoop()
        {
            try
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                string line;
                while ((line = Console.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    try
                    {
                        var dict = serializer.Deserialize<Dictionary<string, object>>(line);
                        if (dict == null || !dict.ContainsKey("type")) continue;

                        string type = dict["type"].ToString();
                        if (type == "chat_message")
                        {
                            string senderName = dict.ContainsKey("sender_name") ? dict["sender_name"].ToString() : "Admin IT";
                            string text = dict.ContainsKey("text") ? dict["text"].ToString() : "";
                            string time = dict.ContainsKey("timestamp") ? dict["timestamp"].ToString() : DateTime.Now.ToString("HH:mm");

                            Dispatcher.Invoke(new Action(() =>
                            {
                                AddChatBubble(senderName, text, time, false);
                                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }

                                if (!_isChatExpanded)
                                {
                                    _unreadCount++;
                                    if (_unreadBadge != null && _lblUnreadCount != null)
                                    {
                                        _lblUnreadCount.Text = _unreadCount.ToString();
                                        _unreadBadge.Visibility = Visibility.Visible;
                                    }
                                }
                            }));
                        }
                        else if (type == "session_ended")
                        {
                            Dispatcher.Invoke(new Action(() =>
                            {
                                this.Close();
                            }));
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n")
                    .Replace("\t", "\\t");
        }
    }
}
