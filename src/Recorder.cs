using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using NAudio.Wave;

namespace ValRec;

/// <summary>
/// Controla ffmpeg: captura de pantalla + codificador por hardware. El audio del sistema (WASAPI loopback)
/// se le pasa por una tubería con nombre para que stdin quede libre para el "q" de parada limpia.
/// </summary>
public sealed class Recorder
{
    Process? _ff;
    NamedPipeServerStream? _pipe;
    WasapiLoopbackCapture? _cap;
    readonly StringBuilder _log = new();

    public string? OutputPath { get; private set; }
    public bool IsRecording => _ff is { HasExited: false };
    public string LastLog { get { lock (_log) return _log.ToString(); } }

    public static string FfmpegPath => Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);

    /// <summary>Detecta el fabricante de la GPU con la API de pantallas de Windows ("amd", "nvidia", "intel" o "").</summary>
    public static string DetectGpu()
    {
        string found = "";
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                var dd = new DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                var d = dd.DeviceString ?? "";
                if (d.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "nvidia";
                if (d.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                    d.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) found = "amd";
                else if (found == "" && d.Contains("Intel", StringComparison.OrdinalIgnoreCase)) found = "intel";
            }
        }
        catch { }
        return found;
    }

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ValRec", "last.log");

    string _baseName = "";

    /// <summary>Empieza un segmento. Usa el método elegido; si ffmpeg muere al arrancar con WGC, reintenta con DXGI.</summary>
    public async Task StartAsync(Settings s, int part = 1)
    {
        string method = s.Capture == "dxgi" ? "dxgi" : "wgc";
        try { await StartCoreAsync(s, part, method); }
        catch (InvalidOperationException) when (method == "wgc")
        {
            try { File.AppendAllText(LogPath, "--- WGC falló al arrancar, reintentando con DXGI\n"); } catch { }
            await StartCoreAsync(s, part, "dxgi");
        }
    }

    async Task StartCoreAsync(Settings s, int part, string method)
    {
        if (IsRecording) return;
        Cleanup();   // restos de un segmento anterior que murió
        if (!File.Exists(FfmpegPath))
            throw new FileNotFoundException("Falta tools\\ffmpeg.exe junto a la aplicación.");

        Directory.CreateDirectory(s.Folder);
        if (part == 1) _baseName = $"VALRec_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        OutputPath = Path.Combine(s.Folder, part == 1 ? _baseName + ".mp4" : $"{_baseName}_parte{part}.mp4");
        lock (_log) _log.Clear();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (part == 1) File.WriteAllText(LogPath, "");
            File.AppendAllText(LogPath, $"--- {DateTime.Now:HH:mm:ss} segmento {part}: {OutputPath}\n");
        }
        catch { }

        int fps = s.Fps, mbps = s.Mbps;
        string gpu = DetectGpu();
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "warning", "-y" }) psi.ArgumentList.Add(a);
        if (s.Audio) { psi.ArgumentList.Add("-copyts"); psi.ArgumentList.Add("-start_at_zero"); }

        string fc;
        string[] enc;
        bool d3d = method == "dxgi";
        // La grabación va SIN filtros (lo más ligero y estable). Los colores se aplican después en el editor.
        // Se entrega BGRA tal cual: el codificador por hardware convierte en GPU (nada de swscale en CPU).
        // WGC (Windows Graphics Capture): no pierde el acceso a la pantalla como DXGI y reescala solo si el juego
        // cambia la resolución (resize_mode=scale = estirado, igual que el filtro «Escala» de OBS).
        // Solo entrega fotogramas cuando algo cambia, así que "fps=" completa a ritmo constante.
        // Video y audio con el MISMO reloj (hora del sistema): así ffmpeg los alinea solo al arrancar, aunque el audio
        // tarde en llegar (el loopback no entrega nada mientras el sistema está en silencio).
        const string Clock = "setpts=RTCTIME/(TB*1000000)";
        string src = method == "wgc"
            ? $"gfxcapture=monitor_idx=0:max_framerate={fps}:resize_mode=scale:scale_mode=bicubic:capture_cursor=true,hwdownload,format=bgra,{Clock},fps={fps}"
            : $"ddagrab=0:framerate={fps},hwdownload,format=bgra,{Clock}";
        string enc264 = gpu switch { "amd" => "h264_amf", "nvidia" => "h264_nvenc", "intel" => "h264_qsv", _ => "libx264" };
        switch (gpu)
        {
            case "amd":
                fc = src + "[v]";
                // Con lowlatency+vbr_latency el codificador se queda sin bits en cada fotograma clave (cada 2 s) y ese fotograma
                // sale con bloques y manchas (el "bup" periódico). vbr_peak con búfer de 2 s le da presupuesto a los I-frames.
                enc = new[] { "-c:v", enc264, "-usage", "transcoding", "-rc", "vbr_peak", "-quality", "speed",
                              "-b:v", $"{mbps}M", "-maxrate", $"{mbps * 2}M", "-bufsize", $"{mbps * 2}M" };
                break;
            case "nvidia":
                fc = src + "[v]";
                enc = new[] { "-c:v", enc264, "-preset", "p3", "-b:v", $"{mbps}M" };
                break;
            case "intel":
                fc = src + "[v]";
                enc = new[] { "-c:v", enc264, "-b:v", $"{mbps}M" };
                break;
            default:
                fc = src + ",format=yuv420p[v]";
                enc = new[] { "-c:v", enc264, "-preset", "ultrafast", "-crf", "23" };
                break;
        }
        if (d3d) { psi.ArgumentList.Add("-init_hw_device"); psi.ArgumentList.Add("d3d11va"); }

        if (s.Audio)
        {
            NAudio.CoreAudioApi.MMDevice? chosen = null;
            if (!string.IsNullOrEmpty(s.AudioDevice))
            {
                try
                {
                    using var en0 = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    chosen = en0.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active)
                        .FirstOrDefault(d => d.FriendlyName == s.AudioDevice);
                }
                catch { }
            }
            _cap = chosen != null ? new WasapiLoopbackCapture(chosen) : new WasapiLoopbackCapture();
            _chosenName = chosen?.FriendlyName;
            var wf = _cap.WaveFormat;
            string pipeName = "valrec_" + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat || wf.BitsPerSample == 32;
            string fmt = isFloat ? "f32le" : "s16le";
            foreach (var a in new[] { "-use_wallclock_as_timestamps", "1", "-f", fmt,
                "-ar", wf.SampleRate.ToString(), "-ac", wf.Channels.ToString(),
                "-i", @"\\.\pipe\" + pipeName }) psi.ArgumentList.Add(a);
        }

        psi.ArgumentList.Add("-filter_complex"); psi.ArgumentList.Add(fc);
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[v]");
        if (s.Audio)
            foreach (var a in new[] { "-map", "0:a", "-af", "aresample=async=1",
                "-c:a", "aac", "-b:a", "160k" }) psi.ArgumentList.Add(a);
        foreach (var a in enc) psi.ArgumentList.Add(a);
        // MP4 fragmentado: si ffmpeg o el PC se cortan, el archivo sigue siendo reproducible.
        // Fotograma clave cada 2 s: el archivo se va escribiendo por fragmentos (sin esto, pantalla estática = nada en disco).
        foreach (var a in new[] { "-g", (fps * 2).ToString(), "-movflags", "frag_keyframe+empty_moov+default_base_moof" }) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(OutputPath);

        _ff = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar ffmpeg.");
        _ff.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_log) _log.AppendLine(e.Data);
            try { File.AppendAllText(LogPath, e.Data + "\n"); } catch { }
        };
        _ff.BeginErrorReadLine();
        try { _ff.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }

        if (s.Audio && _pipe != null && _cap != null)
        {
            using var cts = new CancellationTokenSource(6000);
            try { await _pipe.WaitForConnectionAsync(cts.Token); }
            catch
            {
                var l = LastLog; try { _ff.Kill(true); } catch { } Cleanup();
                throw new InvalidOperationException("ffmpeg no pudo conectarse al audio.\n" + l);
            }
            var pipe = _pipe;
            var wf2 = _cap.WaveFormat;
            int blockAlign = wf2.BlockAlign, bytesPerSec = wf2.AverageBytesPerSecond;
            var wlock = new object();
            long written = 0, realBytes = 0;
            int fills = 0; long maxFillBytes = 0;
            float peak = 0;
            var clock = Stopwatch.StartNew();
            string devName = "?";
            try { if (_chosenName != null) devName = _chosenName + " (elegido)"; else { using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator(); devName = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia).FriendlyName + " (predeterminado)"; } } catch { }
            try { File.AppendAllText(LogPath, $"audio: dispositivo={devName} formato={wf2.Encoding} {wf2.SampleRate} Hz {wf2.Channels} canales\n"); } catch { }
            _audioSummary = () =>
            {
                double real = realBytes / (double)bytesPerSec, total = written / (double)bytesPerSec;
                try { File.AppendAllText(LogPath, $"audio: real={real:0.0}s relleno={total - real:0.0}s pico={peak:0.000} rellenos={fills} mayor={maxFillBytes * 1000.0 / bytesPerSec:0}ms\n"); } catch { }
            };
            _fillStop = false;
            // Cola + hilo escritor propio: el callback de captura NUNCA se bloquea escribiendo en la tubería (si ffmpeg
            // se atrasa con el PC cargado, se perdía audio o llegaba tarde y quedaba un hueco). El orden se mantiene
            // porque tanto el audio real como el relleno se encolan dentro del mismo lock.
            var queue = new System.Collections.Concurrent.BlockingCollection<byte[]>(new System.Collections.Concurrent.ConcurrentQueue<byte[]>());
            _writerQueue = queue;
            _writerTask = Task.Factory.StartNew(() =>
            {
                try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }
                try
                {
                    foreach (var chunk in queue.GetConsumingEnumerable())
                        if (pipe.IsConnected) pipe.Write(chunk, 0, chunk.Length);
                }
                catch { }
            }, TaskCreationOptions.LongRunning);
            // El loopback de Windows NO entrega nada mientras el sistema está en silencio, y ffmpeg se desordena si el
            // audio llega con huecos largos (pierde todo lo que suene después). Por eso el flujo es continuo: pero solo se
            // rellena si pasan >300 ms sin datos (silencio real). Con un umbral corto (40 ms) el retraso normal de entrega
            // se tomaba por silencio y se metían huecos de 30–100 ms EN MEDIO del sonido (cortes y chasquidos).
            _cap.DataAvailable += (_, e) =>
            {
                try
                {
                    if (e.BytesRecorded <= 0) return;
                    var copy = new byte[e.BytesRecorded];
                    Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
                    lock (wlock)
                    {
                        if (queue.IsAddingCompleted) return;
                        queue.Add(copy); written += copy.Length; realBytes += copy.Length;
                        if (wf2.Encoding == WaveFormatEncoding.IeeeFloat || wf2.BitsPerSample == 32)
                            for (int i = 0; i + 4 <= copy.Length; i += 64) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(copy, i)));
                    }
                }
                catch { }
            };
            _cap.StartRecording();
            _fillTask = Task.Run(async () =>
            {
                while (!_fillStop)
                {
                    await Task.Delay(20);
                    try
                    {
                        lock (wlock)
                        {
                            if (queue.IsAddingCompleted) break;
                            long expected = (long)(clock.Elapsed.TotalSeconds * bytesPerSec) / blockAlign * blockAlign;
                            long gap = expected - written;
                            if (gap > bytesPerSec * 0.30)
                            {
                                long n = gap / blockAlign * blockAlign;
                                queue.Add(new byte[n]); written += n; fills++; if (n > maxFillBytes) maxFillBytes = n;
                            }
                        }
                    }
                    catch { }
                }
            });
        }

        // Si ffmpeg muere al arrancar (GPU o codec no soportado) avisamos con su log.
        await Task.Delay(1500);
        if (_ff.HasExited)
        {
            var log = LastLog; Cleanup();
            throw new InvalidOperationException("ffmpeg se cerró al empezar.\n" + log);
        }
    }

    public async Task StopAsync()
    {
        var ff = _ff;
        if (ff == null) return;
        try { if (!ff.HasExited) { ff.StandardInput.WriteLine("q"); ff.StandardInput.Flush(); } } catch { }
        _fillStop = true;
        try { _cap?.StopRecording(); } catch { }
        try { if (_fillTask != null) await _fillTask; } catch { }
        try { _audioSummary?.Invoke(); } catch { }
        try { var q = _writerQueue; if (q != null) { q.CompleteAdding(); } if (_writerTask != null) await Task.WhenAny(_writerTask, Task.Delay(3000)); } catch { }
        try { _pipe?.Dispose(); } catch { }   // EOF en el audio: si no, ffmpeg espera datos y no finaliza el MP4
        await Task.Run(() => { if (!ff.WaitForExit(10000)) { try { ff.Kill(true); } catch { } } });
        Cleanup();
    }

    volatile bool _fillStop;
    string? _chosenName;
    Action? _audioSummary;
    Task? _fillTask;
    Task? _writerTask;
    System.Collections.Concurrent.BlockingCollection<byte[]>? _writerQueue;

    void Cleanup()
    {
        _fillStop = true;
        try { _cap?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        try { _ff?.Dispose(); } catch { }
        _cap = null; _pipe = null; _ff = null;
    }
}
