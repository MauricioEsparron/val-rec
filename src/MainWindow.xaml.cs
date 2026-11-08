using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ValRec;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    const int WM_HOTKEY = 0x0312, HOTKEY_ID = 0x5652, VK_F9 = 0x78;

    readonly Settings _s = Settings.Load();
    readonly Recorder _rec = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Forms.NotifyIcon _tray = new();
    DateTime _t0;
    bool _busy, _exit, _want;
    int _part, _fails;

    static readonly (string, int)[] Qualities = { ("Baja (6 Mbps)", 6), ("Media (12 Mbps)", 12), ("Alta (20 Mbps)", 20), ("Máxima (35 Mbps)", 35) };

    public MainWindow()
    {
        InitializeComponent();
        GpuText.Text = Recorder.DetectGpu() switch
        {
            "amd" => "AMD · captura y codificación por hardware",
            "nvidia" => "NVIDIA · codificación por hardware (NVENC)",
            "intel" => "Intel · codificación por hardware (QuickSync)",
            _ => "Sin GPU compatible detectada · se usará CPU"
        };

        FpsBox.ItemsSource = new[] { "30 fps", "60 fps" };
        FpsBox.SelectedIndex = _s.Fps >= 60 ? 1 : 0;
        QualityBox.ItemsSource = Qualities.Select(q => q.Item1).ToList();
        QualityBox.SelectedIndex = Math.Max(0, Array.FindIndex(Qualities, q => q.Item2 == _s.Mbps));
        AudioCheck.IsChecked = _s.Audio;
        FolderBox.Text = _s.Folder;

        _tick.Tick += (_, _) => UpdateTimer();

        _tray.Icon = System.Drawing.SystemIcons.Application;
        _tray.Text = "VAL Rec";
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Iniciar / detener (F9)", null, async (_, _) => await Dispatcher.InvokeAsync(Toggle).Task.Unwrap());
        menu.Items.Add("Mostrar ventana", null, (_, _) => ShowWindow());
        menu.Items.Add("Salir", null, async (_, _) => { _exit = true; await Dispatcher.InvokeAsync(async () => { await StopIfRecording(); Close(); }).Task.Unwrap(); });
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowWindow(); };
        _tray.Visible = true;

        var args = Environment.GetCommandLineArgs();
        int ti = Array.IndexOf(args, "--test");
        if (ti >= 0 && ti + 1 < args.Length && int.TryParse(args[ti + 1], out int secs))
            Loaded += async (_, _) =>
            {   // autoprueba: graba N segundos, guarda el resultado y sale
                string res;
                try
                {
                    await Toggle(); await Task.Delay(secs * 1000); await Toggle();
                    res = "OK " + _rec.OutputPath + "\n" + _rec.LastLog;
                }
                catch (Exception ex) { res = "FALLO " + ex; }
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "test-result.txt"), res);
                _exit = true; Close();
            };

        SourceInitialized += (_, _) =>
        {
            var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            src.AddHook(WndProc);
            if (!RegisterHotKey(src.Handle, HOTKEY_ID, 0, VK_F9))
                StatusText.Text = "F9 está ocupada por otra app · usa el botón";
        };
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID) { handled = true; _ = Toggle(); }
        return IntPtr.Zero;
    }

    void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }

    async void RecBtn_Click(object sender, RoutedEventArgs e) => await Toggle();

    async Task StopIfRecording() { if (_want) await Toggle(); }

    async Task Toggle()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!_want)
            {
                SaveSettings();
                StatusText.Text = "Iniciando…";
                try { await _rec.StartAsync(_s); }
                catch (Exception ex)
                {
                    StatusText.Text = "Error al iniciar";
                    if (IsVisible)
                        System.Windows.MessageBox.Show(ex.Message, "VAL Rec", MessageBoxButton.OK, MessageBoxImage.Error);
                    else   // iniciado con F9 dentro de un juego: no robarle el foco
                        _tray.ShowBalloonTip(4000, "VAL Rec", "No se pudo iniciar. Registro: %AppData%\\ValRec\\last.log", Forms.ToolTipIcon.Warning);
                    return;
                }
                _t0 = DateTime.Now; _tick.Start(); _want = true; _part = 1; _fails = 0;
                SetUi(true);
                WindowState = WindowState.Minimized; Hide();   // que la ventana no salga en el video
                _tray.ShowBalloonTip(1500, "VAL Rec", "Grabando · F9 para detener", Forms.ToolTipIcon.None);
            }
            else
            {
                StatusText.Text = "Guardando…";
                _want = false;
                await _rec.StopAsync();
                _tick.Stop();
                SetUi(false);
                StatusText.Text = "Guardado: " + System.IO.Path.GetFileName(_rec.OutputPath);
                _tray.ShowBalloonTip(2500, "VAL Rec", "Video guardado", Forms.ToolTipIcon.None);
                if (!IsVisible) { /* queda en la bandeja; el aviso basta */ }
            }
        }
        finally { _busy = false; }
    }

    void SetUi(bool rec)
    {
        FpsBox.IsEnabled = QualityBox.IsEnabled = AudioCheck.IsEnabled = !rec;
        if (!rec) TimerText.Text = "00:00:00";
        _tray.Text = rec ? "VAL Rec · grabando" : "VAL Rec";
    }

    void UpdateTimer()
    {
        var t = DateTime.Now - _t0;
        TimerText.Text = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        StatusText.Text = "● Grabando · F9 detiene";
        if (_want && !_rec.IsRecording && !_busy) _ = Resume();
    }

    // La captura se cortó sola (p. ej. un juego cambió el modo de pantalla): seguir en un archivo nuevo.
    // Nunca se muestra la ventana: le quitaría el foco al juego.
    async Task Resume()
    {
        _busy = true;
        try
        {
            await Task.Delay(_fails == 0 ? 1000 : 2000);   // dejar que el cambio de pantalla termine
            if (!_want) return;
            _part++;
            await _rec.StartAsync(_s, _part);
            _fails = 0;
        }
        catch
        {
            if (++_fails >= 30)   // ~1 minuto reintentando antes de rendirse
            {
                _want = false; _tick.Stop(); SetUi(false);
                StatusText.Text = "La grabación se detuvo";
                _tray.ShowBalloonTip(4000, "VAL Rec", "La captura falló. Registro: %AppData%\\ValRec\\last.log", Forms.ToolTipIcon.Warning);
            }
        }
        finally { _busy = false; }
    }

    void SaveSettings()
    {
        _s.Fps = FpsBox.SelectedIndex == 1 ? 60 : 30;
        _s.Mbps = Qualities[Math.Max(0, QualityBox.SelectedIndex)].Item2;
        _s.Audio = AudioCheck.IsChecked == true;
        _s.Save();
    }

    void Editor_Click(object sender, RoutedEventArgs e) { SaveSettings(); new EditorWindow(_s) { Owner = this }.Show(); }

    void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        using var d = new Forms.FolderBrowserDialog { SelectedPath = _s.Folder };
        if (d.ShowDialog() == Forms.DialogResult.OK) { _s.Folder = d.SelectedPath; FolderBox.Text = _s.Folder; _s.Save(); }
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(_s.Folder);
        Process.Start("explorer.exe", _s.Folder);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exit && _want) { e.Cancel = true; Hide(); return; }   // seguir en bandeja
        SaveSettings();
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID);
        _tray.Visible = false; _tray.Dispose();
        base.OnClosing(e);
    }
}
