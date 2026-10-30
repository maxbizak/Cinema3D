namespace Cinema3D;

/// <summary>Параметры командной строки (для ярлыков и проверки).</summary>
public class Options
{
    public string Play;          // --play "файл"
    public bool Test;            // --test — сразу тест очков
    public OutMode? Mode;        // --mode dlp|2d|anaglyph
    public string Screen;        // --screen \\.\DISPLAY2
    public int ExitAfter;        // --exit-after N — закрыть через N секунд (для замеров)
    public double Start = -1;    // --start N — начать с секунды N
    public string Capture;       // --capture \\.\DISPLAY2 — показывать видео, идущее в браузере на этом мониторе
    public StereoLayout? CaptureLayout; // выбранный формат видео в браузере

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string next() => i + 1 < a.Length ? a[++i] : "";
            switch (a[i].ToLowerInvariant())
            {
                case "--play": o.Play = next(); break;
                case "--test": o.Test = true; break;
                case "--mode":
                    o.Mode = next().ToLowerInvariant() switch { "2d" => OutMode.Mono2D, "anaglyph" => OutMode.Anaglyph, _ => OutMode.Dlp };
                    break;
                case "--screen": o.Screen = next(); break;
                case "--capture": o.Capture = next(); break;
                case "--capture-layout":
                    o.CaptureLayout = next().ToLowerInvariant() switch
                    {
                        "sbs" => StereoLayout.Sbs, "ou" => StereoLayout.Ou,
                        "2d" => StereoLayout.Mono, _ => StereoLayout.Auto
                    };
                    break;
                case "--exit-after": int.TryParse(next(), out o.ExitAfter); break;
                case "--start": double.TryParse(next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out o.Start); break;
                default: // файл, перетащенный на значок, или ссылка
                    if (o.Play == null && (File.Exists(a[i]) || a[i].StartsWith("http", StringComparison.OrdinalIgnoreCase))) o.Play = a[i];
                    break;
            }
        }
        return o;
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // непредвиденная ошибка — в журнал, без пугающего окна «Microsoft .NET»
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Write("ОШИБКА (перехвачена): " + e.Exception);
            if (e.Exception is not ObjectDisposedException)
                MessageBox.Show("Произошла ошибка, программа продолжит работу:\n" + e.Exception.Message +
                    "\n\nПодробности записаны в Cinema3D.log.", "3D Кинотеатр", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("ОШИБКА (фатальная): " + e.ExceptionObject);
        var opt = Options.Parse(args);
        var settings = Settings.Load();
        if (!File.Exists(App.Ffmpeg))
        {
            MessageBox.Show($"Не найден ffmpeg:\n{App.Ffmpeg}\n\nПрограмма должна лежать в папке 3D-Cinema-Test рядом с папкой tools.",
                "3D Кинотеатр", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (opt.Play != null || opt.Test || opt.Capture != null)
        {
            Movie m = null;
            if (opt.Play != null && opt.Play.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                m = Movie.FromUrl(opt.Play, out var err);
                if (m == null) { MessageBox.Show("Не удалось открыть ссылку:\n" + err, "3D Кинотеатр"); return; }
            }
            else if (opt.Play != null)
            {
                m = Movie.FromFile(opt.Play, Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(opt.Play))));
                m.Probe();
            }
            Application.Run(new PlayerForm(settings, m, opt.Test, opt));
        }
        else Application.Run(new LibraryForm(settings));
    }
}
