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

    public async Task StartAsync(Settings s, int part = 1)
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

        string fc;
        string[] enc;
        bool d3d = true;
        // La grabación va SIN filtros (lo más ligero y estable). Los colores se aplican después en el editor.
        // Se entrega BGRA tal cual: el codificador por hardware convierte en GPU (nada de swscale en CPU).
        string enc264 = gpu switch { "amd" => "h264_amf", "nvidia" => "h264_nvenc", "intel" => "h264_qsv", _ => "libx264" };
        switch (gpu)
        {
            case "amd":
                fc = $"ddagrab=0:framerate={fps},hwdownload,format=bgra[v]";
                enc = new[] { "-c:v", enc264, "-usage", "lowlatency", "-rc", "vbr_latency", "-quality", "speed", "-b:v", $"{mbps}M" };
                break;
            case "nvidia":
                fc = $"ddagrab=0:framerate={fps}[v]";
                enc = new[] { "-c:v", enc264, "-preset", "p3", "-b:v", $"{mbps}M" };
                break;
            case "intel":
                fc = $"ddagrab=0:framerate={fps},hwdownload,format=bgra[v]";
                enc = new[] { "-c:v", enc264, "-b:v", $"{mbps}M" };
                break;
            default:
                fc = $"ddagrab=0:framerate={fps},hwdownload,format=bgra,format=yuv420p[v]";
                enc = new[] { "-c:v", enc264, "-preset", "ultrafast", "-crf", "23" };
                break;
        }
        if (d3d) { psi.ArgumentList.Add("-init_hw_device"); psi.ArgumentList.Add("d3d11va"); }

        if (s.Audio)
        {
            _cap = new WasapiLoopbackCapture();
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
            foreach (var a in new[] { "-map", "0:a", "-af", "aresample=async=1:first_pts=0",
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
            _cap.DataAvailable += (_, e) =>
            {
                try { if (pipe.IsConnected && e.BytesRecorded > 0) pipe.Write(e.Buffer, 0, e.BytesRecorded); }
                catch { }
            };
            _cap.StartRecording();
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
        try { _cap?.StopRecording(); } catch { }
        try { _pipe?.Dispose(); } catch { }   // EOF en el audio: si no, ffmpeg espera datos y no finaliza el MP4
        await Task.Run(() => { if (!ff.WaitForExit(10000)) { try { ff.Kill(true); } catch { } } });
        Cleanup();
    }

    void Cleanup()
    {
        try { _cap?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        try { _ff?.Dispose(); } catch { }
        _cap = null; _pipe = null; _ff = null;
    }
}
