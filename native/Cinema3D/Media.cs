using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Cinema3D;

/// <summary>Папка кинотеатра (там, где лежат tools\ffmpeg, movies, media).</summary>
public static class App
{
    public static readonly string Root = FindRoot();
    public static string Ffmpeg => Path.Combine(Root, "tools", "ffmpeg", "ffmpeg.exe");
    public static string Ffprobe => Path.Combine(Root, "tools", "ffmpeg", "ffprobe.exe");
    public static string YtDlp => Path.Combine(Root, "tools", "yt-dlp", "yt-dlp.exe");

    static string FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("C3D_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tools", "ffmpeg", "ffmpeg.exe"))) return d.FullName;
        return dir.FullName;
    }

    /// <summary>Запуск консольной программы без окна с перехватом вывода.</summary>
    public static Process Start(string exe, IEnumerable<string> args, bool binaryStdout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (!binaryStdout) psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }
}

public static class Log
{
    static readonly object Lock = new();
    static string FilePath => Path.Combine(App.Root, "Cinema3D.log");

    public static void Reset() { try { File.WriteAllText(FilePath, ""); } catch { } }

    public static void Write(string line)
    {
        lock (Lock)
        {
            try { File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine); } catch { }
        }
    }
}

public class AudioTrack
{
    public int Index;          // номер потока в файле (для ffmpeg -map 0:N)
    public string Codec = "", Lang = "", Title = "";
    public int Channels;

    public bool IsRussian => Lang is "rus" or "ru" ||
        Title.Contains("рус", StringComparison.OrdinalIgnoreCase) || Title.Contains("russian", StringComparison.OrdinalIgnoreCase);

    public override string ToString()
    {
        var lang = Lang switch { "rus" or "ru" => "Русский", "eng" or "en" => "English", "" or "und" => "", _ => Lang };
        var parts = new List<string>();
        if (lang != "") parts.Add(lang);
        if (Title != "") parts.Add(Title);
        parts.Add($"{Codec} {Channels}кан.");
        return string.Join(" · ", parts);
    }
}

public class Movie
{
    public string Path = "", Name = "", Folder = "";
    public long Size;
    public int Width, Height, VideoIndex = -1;
    public double Sar = 1, Duration, Fps = 24;
    public string FpsText = "24", VideoCodec = "";
    public List<AudioTrack> Audio = new();
    public bool Probed;
    public string Error = "";

    // ---- видео по ссылке (YouTube, VK…): адреса потоков от yt-dlp ----
    public string Url, VideoUrl, AudioUrl, UserAgent = "", Referer = "";
    public bool IsStream => VideoUrl != null;

    public string Key => IsStream ? "url|" + Url : Name + "|" + Size;
    public double DisplayWidth => Width * Sar;

    public string VideoInput => VideoUrl ?? Path;
    public string VideoMap => IsStream ? "0:v:0" : $"0:{VideoIndex}";
    public string AudioInput => AudioUrl ?? VideoUrl ?? Path;
    public string AudioMap(int streamIndex) => IsStream ? "0:a:0" : $"0:{streamIndex}";

    /// <summary>Параметры ffmpeg перед -i: для сетевых потоков — заголовки и переподключение.</summary>
    public IEnumerable<string> InputArgs()
    {
        if (!IsStream) yield break;
        if (UserAgent != "") { yield return "-user_agent"; yield return UserAgent; }
        if (Referer != "") { yield return "-headers"; yield return $"Referer: {Referer}\r\n"; }
        foreach (var a in new[] { "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_on_network_error", "1",
                                  "-reconnect_delay_max", "5", "-rw_timeout", "20000000" })
            yield return a;
    }

    /// <summary>
    /// Ролик по ссылке: yt-dlp узнаёт адреса видео (до 1080p, по возможности H.264) и звука.
    /// Платный и защищённый контент не поддерживается.
    /// </summary>
    public static Movie FromUrl(string url, out string error)
    {
        error = "";
        if (!File.Exists(App.YtDlp)) { error = "Не найден загрузчик tools\\yt-dlp\\yt-dlp.exe"; return null; }
        try
        {
            using var p = App.Start(App.YtDlp, new[] { "-J", "--no-playlist", "--no-warnings",
                "-f", "bv*[height<=1080][vcodec^=avc1]+ba[ext=m4a]/bv*[height<=1080][vcodec^=avc1]+ba/bv*[height<=1080]+ba/b[height<=1080]/b",
                url }, false);
            var errText = new StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errText) errText.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            string json = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(90000)) { try { p.Kill(); } catch { } error = "сайт долго не отвечает"; return null; }
            if (p.ExitCode != 0 || json.Trim() == "")
            {
                lock (errText) error = errText.ToString().Trim();
                if (error == "") error = "не удалось получить видео по ссылке";
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var m = new Movie { Url = url, Folder = "ссылка", Sar = 1 };
            m.Name = Str(r, "title");
            if (m.Name == "") m.Name = url;
            if (r.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number) m.Duration = du.GetDouble();

            JsonElement? video = null, audio = null;
            if (r.TryGetProperty("requested_formats", out var rf) && rf.ValueKind == JsonValueKind.Array)
                foreach (var f in rf.EnumerateArray())
                {
                    if (Str(f, "vcodec") is var vc && vc != "" && vc != "none") video ??= f;
                    else if (Str(f, "acodec") is var ac && ac != "" && ac != "none") audio ??= f;
                }
            if (video == null) { video = r; if (Str(r, "acodec") is var ac2 && ac2 != "" && ac2 != "none") audio = r; }

            var v = video.Value;
            m.VideoUrl = Str(v, "url");
            if (m.VideoUrl == "") { error = "сайт не отдал адрес видео (возможно, ролик платный или защищён)"; return null; }
            m.Width = v.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 1920;
            m.Height = v.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : 1080;
            if (v.TryGetProperty("fps", out var fps) && fps.ValueKind == JsonValueKind.Number && fps.GetDouble() > 0)
            { m.Fps = fps.GetDouble(); m.FpsText = m.Fps.ToString("0.###", CultureInfo.InvariantCulture); }
            m.VideoCodec = Str(v, "vcodec");
            if (v.TryGetProperty("http_headers", out var hh))
            {
                m.UserAgent = Str(hh, "User-Agent");
                m.Referer = Str(hh, "Referer");
            }
            if (audio != null)
            {
                var a = audio.Value;
                m.AudioUrl = Str(a, "url");
                if (m.AudioUrl == "") m.AudioUrl = null;
                m.Audio.Add(new AudioTrack { Index = 0, Codec = Str(a, "acodec").Split('.')[0], Lang = Str(a, "language").ToLowerInvariant(),
                    Title = "звук ролика", Channels = a.TryGetProperty("audio_channels", out var ch) && ch.ValueKind == JsonValueKind.Number ? ch.GetInt32() : 2 });
            }
            m.ProbeStream();
            m.Probed = true;
            if (Stereo.DetectByName(m.Name) == null) m.DetectContent();
            return m;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>Формат, определённый по картинке (null — не удалось).</summary>
    public StereoLayout? ContentLayout;
    public string ContentScores = "";

    /// <summary>Формат для режима «Авто»: метка в названии, иначе по картинке, иначе по пропорциям.</summary>
    public StereoLayout AutoLayout => Stereo.DetectByName(Name) ?? ContentLayout ?? Stereo.Detect(Name, DisplayWidth, Height);

    public int DefaultAudio() => (Audio.FirstOrDefault(a => a.IsRussian) ?? Audio.FirstOrDefault())?.Index ?? -1;

    public static Movie FromFile(string path, string folder)
    {
        var fi = new FileInfo(path);
        return new Movie { Path = fi.FullName, Name = fi.Name, Folder = folder, Size = fi.Length };
    }

    /// <summary>Читает параметры файла через ffprobe.</summary>
    public void Probe()
    {
        try
        {
            using var p = App.Start(App.Ffprobe, new[] { "-v", "error", "-show_entries",
                "stream=index,codec_type,codec_name,width,height,sample_aspect_ratio,avg_frame_rate,r_frame_rate,channels:stream_disposition=attached_pic:stream_tags=language,title:format=duration",
                "-of", "json", Path }, false);
            string json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var du))
                double.TryParse(du.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out Duration);
            foreach (var s in root.GetProperty("streams").EnumerateArray())
            {
                string type = Str(s, "codec_type");
                bool pic = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && !pic && VideoIndex < 0)
                {
                    VideoIndex = s.GetProperty("index").GetInt32();
                    VideoCodec = Str(s, "codec_name");
                    Width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    Height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    var sp = Str(s, "sample_aspect_ratio").Split(':');
                    if (sp.Length == 2 && double.TryParse(sp[0], out var a) && double.TryParse(sp[1], out var b) && a > 0 && b > 0) Sar = a / b;
                    foreach (var key in new[] { "avg_frame_rate", "r_frame_rate" })
                    {
                        var fr = Str(s, key).Split('/');
                        if (fr.Length == 2 && double.TryParse(fr[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                            && double.TryParse(fr[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && n > 0 && d > 0 && n / d < 200)
                        { Fps = n / d; FpsText = $"{fr[0]}/{fr[1]}"; break; }
                    }
                }
                else if (type == "audio")
                {
                    var t = new AudioTrack { Index = s.GetProperty("index").GetInt32(), Codec = Str(s, "codec_name") };
                    if (s.TryGetProperty("channels", out var ch)) t.Channels = ch.GetInt32();
                    if (s.TryGetProperty("tags", out var tags))
                    {
                        t.Lang = Str(tags, "language").ToLowerInvariant();
                        t.Title = Str(tags, "title");
                    }
                    Audio.Add(t);
                }
            }
            if (VideoIndex < 0) Error = "в файле нет видео";
            else if (Stereo.DetectByName(Name) == null) DetectContent();
        }
        catch (Exception ex) { Error = ex.Message; }
        Probed = true;
    }

    /// <summary>
    /// Определяет формат по картинке: у стереопары половины кадра почти одинаковые
    /// (отличаются только небольшим сдвигом). Сравниваем левую с правой и верхнюю с нижней.
    /// </summary>
    /// <summary>
    /// Для ссылок без описания (прямая ссылка на файл): длительность, размер кадра, частоту и звук
    /// узнаём у самого потока через ffprobe.
    /// </summary>
    void ProbeStream()
    {
        try
        {
            var args = new List<string> { "-v", "error" };
            args.AddRange(InputArgs());
            args.AddRange(new[] { "-show_entries", "stream=codec_type,codec_name,width,height,avg_frame_rate,channels:format=duration",
                "-of", "json", VideoInput });
            using var p = App.Start(App.Ffprobe, args, false);
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            string json = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return; }
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (Duration <= 0 && r.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var du))
                double.TryParse(du.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out Duration);
            if (!r.TryGetProperty("streams", out var streams)) return;
            foreach (var s in streams.EnumerateArray())
            {
                string type = Str(s, "codec_type");
                if (type == "video")
                {
                    if (s.TryGetProperty("width", out var w) && w.GetInt32() > 0) Width = w.GetInt32();
                    if (s.TryGetProperty("height", out var h) && h.GetInt32() > 0) Height = h.GetInt32();
                    var fr = Str(s, "avg_frame_rate").Split('/');
                    if (fr.Length == 2 && double.TryParse(fr[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                        && double.TryParse(fr[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && n > 0 && d > 0 && n / d < 200)
                    { Fps = n / d; FpsText = $"{fr[0]}/{fr[1]}"; }
                    if (VideoCodec == "" || VideoCodec == "none") VideoCodec = Str(s, "codec_name");
                }
                else if (type == "audio" && Audio.Count == 0 && AudioUrl == null)
                {
                    // звук лежит в том же файле, что и видео
                    AudioUrl = VideoUrl;
                    Audio.Add(new AudioTrack { Index = 0, Codec = Str(s, "codec_name"), Title = "звук ролика",
                        Channels = s.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 2 });
                }
            }
        }
        catch { }
    }

    internal void DetectContent()
    {
        if (Duration <= 0) return;
        const int W = 160, H = 90;
        double sbs = 0, ou = 0;
        int n = 0;
        // для потока из сети — 2 кадра (меньше обращений к сайту), для файла — 3
        foreach (var frac in IsStream ? new[] { 0.3, 0.6 } : new[] { 0.2, 0.45, 0.7 })
        {
            var px = GrabGray(Duration * frac, W, H);
            if (px == null) continue;
            double mean = 0;
            foreach (var b in px) mean += b;
            if (mean / px.Length < 12) continue; // тёмный кадр ничего не скажет
            sbs += HalfDiff(px, W, H, true);
            ou += HalfDiff(px, W, H, false);
            n++;
        }
        if (n == 0) return;
        sbs /= n; ou /= n;
        ContentScores = $"sbs={sbs:F1} ou={ou:F1}";
        double best = Math.Min(sbs, ou), other = Math.Max(sbs, ou);
        if (best < 20 && best < other * 0.6) ContentLayout = ou < sbs ? StereoLayout.Ou : StereoLayout.Sbs;
        else if (best >= 20) ContentLayout = StereoLayout.Mono;
    }

    byte[] GrabGray(double t, int w, int h)
    {
        try
        {
            var args = new List<string> { "-v", "error", "-nostdin" };
            args.AddRange(InputArgs());
            args.AddRange(new[] { "-ss", t.ToString("0.###", CultureInfo.InvariantCulture), "-i", VideoInput, "-map", VideoMap,
                "-frames:v", "1", "-vf", $"scale={w}:{h}:flags=area,format=gray", "-f", "rawvideo", "pipe:1" });
            using var p = App.Start(App.Ffmpeg, args, true);
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            var buf = new byte[w * h];
            int read = 0;
            var s = p.StandardOutput.BaseStream;
            while (read < buf.Length) { int k = s.Read(buf, read, buf.Length - read); if (k <= 0) break; read += k; }
            p.WaitForExit(10000);
            return read == buf.Length ? buf : null;
        }
        catch { return null; }
    }

    /// <summary>Средняя разница между половинами кадра (с подбором сдвига ±3 пикселя по горизонтали).</summary>
    static double HalfDiff(byte[] p, int w, int h, bool sideBySide)
    {
        double best = double.MaxValue;
        for (int s = -3; s <= 3; s++)
        {
            long sum = 0; int cnt = 0;
            if (sideBySide)
            {
                int half = w / 2;
                for (int y = 0; y < h; y++)
                    for (int x = 3; x < half - 3; x++) { sum += Math.Abs(p[y * w + x] - p[y * w + half + x + s]); cnt++; }
            }
            else
            {
                int half = h / 2;
                for (int y = 0; y < half; y++)
                    for (int x = 3; x < w - 3; x++) { sum += Math.Abs(p[y * w + x] - p[(y + half) * w + x + s]); cnt++; }
            }
            best = Math.Min(best, (double)sum / cnt);
        }
        return best;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

public sealed class VideoFrame
{
    public byte[] Data;
    public double Time;
}

/// <summary>
/// Декодирование видео через ffmpeg (аппаратно, если получится) в кадры NV12.
/// Кадры складываются в очередь заранее (до 10 штук), рендер берёт их по часам.
/// </summary>
public sealed class VideoDecoder : IDisposable
{
    public readonly int W, H;
    readonly int frameBytes;
    readonly double fps, start;
    readonly Process proc;
    readonly Thread thread;
    readonly CancellationTokenSource cts = new();
    readonly BlockingCollection<VideoFrame> queue; // 10 кадров для файла, 60 (≈2 с) для потока из сети
    readonly ConcurrentBag<byte[]> pool = new();
    readonly StringBuilder errors = new();
    public volatile bool Eof;

    public int Queued => queue.Count;
    public string CommandLine = "";
    long framesRead;
    /// <summary>Сколько кадров пришло от ffmpeg (0 при Eof — видео не открылось).</summary>
    public long FramesRead => Interlocked.Read(ref framesRead);
    public string Errors { get { lock (errors) return errors.ToString(); } }

    public VideoDecoder(Movie m, double startSec)
    {
        start = startSec;
        fps = m.Fps > 0 ? m.Fps : 24;
        queue = new(new ConcurrentQueue<VideoFrame>(), m.IsStream ? 60 : 10);
        // слишком широкие кадры (Full-SBS 3840x1080) уменьшаем вдвое по ширине: на проектор всё равно 1280 на глаз
        int w = m.Width, h = m.Height;
        if (w > 2560) w /= 2;
        if (h > 2160) h /= 2;
        W = w & ~1; H = h & ~1;
        frameBytes = W * H * 3 / 2;
        string vf = (W != m.Width || H != m.Height ? $"scale={W}:{H}:flags=bicubic," : "") + "format=nv12";
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-hwaccel", "auto" };
        args.AddRange(m.InputArgs());
        if (startSec > 0.01) args.AddRange(new[] { "-ss", startSec.ToString("0.###", CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", m.VideoInput, "-map", m.VideoMap, "-an", "-sn", "-dn",
            "-vf", vf, "-fps_mode", "cfr", "-r", m.FpsText, "-f", "rawvideo", "pipe:1" });
        CommandLine = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        proc = App.Start(App.Ffmpeg, args, true);
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
        proc.BeginErrorReadLine();
        thread = new Thread(ReadLoop) { IsBackground = true, Name = "video-decode" };
        thread.Start();
    }

    void ReadLoop()
    {
        var s = proc.StandardOutput.BaseStream;
        long k = 0;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (!pool.TryTake(out var buf)) buf = new byte[frameBytes];
                int read = 0;
                while (read < frameBytes)
                {
                    int n = s.Read(buf, read, frameBytes - read);
                    if (n <= 0) { Eof = true; return; }
                    read += n;
                }
                queue.Add(new VideoFrame { Data = buf, Time = start + k / fps }, cts.Token);
                k++;
                Interlocked.Increment(ref framesRead);
            }
        }
        catch { Eof = true; }
    }

    public bool TryTake(out VideoFrame f) => queue.TryTake(out f);
    public void Return(VideoFrame f) { if (f?.Data != null && f.Data.Length == frameBytes) pool.Add(f.Data); }

    public void Dispose()
    {
        cts.Cancel();
        try { if (!proc.HasExited) proc.Kill(); } catch { }
        thread.Join(1000);
        proc.Dispose();
    }
}

/// <summary>Вывод звука через WinMM waveOut. Позиция воспроизведения — главные часы фильма.</summary>
public sealed class AudioOut : IDisposable
{
    const int Rate = 48000, Channels = 2, BytesPerFrame = 4;
    const int BufFrames = 1920;            // 40 мс
    const int NBuf = 8;
    const uint WAVE_MAPPER = 0xFFFFFFFF, WHDR_DONE = 1, TIME_SAMPLES = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    [StructLayout(LayoutKind.Sequential)]
    struct MMTIME { public uint wType; public uint u; public uint pad; }

    [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr hwo, uint dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr hwo, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutGetPosition(IntPtr hwo, ref MMTIME t, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutPause(IntPtr hwo);
    [DllImport("winmm.dll")] static extern uint waveOutRestart(IntPtr hwo);
    [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr hwo);

    readonly IntPtr hwo;
    readonly IntPtr[] hdr = new IntPtr[NBuf], data = new IntPtr[NBuf];
    readonly bool[] queued = new bool[NBuf];
    readonly int hdrSize = Marshal.SizeOf<WAVEHDR>();
    readonly int flagsOffset = (int)Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwFlags));
    readonly int lengthOffset = (int)Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwBufferLength));
    readonly Process proc;
    readonly Thread thread;
    readonly double start;
    readonly object devLock = new();
    volatile bool stop;
    public volatile float Volume = 1f;
    public volatile bool Eof;
    long bytesFed;
    readonly StringBuilder errors = new();
    /// <summary>Сколько байт звука отдано звуковой карте (0 при Eof — звук не открылся).</summary>
    public long BytesFed => Interlocked.Read(ref bytesFed);
    public string Errors { get { lock (errors) return errors.ToString(); } }

    public AudioOut(Movie m, int streamIndex, double startSec)
    {
        start = startSec;
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = Channels, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * BytesPerFrame,
            nBlockAlign = BytesPerFrame, wBitsPerSample = 16 };
        if (waveOutOpen(out hwo, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) throw new Exception("не удалось открыть звуковое устройство");
        waveOutPause(hwo); // звук начнётся, когда будут готовы первые кадры видео
        for (int i = 0; i < NBuf; i++)
        {
            data[i] = Marshal.AllocHGlobal(BufFrames * BytesPerFrame);
            hdr[i] = Marshal.AllocHGlobal(hdrSize);
            var h = new WAVEHDR { lpData = data[i], dwBufferLength = BufFrames * BytesPerFrame };
            Marshal.StructureToPtr(h, hdr[i], false);
            waveOutPrepareHeader(hwo, hdr[i], (uint)hdrSize);
        }
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(m.InputArgs());
        if (startSec > 0.01) args.AddRange(new[] { "-ss", startSec.ToString("0.###", CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", m.AudioInput, "-map", m.AudioMap(streamIndex), "-vn", "-sn", "-dn",
            "-ac", "2", "-ar", Rate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "pipe:1" });
        proc = App.Start(App.Ffmpeg, args, true);
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
        proc.BeginErrorReadLine();
        thread = new Thread(FeedLoop) { IsBackground = true, Name = "audio-feed" };
        thread.Start();
    }

    void FeedLoop()
    {
        var s = proc.StandardOutput.BaseStream;
        var buf = new byte[BufFrames * BytesPerFrame];
        int i = 0;
        try
        {
            while (!stop)
            {
                // ждём, пока буфер освободится
                while (queued[i] && (Marshal.ReadInt32(hdr[i], flagsOffset) & WHDR_DONE) == 0)
                {
                    if (stop) return;
                    Thread.Sleep(5);
                }
                int read = 0;
                while (read < buf.Length)
                {
                    int n = s.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read == 0) { Eof = true; return; }
                ApplyVolume(buf, read);
                Marshal.Copy(buf, 0, data[i], read);
                Marshal.WriteInt32(hdr[i], lengthOffset, read);
                lock (devLock) { if (stop) return; waveOutWrite(hwo, hdr[i], (uint)hdrSize); }
                Interlocked.Add(ref bytesFed, read);
                queued[i] = true;
                i = (i + 1) % NBuf;
                if (read < buf.Length) { Eof = true; return; }
            }
        }
        catch { Eof = true; }
    }

    void ApplyVolume(byte[] buf, int len)
    {
        float v = Volume;
        if (v >= 0.999f) return;
        var samples = MemoryMarshal.Cast<byte, short>(buf.AsSpan(0, len & ~1));
        for (int k = 0; k < samples.Length; k++) samples[k] = (short)(samples[k] * v);
    }

    /// <summary>Сколько секунд фильма уже прозвучало.</summary>
    public double Clock
    {
        get
        {
            var t = new MMTIME { wType = TIME_SAMPLES };
            lock (devLock) waveOutGetPosition(hwo, ref t, (uint)Marshal.SizeOf<MMTIME>());
            return start + t.u / (double)Rate;
        }
    }

    public void Pause() { lock (devLock) waveOutPause(hwo); }
    public void Resume() { lock (devLock) waveOutRestart(hwo); }

    public void Dispose()
    {
        stop = true;
        try { if (!proc.HasExited) proc.Kill(); } catch { }
        thread.Join(1000);
        lock (devLock)
        {
            waveOutReset(hwo);
            for (int i = 0; i < NBuf; i++)
            {
                waveOutUnprepareHeader(hwo, hdr[i], (uint)hdrSize);
                Marshal.FreeHGlobal(hdr[i]); Marshal.FreeHGlobal(data[i]);
            }
            waveOutClose(hwo);
        }
        proc.Dispose();
    }
}

/// <summary>Одно воспроизведение фильма с заданной секунды: видео + звук + часы.</summary>
public sealed class Session : IDisposable
{
    public readonly Movie Movie;
    public readonly VideoDecoder Video;
    public AudioOut Audio { get; private set; }
    int audioStream;
    float volume;
    public readonly double Start;
    readonly Stopwatch sw = new();
    bool started;
    volatile bool paused;
    public string AudioError = "";

    public Session(Movie m, double start, int audioStream, float volume, bool startPaused)
    {
        Movie = m; Start = Math.Max(0, start); paused = startPaused;
        this.audioStream = audioStream; this.volume = volume;
        Video = new VideoDecoder(m, Start);
        if (audioStream >= 0)
        {
            try { Audio = new AudioOut(m, audioStream, Start) { Volume = volume }; }
            catch (Exception ex) { AudioError = ex.Message; }
        }
    }

    public bool Paused => paused;

    /// <summary>Поток из сети подгружается: часы стоят, пока не накопится запас кадров.</summary>
    public bool Buffering => !started || buffering;
    bool buffering;

    /// <summary>
    /// Вызывается рендером: когда накопились первые кадры, запускаем часы.
    /// Для потока из сети: если кадры кончились (сеть не успевает), ставим на паузу до подгрузки ~1 с.
    /// </summary>
    public void Tick()
    {
        int need = Movie.IsStream ? 24 : 3;
        if (!started)
        {
            if (Video.Queued < need && !Video.Eof) return;
            started = true;
            if (!paused) Run();
            return;
        }
        // звук не открылся (сайт оборвал соединение и т.п.) — идём без звука по своим часам
        if (Audio != null && !AudioFailed && Audio.Eof && Audio.BytesFed == 0)
        {
            AudioFailed = true;
            swBase = Audio.Clock - Start;
            if (!paused && !buffering) sw.Start();
        }
        if (!Movie.IsStream || paused) return;
        if (!buffering && Video.Queued == 0 && !Video.Eof) { buffering = true; Halt(); }
        else if (buffering && (Video.Queued >= need || Video.Eof)) { buffering = false; Run(); }
    }

    /// <summary>Звук не загрузился — фильм идёт без звука.</summary>
    public volatile bool AudioFailed;
    double swBase;
    bool UseAudio => Audio != null && !AudioFailed;

    void Run() { if (UseAudio) Audio.Resume(); else sw.Start(); }
    void Halt() { if (UseAudio) Audio.Pause(); else sw.Stop(); }

    public void SetPaused(bool p)
    {
        if (p == paused) return;
        paused = p;
        if (!started) return;
        if (p) Halt(); else Run();
    }

    public double Now => !started ? Start : UseAudio ? Audio.Clock : Start + swBase + sw.Elapsed.TotalSeconds;

    public float Volume { set { volume = value; if (Audio != null) Audio.Volume = value; } }

    /// <summary>
    /// Звук не открылся из-за временной ошибки сайта — открыть его заново с текущего места,
    /// не останавливая картинку. Часы переходят обратно на звук, когда он заиграет.
    /// </summary>
    public void RetryAudio()
    {
        if (!AudioFailed || audioStream < 0) return;
        var old = Audio;
        double at = Now;
        try
        {
            var a = new AudioOut(Movie, audioStream, at) { Volume = volume };
            // пока новый звук готовится, картинка идёт по своим часам; переключаемся, когда он начал звучать
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < 100 && a.BytesFed == 0 && !a.Eof; i++) Thread.Sleep(50);
                if (a.BytesFed == 0) { a.Dispose(); return; }
                lock (this)
                {
                    Audio = a;
                    AudioFailed = false;
                    sw.Stop();
                    if (!paused && !buffering) a.Resume();
                }
                old?.Dispose();
                Log.Write("Звук подключился");
            });
        }
        catch { }
    }

    public void Dispose()
    {
        Video.Dispose();
        Audio?.Dispose();
    }
}
