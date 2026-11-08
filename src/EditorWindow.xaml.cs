using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ValRec;

/// <summary>
/// Carga un video ya grabado, deja ajustar los colores (fórmula de OBS) con vista previa de un fotograma
/// y lo exporta con el codificador por hardware. El trabajo pesado ocurre aquí, no mientras se juega.
/// </summary>
public partial class EditorWindow : Window
{
    readonly Settings _s;
    ColorAdjust _fx;
    string? _video;
    double _duration;
    bool _loading;
    Process? _export;
    bool _cancelled;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly SemaphoreSlim _gate = new(1, 1);
    int _ticket;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "ValRec");
    readonly List<(Slider s, TextBlock v, Action<double> set, Func<double> get, string fmt)> _rows = new();

    static readonly (string, double, double, string)[] Defs =
    {
        ("Gamma", -3, 3, nameof(ColorAdjust.Gamma)),
        ("Contraste", -4, 4, nameof(ColorAdjust.Contrast)),
        ("Luminosidad", -1, 1, nameof(ColorAdjust.Brightness)),
        ("Saturación", -1, 5, nameof(ColorAdjust.Saturation)),
        ("Cambio de tonalidad", -180, 180, nameof(ColorAdjust.Hue)),
        ("Nitidez", 0, 3, nameof(ColorAdjust.Sharpness)),
        ("Desenfoque de movimiento", 0, 1, nameof(ColorAdjust.MotionBlur)),
    };

    public EditorWindow(Settings s)
    {
        InitializeComponent();
        _s = s;
        _fx = s.Color.Clone();
        Directory.CreateDirectory(_tmp);

        QualityBox.ItemsSource = new[] { "Calidad: 10 Mbps", "Calidad: 20 Mbps", "Calidad: 35 Mbps", "Calidad: 60 Mbps" };
        QualityBox.SelectedIndex = s.ExportMbps switch { <= 10 => 0, <= 20 => 1, <= 35 => 2, _ => 3 };

        BlurMode.ItemsSource = new[] { "Desenfoque: rápido (mezcla de fotogramas)", "Desenfoque: calidad (interpolación, MUY lento)" };
        BlurMode.SelectedIndex = s.BlurQuality ? 1 : 0;
        BuildSliders();
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RefreshFxAsync(); };
        Closing += (_, _) => { lock (_chunks) foreach (var c in _chunks) { try { c.Kill(true); } catch { } } _s.Color = _fx.Clone(); _s.ExportMbps = Mbps(); _s.BlurQuality = BlurMode.SelectedIndex == 1; _s.Save(); try { _export?.Kill(true); } catch { } };
    }

    int Mbps() => QualityBox.SelectedIndex switch { 0 => 10, 1 => 20, 2 => 35, _ => 60 };

    // ---------- controles ----------
    void BuildSliders()
    {
        foreach (var (name, min, max, prop) in Defs)
        {
            var pi = typeof(ColorAdjust).GetProperty(prop)!;
            var head = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var val = new TextBlock { Width = 52, TextAlignment = TextAlignment.Right, Foreground = System.Windows.Media.Brushes.Gray };
            DockPanel.SetDock(val, Dock.Right);
            head.Children.Add(val);
            head.Children.Add(new TextBlock { Text = name });
            var sl = new Slider { Minimum = min, Maximum = max, SmallChange = 0.01, LargeChange = 0.1, TickFrequency = 0.01 };
            string fmt = prop == nameof(ColorAdjust.Hue) ? "0" : "0.00";
            Action<double> set = v => pi.SetValue(_fx, v);
            Func<double> get = () => (double)pi.GetValue(_fx)!;
            sl.ValueChanged += (_, e) =>
            {
                if (_loading) return;
                var v = Math.Round(e.NewValue, prop == nameof(ColorAdjust.Hue) ? 0 : 2);
                set(v); val.Text = v.ToString(fmt, CultureInfo.CurrentCulture);
                _debounce.Stop(); _debounce.Start();
            };
            sl.MouseDoubleClick += (_, _) => sl.Value = 0;
            SlidersPanel.Children.Add(head);
            SlidersPanel.Children.Add(sl);
            _rows.Add((sl, val, set, get, fmt));
        }
        SyncSliders();
    }

    void SyncSliders()
    {
        _loading = true;
        foreach (var r in _rows) { r.s.Value = r.get(); r.v.Text = r.get().ToString(r.fmt, CultureInfo.CurrentCulture); }
        _loading = false;
    }

    void Obs_Click(object sender, RoutedEventArgs e) { _fx = ColorAdjust.Obs(); SyncSliders(); _ = RefreshFxAsync(); }
    void ValEdit_Click(object sender, RoutedEventArgs e) { _fx = ColorAdjust.ValEdit(); SyncSliders(); _ = RefreshFxAsync(); }
    void Reset_Click(object sender, RoutedEventArgs e) { _fx = new ColorAdjust(); SyncSliders(); _ = RefreshFxAsync(); }

    // ---------- cargar video ----------
    async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Videos|*.mp4;*.mkv;*.mov;*.avi;*.webm|Todos los archivos|*.*",
            InitialDirectory = Directory.Exists(_s.Folder) ? _s.Folder : null
        };
        if (dlg.ShowDialog(this) != true) return;
        _video = dlg.FileName;
        PathText.Text = _video;
        Status.Text = "Leyendo video…";
        var info = await Task.Run(() => RunFfmpeg(new[] { "-hide_banner", "-i", _video }));
        var m = Regex.Match(info, @"Duration:\s*(\d+):(\d+):([\d.]+)");
        if (!m.Success) { Status.Text = "No se pudo leer ese archivo como video"; _video = null; return; }
        _duration = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
                    double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        TimeSlider.IsEnabled = true; TimeSlider.Maximum = Math.Max(0.1, _duration - 0.2);
        ExportBtn.IsEnabled = true;
        TimeSlider.Value = Math.Min(1, TimeSlider.Maximum);
        Time_Changed(null!, null!);   // por si el valor no cambió
        Status.Text = "Mueve la barra para elegir un fotograma de prueba y ajusta los colores";
    }

    void Time_Changed(object sender, RoutedPropertyChangedEventArgs<double>? e)
    {
        var t = TimeSpan.FromSeconds(TimeSlider.Value);
        TimeText.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00} / {TimeSpan.FromSeconds(_duration):mm\\:ss}";
        _debounce.Stop(); _debounce.Start();
        _origDirty = true;
    }
    bool _origDirty = true;

    // ---------- vista previa ----------
    async Task RefreshFxAsync()
    {
        if (_video == null) return;
        int my = ++_ticket;
        await _gate.WaitAsync();
        try
        {
            if (my != _ticket) return;   // llegó un cambio más nuevo
            string video = _video!; double t = TimeSlider.Value; var tmp = _fx.Clone(); string fx = tmp.ToFilter(); bool needOrig = _origDirty;
            string o = Path.Combine(_tmp, "orig.png"), f = Path.Combine(_tmp, "fx.png");
            await Task.Run(() =>
            {
                string[] baseArgs(string vf, string outp) => new[] { "-hide_banner", "-loglevel", "error", "-y", "-ss",
                    t.ToString("0.###", CultureInfo.InvariantCulture), "-i", video, "-frames:v", "1", "-vf", vf, outp };
                if (needOrig) RunFfmpeg(baseArgs("scale=720:-2,format=rgb24", o));
                string tf = tmp.TemporalFilter(_s.Fps);
                string chain = (tf == "" ? "" : tf + ",") + "scale=720:-2,format=bgra," + (fx == "" ? "" : fx + ",") + "format=rgb24";
                if (tf == "") RunFfmpeg(baseArgs(chain, f));
                else   // el desenfoque necesita fotogramas previos: se procesan varios y se queda el último
                    RunFfmpeg(new[] { "-hide_banner", "-loglevel", "error", "-y", "-ss", Math.Max(0, t - 0.1).ToString("0.###", CultureInfo.InvariantCulture),
                        "-i", video, "-vf", chain, "-frames:v", "6", "-update", "1", f });
            });
            _origDirty = false;
            if (File.Exists(o)) OrigImg.Source = Load(o);
            if (File.Exists(f)) FxImg.Source = Load(f);
        }
        catch (Exception ex) { Status.Text = "Vista previa: " + ex.Message; }
        finally { _gate.Release(); }
    }

    static BitmapImage Load(string path)
    {
        var b = new BitmapImage();
        b.BeginInit();
        b.CacheOption = BitmapCacheOption.OnLoad;
        b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        b.UriSource = new Uri(path);
        b.EndInit(); b.Freeze();
        return b;
    }

    static string RunFfmpeg(string[] args)
    {
        var psi = new ProcessStartInfo(Recorder.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return err;
    }

    // ---------- exportar ----------
    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_video == null || _export != null) return;
        string dir = Path.GetDirectoryName(_video)!, name = Path.GetFileNameWithoutExtension(_video);
        string outp = Path.Combine(dir, name + "_color.mp4");
        for (int i = 2; File.Exists(outp); i++) outp = Path.Combine(dir, $"{name}_color{i}.mp4");

        string fx = _fx.ToFilter();
        bool hq = _fx.MotionBlur > 1e-4 && BlurMode.SelectedIndex == 1;
        string tf = _fx.TemporalFilter(_s.Fps, hq);
        string vf = (tf == "" ? "" : tf + ",") + (fx == ""
            ? "format=nv12"
            : "scale=in_range=auto,format=bgra," + fx + ",scale=out_color_matrix=bt709:out_range=tv,format=nv12");
        int mb = Mbps();
        string gpu = Recorder.DetectGpu();
        string[] enc = gpu switch
        {
            "amd" => new[] { "-c:v", "h264_amf", "-usage", "transcoding", "-quality", "quality", "-rc", "vbr_peak", "-b:v", $"{mb}M", "-maxrate", $"{mb * 2}M" },
            "nvidia" => new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-b:v", $"{mb}M" },
            "intel" => new[] { "-c:v", "h264_qsv", "-b:v", $"{mb}M" },
            _ => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "18" },
        };

        if (hq) { await ExportChunkedAsync(outp, vf, enc); return; }

        var psi = new ProcessStartInfo(Recorder.FfmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-progress", "pipe:1", "-nostats", "-i", _video,
            "-map", "0:v:0", "-map", "0:a?", "-vf", vf }) psi.ArgumentList.Add(a);
        foreach (var a in enc) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-c:a", "copy", "-movflags", "+faststart", outp }) psi.ArgumentList.Add(a);

        _cancelled = false;
        ExportBtn.IsEnabled = false; OpenBtn.IsEnabled = false; CancelBtn.Visibility = Visibility.Visible;
        Progress.Value = 0; Status.Text = "Exportando…";
        var errText = new System.Text.StringBuilder();
        try
        {
            _export = Process.Start(psi)!;
            try { _export.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var p = _export;
            p.ErrorDataReceived += (_, d) => { if (d.Data != null) lock (errText) errText.AppendLine(d.Data); };
            p.BeginErrorReadLine();
            double dur = _duration; var sw = Stopwatch.StartNew();
            p.OutputDataReceived += (_, d) =>
            {
                if (d.Data == null || !d.Data.StartsWith("out_time_us=")) return;
                if (!double.TryParse(d.Data.AsSpan(12), NumberStyles.Float, CultureInfo.InvariantCulture, out var us) || us < 0) return;
                double done = us / 1e6, pct = Math.Min(100, done / dur * 100);
                double eta = done > 0.5 ? sw.Elapsed.TotalSeconds * (dur - done) / done : double.NaN;
                Dispatcher.BeginInvoke(() =>
                {
                    Progress.Value = pct;
                    Status.Text = $"Exportando… {pct:0}%" + (double.IsNaN(eta) ? "" : $" · faltan ~{TimeSpan.FromSeconds(eta):mm\\:ss}");
                });
            };
            p.BeginOutputReadLine();
            await Task.Run(() => p.WaitForExit());
            int code = p.ExitCode;
            if (_cancelled) { try { File.Delete(outp); } catch { } Status.Text = "Exportación cancelada"; }
            else if (code != 0 || !File.Exists(outp))
            {
                try { File.Delete(outp); } catch { }
                Status.Text = "Falló la exportación";
                string log; lock (errText) log = errText.ToString();
                System.Windows.MessageBox.Show(this, log.Length > 1200 ? log[^1200..] : log, "VAL Rec", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                Progress.Value = 100; Status.Text = "Listo: " + Path.GetFileName(outp);
                Process.Start("explorer.exe", "/select,\"" + outp + "\"");
            }
        }
        catch (Exception ex) { Status.Text = "Error: " + ex.Message; }
        finally
        {
            try { _export?.Dispose(); } catch { }
            _export = null;
            ExportBtn.IsEnabled = true; OpenBtn.IsEnabled = true; CancelBtn.Visibility = Visibility.Collapsed;
        }
    }


    // Modo calidad: la interpolación de ffmpeg usa un solo hilo, así que se parte el video en tramos
    // y se procesan varios a la vez; luego se unen sin recodificar y se agrega el audio original.
    readonly List<Process> _chunks = new();

    async Task ExportChunkedAsync(string outp, string vf, string[] enc)
    {
        int k = Math.Clamp(Environment.ProcessorCount - 1, 1, 6);
        double seg = Math.Max(5, Math.Ceiling(_duration / k));
        int count = (int)Math.Ceiling(_duration / seg);
        string work = Path.Combine(_tmp, "exp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var done = new double[count];
        var errs = new System.Text.StringBuilder();
        string video = _video!; double dur = _duration; var sw = Stopwatch.StartNew();

        _cancelled = false;
        ExportBtn.IsEnabled = false; OpenBtn.IsEnabled = false; CancelBtn.Visibility = Visibility.Visible;
        Progress.Value = 0; Status.Text = $"Exportando en {count} tramos en paralelo… (puede tardar mucho)";
        try
        {
            var tasks = new List<Task<int>>();
            for (int i = 0; i < count; i++)
            {
                int idx = i; double start = i * seg, len = Math.Min(seg, dur - start);
                var psi = new ProcessStartInfo(Recorder.FfmpegPath)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-progress", "pipe:1", "-nostats",
                    "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-t", len.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", video, "-an", "-vf", vf }) psi.ArgumentList.Add(a);
                foreach (var a in enc) psi.ArgumentList.Add(a);
                foreach (var a in new[] { "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
                    Path.Combine(work, $"seg{idx:000}.mp4") }) psi.ArgumentList.Add(a);

                var p = Process.Start(psi)!;
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                lock (_chunks) _chunks.Add(p);
                p.ErrorDataReceived += (_, d) => { if (d.Data != null) lock (errs) errs.AppendLine(d.Data); };
                p.OutputDataReceived += (_, d) =>
                {
                    if (d.Data == null || !d.Data.StartsWith("out_time_us=")) return;
                    if (!double.TryParse(d.Data.AsSpan(12), NumberStyles.Float, CultureInfo.InvariantCulture, out var us) || us < 0) return;
                    double total, pct;
                    lock (done)
                    {
                        done[idx] = Math.Min(len, us / 1e6); total = done.Sum(); pct = Math.Min(99, total / dur * 100);
                    }
                    double eta = total > 1 ? sw.Elapsed.TotalSeconds * (dur - total) / total : double.NaN;
                    Dispatcher.BeginInvoke(() =>
                    {
                        Progress.Value = pct;
                        Status.Text = $"Exportando… {pct:0}%" + (double.IsNaN(eta) ? "" : $" · faltan ~{TimeSpan.FromSeconds(eta):hh\\:mm\\:ss}");
                    });
                };
                p.BeginErrorReadLine(); p.BeginOutputReadLine();
                tasks.Add(Task.Run(() => { p.WaitForExit(); return p.ExitCode; }));
            }

            var codes = await Task.WhenAll(tasks);
            if (_cancelled) { Status.Text = "Exportación cancelada"; return; }
            if (codes.Any(c => c != 0)) throw new InvalidOperationException("Falló un tramo:\n" + Tail(errs));

            // unir tramos + audio original
            Status.Text = "Uniendo tramos…";
            string list = Path.Combine(work, "list.txt");
            File.WriteAllLines(list, Enumerable.Range(0, count)
                .Select(i => "file '" + Path.Combine(work, $"seg{i:000}.mp4").Replace("\\", "/") + "'"));
            string log = await Task.Run(() => RunFfmpeg(new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0",
                "-i", list, "-i", video, "-map", "0:v:0", "-map", "1:a?", "-c", "copy", "-movflags", "+faststart", outp }));
            if (!File.Exists(outp)) throw new InvalidOperationException("No se pudieron unir los tramos:\n" + log);

            Progress.Value = 100; Status.Text = "Listo: " + Path.GetFileName(outp);
            Process.Start("explorer.exe", "/select,\"" + outp + "\"");
        }
        catch (Exception ex)
        {
            try { File.Delete(outp); } catch { }
            Status.Text = "Falló la exportación";
            System.Windows.MessageBox.Show(this, ex.Message, "VAL Rec", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            lock (_chunks) { foreach (var p in _chunks) { try { p.Dispose(); } catch { } } _chunks.Clear(); }
            try { Directory.Delete(work, true); } catch { }
            ExportBtn.IsEnabled = true; OpenBtn.IsEnabled = true; CancelBtn.Visibility = Visibility.Collapsed;
        }
    }

    static string Tail(System.Text.StringBuilder sb) { string t; lock (sb) t = sb.ToString(); return t.Length > 1200 ? t[^1200..] : t; }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancelled = true;
        try { _export?.Kill(true); } catch { }
        lock (_chunks) foreach (var p in _chunks) { try { p.Kill(true); } catch { } }
    }
}
