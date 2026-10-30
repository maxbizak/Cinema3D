using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Numerics;

namespace Cinema3D;

/// <summary>
/// Окно воспроизведения на весь экран проектора.
/// DLP: на каждом такте проектора один глаз на весь экран, по очереди L, R, L, R...
/// Кадр фильма меняется только в начале пары, поэтому оба глаза всегда видят один кадр.
/// Чётность глаза считается по номеру такта из статистики видеокарты: если такт пропущен,
/// порядок глаз восстанавливается сам.
/// </summary>
public sealed class PlayerForm : Form
{
    readonly Settings settings;
    readonly Options opt;
    readonly Movie movie;
    readonly MovieSettings ms;
    readonly DisplayInfo disp;
    readonly Stopwatch clock = Stopwatch.StartNew();
    Thread renderThread;
    IntPtr hwnd;
    volatile bool running = true;
    volatile Session session;
    volatile bool test, swapEyes, showDiag;
    volatile OutMode outMode;
    volatile StereoLayout layout;
    volatile string toast = "";
    long toastUntil;
    int audioStream;
    System.Windows.Forms.Timer uiTimer;
    ControlForm controls;
    /// <summary>Пульт, если он ещё открыт (после выхода окно пульта уже закрыто — не трогать).</summary>
    ControlForm Ctl => controls is { IsDisposed: false, Disposing: false } ? controls : null;
    long lastActivity = Environment.TickCount64;
    Point lastMouse;

    /// <summary>Прозрачный курсор: прячем стрелку только над картинкой, а не во всей программе.</summary>
    static readonly Cursor BlankCursor = new(new Bitmap(32, 32).GetHicon());

    // для диагностики (пишет поток вывода)
    volatile string adapterName = "";
    double hz;
    long missed, parityFixes, presents;
    int missedLastMin;
    bool loggedDecoder;
    readonly string captureDevice;                       // захват монитора с браузером (null — обычный режим)
    volatile StereoLayout captureLayout = StereoLayout.Mono;
    RectangleF captureCrop = new(0, 0, 1, 1);           // где на захваченном экране само видео (доли 0..1)
    readonly Queue<(bool[] Rows, bool[] Cols)> capHist = new();      // замеры захвата за последнюю минуту
    readonly Queue<StereoLayout> recentLayouts = new();
    Renderer.CaptureGeometry capGeo;                                  // текущая (устойчивая) рамка видео
    volatile bool resetCapture;

    /// <summary>Найти рамку видео заново (после смены видео или выхода браузера из полноэкранного режима).</summary>
    public void ResetCaptureFrame()
    {
        resetCapture = true;
        Toast("Ищу видео на экране заново…", 2500);
    }

    static bool Moved(Renderer.CaptureGeometry a, Renderer.CaptureGeometry b, float tol)
    {
        static bool D(RectangleF x, RectangleF y, float t) =>
            Math.Abs(x.Left - y.Left) > t || Math.Abs(x.Top - y.Top) > t || Math.Abs(x.Right - y.Right) > t || Math.Abs(x.Bottom - y.Bottom) > t;
        return D(a.Crop, b.Crop, tol) || D(a.OuL, b.OuL, tol) || D(a.SbsL, b.SbsL, tol);
    }
    RectangleF capOuL = new(0, 0, 1, .5f), capOuR = new(0, .5f, 1, .5f), capSbsL = new(0, 0, .5f, 1), capSbsR = new(.5f, 0, .5f, 1);
    Vector4 lastRA; float lastAspect;
    bool dumped;
    volatile int netRetries;
    Session audioFailNoted;
    int audioRetries;
    long lastAudioRetry;
    bool audioFailToast;
    long lastRetry = Environment.TickCount64;

    /// <summary>Временная ошибка сайта: слишком много запросов или сбой сервера — имеет смысл повторить.</summary>
    static bool IsTemporary(string err) =>
        err.Contains("429") || err.Contains(" 500") || err.Contains(" 502") || err.Contains(" 503") || err.Contains(" 504")
        || err.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    public PlayerForm(Settings s, Movie m, bool startTest, Options o)
    {
        settings = s; opt = o; movie = m;
        disp = Displays.PickProjector(o.Screen ?? s.Screen);
        outMode = o.Mode ?? s.OutMode; // --mode действует только на этот запуск и не сохраняется
        captureDevice = o.Capture;
        test = startTest || (m == null && captureDevice == null);
        if (m != null)
        {
            ms = s.For(m);
            layout = ms.StereoLayout;
            swapEyes = ms.Swap;
            audioStream = ms.AudioStream >= 0 && m.Audio.Any(a => a.Index == ms.AudioStream) ? ms.AudioStream : m.DefaultAudio();
        }
        if (test) swapEyes = s.TestSwap;

        Text = m != null ? m.Name + " — 3D Кинотеатр" : captureDevice != null ? "Захват экрана — 3D Кинотеатр" : "Тест очков — 3D Кинотеатр";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = disp.Screen.Bounds;
        BackColor = Color.Black;
        KeyPreview = true;
        TopMost = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (captureDevice != null && string.Equals(captureDevice, disp.Device, StringComparison.OrdinalIgnoreCase))
        {
            // снимать и показывать на одном экране нельзя — получится «зеркало в зеркале»
            MessageBox.Show(this, $"Экран для фильма ({disp}) совпадает с экраном, где браузер. Выберите в окне программы проектор.",
                "3D Кинотеатр", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            BeginInvoke(Close);
            return;
        }
        Cursor = BlankCursor;
        Log.Reset();
        Log.Write($"Старт: экран {disp}, режим {outMode}, {(captureDevice != null ? "захват " + captureDevice : movie == null ? "тест" : movie.IsStream ? "ссылка " + movie.Url : "файл " + movie.Path)}");
        if (movie != null)
            Log.Write($"Формат: {EffectiveLayout} (настройка {layout}, по картинке {movie.ContentLayout?.ToString() ?? "-"} {movie.ContentScores}), " +
                      $"звук: {movie.Audio.FirstOrDefault(a => a.Index == audioStream)?.ToString() ?? "нет"}");
        hwnd = Handle;
        if (movie != null)
        {
            double start = opt.Start >= 0 ? opt.Start : (ms.Pos > 5 && ms.Pos < movie.Duration - 30 ? ms.Pos : 0);
            session = new Session(movie, start, audioStream, settings.Volume, test);
            if (start > 5) Toast("Продолжение с " + Fmt(start), 3000);
            else Toast(movie.Name, 2500);
        }
        if (test) Toast("Тест очков: каждый глаз должен видеть только свою надпись.  S — поменять глаза,  Esc — выход", 8000);
        if (disp.Hz is < 110 or > 130 && outMode == OutMode.Dlp)
            Toast($"Внимание: проектор работает на {disp.Hz} Гц, для DLP-очков нужно 120 Гц", 8000);

        renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "render", Priority = ThreadPriority.AboveNormal };
        renderThread.Start();

        uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
        int ticks = 0;
        uiTimer.Tick += (_, _) =>
        {
            ticks++;
            if (opt.ExitAfter > 0 && clock.Elapsed.TotalSeconds >= opt.ExitAfter) { Close(); return; }
            if (ticks % 20 == 0) SavePosition();
            // поток из сети не открылся из-за временной ошибки сайта (429, 5xx) — повторить через 4 с, до 3 раз
            var s = session;
            if (s != null && s.AudioFailed)
            {
                string aerr = s.Audio?.Errors ?? "";
                if (s != audioFailNoted)
                {
                    audioFailNoted = s; audioRetries = 0;
                    Log.Write("Звук не загрузился: " + aerr.Replace("\r", "").Replace("\n", " | "));
                }
                if (IsTemporary(aerr) && audioRetries < 3 && Environment.TickCount64 - lastAudioRetry > 5000)
                {
                    audioRetries++; lastAudioRetry = Environment.TickCount64;
                    Log.Write($"Повтор звука ({audioRetries}/3)");
                    Toast($"Звук: сайт временно не отвечает, подключаю снова… ({audioRetries}/3)", 3000);
                    s.RetryAudio();
                }
                else if (!IsTemporary(aerr) || audioRetries >= 3)
                {
                    if (!audioFailToast) { audioFailToast = true; Toast("Звук не загрузился — фильм идёт без звука. Попробуйте другую дорожку (A) или перемотать.", 6000); }
                }
            }
            if (s != null && movie != null && movie.IsStream && s.Video.Eof && s.Video.FramesRead == 0
                && netRetries < 3 && IsTemporary(s.Video.Errors) && Environment.TickCount64 - lastRetry > 4000)
            {
                netRetries++;
                lastRetry = Environment.TickCount64;
                Log.Write($"Повтор открытия потока ({netRetries}/3)");
                Restart(s.Start);
            }
            // курсор и пульт на экране проектора прячутся через 3 с без движения мыши
            if (Environment.TickCount64 - lastActivity > 3000 && !(Ctl?.ContainsFocusOrMouse ?? false))
            {
                Cursor = BlankCursor;
                Ctl?.AutoHide();
            }
        };
        uiTimer.Start();

        // пульт: на другом экране, если он есть, иначе внизу экрана проектора (появляется при движении мыши)
        controls = new ControlForm(this, disp.Screen,
            captureDevice == null ? null : Screen.AllScreens.FirstOrDefault(s => s.DeviceName == captureDevice));
        controls.Show(this);
        Activate();
    }

    // ---------------- вывод кадров ----------------

    void RenderLoop()
    {
        Renderer r = null;
        try
        {
            r = new Renderer(hwnd, disp.Device, disp.Screen.Bounds.Width, disp.Screen.Bounds.Height);
            adapterName = r.AdapterName;
            Log.Write($"Видеокарта: {r.AdapterName}, окно {r.Width}x{r.Height}");
            if (captureDevice != null)
            {
                var err = r.StartCapture(captureDevice);
                Log.Write(err ?? $"Захват экрана {captureDevice}: {r.RgbW}x{r.RgbH}");
                if (err != null) Toast(err, 15000);
                else Toast("Откройте видео в браузере на весь экран этого монитора и нажмите Play. Формат определится сам.", 8000);
            }
            long lastDetect = 0;

            Session cs = null;
            VideoFrame cur = null, next = null;
            long lastOff = long.MinValue, lastSyncQpc = 0, lastLog = 0, lastOvBuild = -1000;
            uint lastSyncRefresh = 0;
            var missTimes = new Queue<long>();
            bool testDrawn = false;
            string ovText = "";

            while (running)
            {
                // 1. статистика: на каком такте экрана показан последний кадр
                if (r.TryStats(out var st))
                {
                    long off = (long)st.PresentRefreshCount - st.PresentCount;
                    if (lastOff != long.MinValue && off != lastOff)
                    {
                        long d = off - lastOff;
                        // Первые 3 с и сдвиги больше 60 тактов (0,5 с) — это переход окна в полноэкранный вывод
                        // или пересинхронизация, а не рывки: порядок глаз выравниваем, но пропусками не считаем.
                        if (d > 0 && d <= 60 && clock.ElapsedMilliseconds > 3000)
                        {
                            missed += d;
                            for (int k = 0; k < Math.Min(d, 50); k++) missTimes.Enqueue(clock.ElapsedMilliseconds);
                        }
                        if ((d & 1) != 0) parityFixes++;
                    }
                    lastOff = off;
                    if (lastSyncQpc == 0 || st.SyncQPCTime <= lastSyncQpc) { lastSyncQpc = st.SyncQPCTime; lastSyncRefresh = st.SyncRefreshCount; }
                    else if (st.SyncRefreshCount - lastSyncRefresh >= 120)
                    {
                        var measuredHz = (st.SyncRefreshCount - lastSyncRefresh) * (double)Stopwatch.Frequency / (st.SyncQPCTime - lastSyncQpc);
                        if (measuredHz is >= 20 and <= 500) hz = measuredHz;
                        lastSyncQpc = st.SyncQPCTime; lastSyncRefresh = st.SyncRefreshCount;
                    }
                }
                while (missTimes.Count > 0 && clock.ElapsedMilliseconds - missTimes.Peek() > 60000) missTimes.Dequeue();
                missedLastMin = missTimes.Count;

                // номер такта, на котором покажется этот кадр; чётный = левый глаз
                long vIdx = presents + (lastOff == long.MinValue ? 0 : lastOff);
                bool firstOfPair = (vIdx & 1) == 0;
                var mode = outMode;

                // 2. кадр фильма (в DLP — только в начале пары)
                var s = session;
                if (s != cs)
                {
                    if (cs != null) { cs.Video.Return(cur); cs.Video.Return(next); }
                    cur = next = null; cs = s;
                }
                if (cs != null && !test)
                {
                    cs.Tick();
                    if (mode != OutMode.Dlp || firstOfPair || cur == null)
                    {
                        // кадр покажется примерно через 3 такта — берём кадр на это время
                        double t = cs.Now + 0.025 + settings.AvOffsetMs / 1000.0;
                        bool changed = false;
                        while (true)
                        {
                            if (next == null && !cs.Video.TryTake(out next)) break;
                            if (next.Time <= t || cur == null)
                            {
                                cs.Video.Return(cur);
                                cur = next; next = null; changed = true;
                                if (cur.Time > t) break;
                            }
                            else break;
                        }
                        if (changed) r.UploadNV12(cur.Data, cs.Video.W, cs.Video.H);
                    }
                }

                // 3. тестовая стереопара
                if (test && !testDrawn) { DrawTestPattern(r); testDrawn = true; }
                if (!test) testDrawn = false; // захват мог занять ту же картинку — при следующем тесте нарисовать заново

                // 3а. захват монитора: новый кадр только в начале пары, чтобы оба глаза видели один кадр
                if (captureDevice != null && !test)
                {
                    if (mode != OutMode.Dlp || firstOfPair) r.CaptureFrame();
                    // пока пульт стоит на захватываемом мониторе, он попал бы в замер — пропускаем
                    if (resetCapture)
                    {
                        resetCapture = false;
                        capHist.Clear(); recentLayouts.Clear(); capGeo = null;
                        lastDetect = 0;
                        Log.Write("Захват: рамка сброшена");
                    }
                    if (clock.ElapsedMilliseconds - lastDetect > 2000 && r.CapturedFrames > 0 && !(Ctl?.CoversCapture ?? false))
                    {
                        lastDetect = clock.ElapsedMilliseconds;
                        var smp = r.SampleCapture(capGeo);
                        if (smp != null)
                        {
                            // Рамка видео на экране не меняется, пока не переключили видео. Поэтому копим её за минуту
                            // (30 замеров): строка/столбец — видео, если там хоть раз была картинка. Тёмные сцены рамку
                            // не сжимают, а мелкие колебания (меньше 1,5%) не двигают картинку на проекторе.
                            capHist.Enqueue((smp.Rows, smp.Cols));
                            while (capHist.Count > 30) capHist.Dequeue();
                            var rows = new bool[smp.Rows.Length];
                            var cols = new bool[smp.Cols.Length];
                            foreach (var (hr, hc) in capHist)
                            {
                                if (hr.Length != rows.Length || hc.Length != cols.Length) continue;
                                for (int i = 0; i < rows.Length; i++) rows[i] |= hr[i];
                                for (int i = 0; i < cols.Length; i++) cols[i] |= hc[i];
                            }
                            var g = Renderer.Geometry(rows, cols, smp.Sx, smp.Sy, smp.W, smp.H);
                            if (g != null && (capGeo == null || Moved(g, capGeo, 0.015f)))
                            {
                                capGeo = g;
                                captureCrop = g.Crop; capOuL = g.OuL; capOuR = g.OuR; capSbsL = g.SbsL; capSbsR = g.SbsR;
                                Log.Write($"Захват: видео на экране x={g.Crop.X:F2} y={g.Crop.Y:F2} w={g.Crop.Width:F2} h={g.Crop.Height:F2}; " +
                                          $"OU глаз h={g.OuL.Height:F2}, SBS глаз w={g.SbsL.Width:F2}");
                            }
                            // формат — по большинству последних 5 замеров (тёмные сцены не голосуют)
                            if (smp.Layout != null)
                            {
                                recentLayouts.Enqueue(smp.Layout.Value);
                                while (recentLayouts.Count > 5) recentLayouts.Dequeue();
                                var votes = recentLayouts.GroupBy(q => q).OrderByDescending(gr => gr.Count()).First();
                                if (votes.Count() >= 3 && votes.Key != captureLayout)
                                {
                                    captureLayout = votes.Key;
                                    if (layout == StereoLayout.Auto) Toast("Формат видео: " + Stereo.Name(captureLayout), 2500);
                                    Log.Write("Захват: формат " + captureLayout);
                                }
                            }
                        }
                        var dump = Environment.GetEnvironmentVariable("C3D_DUMP_CAPTURE");
                        if (!string.IsNullOrEmpty(dump) && !dumped) { dumped = true; r.SaveCapture(dump); }
                    }
                }

                // 4. какой кусок кадра показывать
                Vector4 L, R; float aspect; int src; bool content;
                if (test)
                {
                    (L, R, aspect) = Stereo.Eyes(StereoLayout.Sbs, r.RgbW, r.RgbH);
                    src = 1; content = r.RgbW > 0;
                }
                else if (captureDevice != null)
                {
                    // картинки глаз, найденные на экране (у широкоформатного фильма — без его чёрных полос,
                    // оба глаза одного размера, чтобы не разъезжались по вертикали)
                    var lay = layout == StereoLayout.Auto ? captureLayout : layout;
                    RectangleF a, b;
                    if (lay == StereoLayout.Ou) { a = capOuL; b = capOuR; }
                    else if (lay == StereoLayout.Sbs) { a = capSbsL; b = capSbsR; }
                    else { a = b = captureCrop; }
                    L = new Vector4(a.X, a.Y, a.Width, a.Height);
                    R = new Vector4(b.X, b.Y, b.Width, b.Height);
                    // пропорции глаза на экране; у Half-форматов картинка глаза сжата вдвое — возвращаем
                    aspect = a.Height > 0 ? a.Width * r.RgbW / (a.Height * r.RgbH) : 16f / 9;
                    if (lay == StereoLayout.Sbs && aspect < 1.25f) aspect *= 2;
                    if (lay == StereoLayout.Ou && aspect > 2.5f) aspect /= 2;
                    src = 1; content = r.CapturedFrames > 0;
                }
                else if (movie != null)
                {
                    var lay = layout == StereoLayout.Auto ? movie.AutoLayout : layout;
                    (L, R, aspect) = Stereo.Eyes(lay, movie.DisplayWidth, movie.Height);
                    src = 0; content = r.HasVideo;
                }
                else { L = R = Vector4.Zero; aspect = 0; src = 0; content = false; }
                var eL = swapEyes ? R : L;
                var eR = swapEyes ? L : R;
                Vector4 rA = eL, rB = eR;
                int shaderMode = 0;
                if (mode == OutMode.Dlp) rA = (vIdx & 1) == 0 ? eL : eR;
                else if (mode == OutMode.Anaglyph) shaderMode = 1;

                // 5. надписи (обновляем не чаще 4 раз в секунду)
                if (clock.ElapsedMilliseconds - lastOvBuild >= 250)
                {
                    lastOvBuild = clock.ElapsedMilliseconds;
                    string text = BuildOverlayText(cs);
                    if (text != ovText) { ovText = text; if (text != "") DrawOverlay(r, text); }
                }

                lastRA = rA; lastAspect = aspect;
                r.Draw(rA, rB, shaderMode, src, aspect, ovText != "", content);
                r.Present();
                presents++;

                // 6. журнал раз в 5 секунд
                if (clock.ElapsedMilliseconds - lastLog >= 5000)
                {
                    lastLog = clock.ElapsedMilliseconds;
                    Log.Write($"hz={hz:F2} presents={presents} missed={missed} lastMin={missedLastMin} parityFixes={parityFixes} " +
                              $"t={(cs?.Now ?? 0):F1} queue={cs?.Video.Queued ?? 0} mode={mode} test={test} paused={cs?.Paused} gpu={adapterName}" +
                              (captureDevice != null ? $" crop={captureCrop.X:F2},{captureCrop.Y:F2},{captureCrop.Width:F2},{captureCrop.Height:F2} eye={lastRA.X:F2},{lastRA.Y:F2},{lastRA.Z:F2},{lastRA.W:F2} aspect={lastAspect:F2} frames={r.CapturedFrames}" : ""));
                    // кадры не идут — записать, что ответил ffmpeg (один раз)
                    if (cs != null && !test && cs.Video.FramesRead == 0 && !loggedDecoder)
                    {
                        loggedDecoder = true;
                        Log.Write("Видео не поступает. Команда: ffmpeg " + cs.Video.CommandLine);
                        Log.Write("Ответ ffmpeg: " + cs.Video.Errors.Replace("\r", "").Replace("\n", " | "));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("ОШИБКА: " + ex);
            if (running)
                BeginInvoke(() =>
                {
                    MessageBox.Show(this, "Ошибка вывода на проектор:\n" + ex.Message, "3D Кинотеатр", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                });
        }
        finally { r?.Dispose(); }
    }

    string BuildOverlayText(Session cs)
    {
        var lines = new List<string>();
        if (Environment.TickCount64 < Interlocked.Read(ref toastUntil)) lines.Add(toast);
        if (cs != null && cs.Video.Eof && cs.Video.FramesRead == 0 && !test)
        {
            var err = cs.Video.Errors.Split('\n').FirstOrDefault(l => l.Contains("rror")) ?? "";
            lines.Add(err.Contains("403") ? "Сайт не разрешает показывать это видео в стороннем плеере (403). Скачайте фильм файлом."
                    : IsTemporary(err) && netRetries < 3 ? $"Сайт временно не отвечает, повторяю… (попытка {netRetries + 1} из 3)"
                    : IsTemporary(err) ? "Сайт временно ограничил доступ. Подождите пару минут и откройте ссылку снова."
                    : "Не удалось открыть видео. " + (err.Length > 160 ? err[..160] : err));
        }
        else if (movie != null && movie.IsStream && cs != null && cs.Buffering && !test) lines.Add("Загрузка видео из сети…");
        if (showDiag)
        {
            string time = cs != null ? $"{Fmt(cs.Now)} / {Fmt(movie.Duration)}{(cs.Paused ? "  (пауза)" : "")}" : "-";
            string track = movie?.Audio.FirstOrDefault(a => a.Index == audioStream)?.ToString() ?? "нет";
            var lay = movie == null ? StereoLayout.Sbs : layout == StereoLayout.Auto ? movie.AutoLayout : layout;
            lines.Add($"Проектор: {disp.Device.Replace(@"\\.\", "")}  {hz:F2} Гц   видеокарта: {adapterName}");
            lines.Add($"Пропущено тактов: {missedLastMin} за минуту (всего {missed}),  исправлений порядка глаз: {parityFixes}");
            lines.Add($"Режим: {Stereo.Name(outMode)}   формат: {Stereo.Name(lay)}{(layout == StereoLayout.Auto ? " (авто)" : "")}   глаза: {(swapEyes ? "поменяны" : "обычно")}");
            if (movie != null)
                lines.Add($"Время: {time}   кадров в очереди: {cs?.Video.Queued ?? 0}   звук: {track}   сдвиг звука: {settings.AvOffsetMs:+0;-0;0} мс");
            lines.Add("Пробел пауза · ←→ ±10 с · PgUp/PgDn ±1 мин · ↑↓ громкость · S глаза · L формат · A дорожка · 1/2/3 режим · T тест · [ ] сдвиг звука · Esc выход");
        }
        return string.Join("\n", lines);
    }

    static readonly Font OvFont = new("Segoe UI", 17, FontStyle.Regular, GraphicsUnit.Pixel);

    void DrawOverlay(Renderer r, string text)
    {
        int maxW = Math.Max(300, disp.Screen.Bounds.Width - 40);
        using var meas = new Bitmap(1, 1);
        Size sz;
        using (var g0 = Graphics.FromImage(meas))
            sz = Size.Ceiling(g0.MeasureString(text, OvFont, maxW - 24));
        int w = Math.Min(maxW, sz.Width + 24), h = sz.Height + 16;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(215, 14, 14, 20));
            g.DrawString(text, OvFont, Brushes.White, new RectangleF(12, 8, w - 24, h - 16));
        }
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try { r.UploadOverlay(bd.Scan0, w, h, bd.Stride); } finally { bmp.UnlockBits(bd); }
    }

    /// <summary>Тестовая стереопара: слева «ЛЕВЫЙ ГЛАЗ» (красный), справа «ПРАВЫЙ ГЛАЗ» (синий).</summary>
    static void DrawTestPattern(Renderer r)
    {
        const int W = 1280, H = 720;
        using var bmp = new Bitmap(W * 2, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var big = new Font("Segoe UI", 96, FontStyle.Bold, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", 30, FontStyle.Regular, GraphicsUnit.Pixel);
            using var ball = new Font("Segoe UI", 28, FontStyle.Bold, GraphicsUnit.Pixel);
            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var eyes = new[]
            {
                (x: 0, bg: Color.FromArgb(90, 13, 13), fg: Color.FromArgb(255, 96, 96), text: "◀ ЛЕВЫЙ ГЛАЗ", d: +1),
                (x: W, bg: Color.FromArgb(13, 42, 90), fg: Color.FromArgb(96, 168, 255), text: "ПРАВЫЙ ГЛАЗ ▶", d: -1),
            };
            foreach (var e in eyes)
            {
                g.ResetTransform();
                g.TranslateTransform(e.x, 0);
                using (var b = new SolidBrush(e.bg)) g.FillRectangle(b, 0, 0, W, H);
                using (var b = new SolidBrush(e.fg)) g.DrawString(e.text, big, b, new RectangleF(0, 80, W, 140), center);
                g.DrawString("Закройте один глаз: каждый глаз должен видеть ТОЛЬКО свою надпись", small, Brushes.Gainsboro, new RectangleF(0, 230, W, 50), center);
                // шар «перед экраном»: у левого глаза правее, у правого левее
                using (var b = new SolidBrush(Color.FromArgb(255, 210, 58))) g.FillEllipse(b, W / 2 - 90 + e.d * 28, 380, 180, 180);
                g.DrawString("ближе", ball, Brushes.Black, new RectangleF(W / 2 - 90 + e.d * 28, 380, 180, 180), center);
                // рамки «за экраном»
                using var pen = new Pen(Color.White, 8);
                g.DrawRectangle(pen, 120 - e.d * 14, 360, 260, 240);
                g.DrawRectangle(pen, W - 380 - e.d * 14, 360, 260, 240);
            }
        }
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { r.UploadRgb(bd.Scan0, bmp.Width, bmp.Height, bd.Stride); } finally { bmp.UnlockBits(bd); }
    }

    // ---------------- управление ----------------

    void Toast(string text, int ms = 2000)
    {
        toast = text;
        Interlocked.Exchange(ref toastUntil, Environment.TickCount64 + ms);
    }

    static string Fmt(double s)
    {
        if (double.IsNaN(s) || s < 0) s = 0;
        var t = TimeSpan.FromSeconds(Math.Floor(s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>Перезапуск декодеров с новой позиции (перемотка, смена дорожки).</summary>
    void Restart(double t)
    {
        if (movie == null) return;
        var old = session;
        t = Math.Clamp(t, 0, Math.Max(0, movie.Duration - 1));
        bool paused = old?.Paused ?? false;
        session = new Session(movie, t, audioStream, settings.Volume, paused);
        if (old != null) ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(300); old.Dispose(); });
    }

    // ---------- управление: общее для клавиатуры и пульта ----------

    public Movie Movie => movie;
    public double Position => session?.Now ?? 0;
    public double Duration => movie?.Duration ?? 0;
    public bool Paused => session?.Paused ?? true;
    public bool TestMode => test;
    public int AudioStream => audioStream;
    public StereoLayout LayoutSetting => layout;
    public StereoLayout EffectiveLayout => layout != StereoLayout.Auto ? layout
        : movie != null ? movie.AutoLayout : captureDevice != null ? captureLayout : StereoLayout.Sbs;
    public bool Capturing => captureDevice != null;
    public OutMode Mode => outMode;
    public bool Swap => swapEyes;
    public float Volume => settings.Volume;

    public void TogglePause()
    {
        var s = session;
        if (s == null || test) return;
        s.SetPaused(!s.Paused);
        Toast(s.Paused ? "Пауза" : "Воспроизведение", 1000);
    }

    public void SeekTo(double t)
    {
        if (session == null) return;
        t = Math.Clamp(t, 0, Duration);
        Restart(t);
        Toast("→ " + Fmt(t), 1200);
    }

    public void SeekBy(double delta)
    {
        var s = session;
        if (s == null) return;
        double t = Math.Clamp(s.Now + delta, 0, Duration);
        Restart(t);
        Toast($"{(delta > 0 ? "+" : "")}{delta:0} с → {Fmt(t)}", 1200);
    }

    public void SetAudio(int streamIndex)
    {
        if (movie == null || streamIndex == audioStream || !movie.Audio.Any(a => a.Index == streamIndex)) return;
        audioStream = streamIndex;
        ms.AudioStream = streamIndex;
        Restart(session?.Now ?? 0);
        Toast("Звук: " + movie.Audio.First(a => a.Index == audioStream), 3000);
    }

    public void NextAudio()
    {
        if (movie == null || movie.Audio.Count < 2) { Toast("В фильме одна звуковая дорожка"); return; }
        int idx = movie.Audio.FindIndex(a => a.Index == audioStream);
        SetAudio(movie.Audio[(idx + 1) % movie.Audio.Count].Index);
    }

    public void SetLayout(StereoLayout l)
    {
        if (movie == null && captureDevice == null) return;
        layout = l;
        if (ms != null) ms.StereoLayout = l;
        var auto = movie != null ? movie.AutoLayout : captureLayout;
        Toast("Формат: " + Stereo.Name(l) + (l == StereoLayout.Auto ? $" ({Stereo.Name(auto)})" : ""));
    }

    public void SetMode(OutMode m)
    {
        outMode = m;
        settings.OutMode = m;
        Toast("Режим: " + Stereo.Name(m));
    }

    public void SetSwap(bool on)
    {
        swapEyes = on;
        if (test) settings.TestSwap = on; else if (ms != null) ms.Swap = on;
        Toast(on ? "Глаза поменяны местами" : "Глаза: обычный порядок");
    }

    public void SetVolume(float v)
    {
        settings.Volume = Math.Clamp(v, 0f, 1f);
        var s = session;
        if (s != null) s.Volume = settings.Volume;
        Toast($"Громкость {settings.Volume * 100:0}%", 1000);
    }

    public void ToggleTest()
    {
        var s = session;
        test = !test;
        if (test) { s?.SetPaused(true); swapEyes = settings.TestSwap; Toast("Тест очков. S — поменять глаза, T или Esc — к фильму", 5000); }
        else { swapEyes = ms?.Swap ?? false; s?.SetPaused(false); }
    }

    /// <summary>Клавиши (с окна плеера и с пульта).</summary>
    public void HandleKey(Keys k)
    {
        switch (k)
        {
            case Keys.Escape:
                if (test && movie != null) ToggleTest();
                else Close();
                break;
            case Keys.Space: TogglePause(); break;
            case Keys.Right: SeekBy(10); break;
            case Keys.Left: SeekBy(-10); break;
            case Keys.PageUp: SeekBy(60); break;
            case Keys.PageDown: SeekBy(-60); break;
            case Keys.Up: SetVolume(settings.Volume + 0.1f); break;
            case Keys.Down: SetVolume(settings.Volume - 0.1f); break;
            case Keys.S: SetSwap(!swapEyes); break;
            case Keys.L:
                SetLayout(layout switch { StereoLayout.Auto => StereoLayout.Sbs, StereoLayout.Sbs => StereoLayout.Ou, StereoLayout.Ou => StereoLayout.Mono, _ => StereoLayout.Auto });
                break;
            case Keys.A: NextAudio(); break;
            case Keys.D1: case Keys.NumPad1: SetMode(OutMode.Dlp); break;
            case Keys.D2: case Keys.NumPad2: SetMode(OutMode.Mono2D); break;
            case Keys.D3: case Keys.NumPad3: SetMode(OutMode.Anaglyph); break;
            case Keys.T: ToggleTest(); break;
            case Keys.I: showDiag = !showDiag; break;
            case Keys.OemOpenBrackets:
            case Keys.OemCloseBrackets:
                settings.AvOffsetMs += k == Keys.OemCloseBrackets ? 20 : -20;
                Toast($"Сдвиг звука: {settings.AvOffsetMs:+0;-0;0} мс", 1200);
                break;
            default: return;
        }
        Activity();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        HandleKey(e.KeyCode);
        e.Handled = true;
    }

    /// <summary>Мышь сдвинулась или нажата клавиша — показать курсор и пульт.</summary>
    public void Activity()
    {
        lastActivity = Environment.TickCount64;
        Cursor = Cursors.Default;
        Ctl?.Reveal();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Math.Abs(e.X - lastMouse.X) + Math.Abs(e.Y - lastMouse.Y) > 3) { lastMouse = e.Location; Activity(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Activity();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        TogglePause();
    }

    void SavePosition()
    {
        var s = session;
        if (movie != null && s != null && !test) ms.Pos = s.Now;
        settings.Screen = disp.Device;
        settings.Save();
    }

    bool closing;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (closing) { base.OnFormClosing(e); return; } // повторное закрытие — уже всё освобождено
        closing = true;
        SavePosition();
        running = false;
        uiTimer?.Stop();
        renderThread?.Join(2000);
        session?.Dispose();
        session = null;
        Ctl?.CloseFromPlayer();
        Log.Write($"Стоп: presents={presents} missed={missed} parityFixes={parityFixes} hz={hz:F2}");
        base.OnFormClosing(e);
    }
}
