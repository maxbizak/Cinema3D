namespace Cinema3D;

/// <summary>
/// Пульт во время просмотра: пауза, перемотка, звуковая дорожка, громкость, формат, глаза.
/// Если у компьютера есть второй экран, пульт стоит на нём всё время.
/// Если экран один (только проектор), пульт появляется при движении мыши и прячется через 3 секунды.
/// </summary>
public sealed class ControlForm : Form
{
    static readonly Color Bg = Color.FromArgb(24, 24, 30), Fg = Color.Gainsboro, Ctl = Color.FromArgb(44, 44, 54);

    readonly PlayerForm p;
    readonly bool pinned;
    readonly Button playBtn;
    readonly Label timeLabel = new() { AutoSize = false, Width = 150, Height = 30, TextAlign = ContentAlignment.MiddleCenter };
    readonly TrackBar seek = new() { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, AutoSize = false, Height = 30, Dock = DockStyle.Fill };
    readonly ComboBox audioBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    readonly ComboBox layoutBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    readonly ComboBox modeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    readonly CheckBox swapBox = new() { Text = "Поменять глаза", AutoSize = true, Margin = new Padding(10, 7, 6, 0) };
    readonly TrackBar volume = new() { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, AutoSize = false, Width = 110, Height = 28 };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    bool dragging, syncing, closingByPlayer;
    string displayedExternalAudio;
    readonly TableLayoutPanel row1;
    readonly FlowLayoutPanel row2;
    readonly Rectangle area;

    readonly bool hideWhenIdle;   // захват браузера: пульт на том же мониторе — прятать, чтобы не попал в кадр
    Point lastCursor;
    long lastMove = Environment.TickCount64;

    /// <summary>Пульт сейчас виден на мониторе, который снимает захват (в это время рамку видео не пересчитываем).</summary>
    public bool CoversCapture => hideWhenIdle && Visible;

    public ControlForm(PlayerForm player, Screen projector, Screen capture = null)
    {
        p = player;
        Text = "Пульт — 3D Кинотеатр";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = Bg; ForeColor = Fg;
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.Manual;

        // где стоять: другой экран, если он есть; иначе внизу экрана проектора
        var other = Screen.AllScreens.Where(s => s.DeviceName != projector.DeviceName && s.DeviceName != capture?.DeviceName)
                                     .OrderByDescending(s => s.Primary).FirstOrDefault();
        pinned = other != null;
        hideWhenIdle = !pinned && capture != null;
        lastCursor = Cursor.Position; // стартовое положение мыши — не «движение»
        area = (other ?? capture ?? projector).WorkingArea;
        ClientSize = new Size(Math.Min(1000, area.Width - 20), 96); // точный размер — в OnLoad, по содержимому

        // ---- строка 1: пауза, перемотка, время, ползунок ----
        playBtn = Btn("⏸ Пауза", 110, (_, _) => p.TogglePause());
        row1 = new TableLayoutPanel { Dock = DockStyle.Top, Height = 42, ColumnCount = 6, Padding = new Padding(6, 6, 6, 0) };
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var backBtn = Btn("⏪ 10 с", 80, (_, _) => p.SeekBy(-10));
        var fwdBtn = Btn("10 с ⏩", 80, (_, _) => p.SeekBy(10));
        row1.Controls.Add(playBtn, 0, 0);
        row1.Controls.Add(backBtn, 1, 0);
        row1.Controls.Add(fwdBtn, 2, 0);
        if (p.Capturing)
        {
            // видео ведёт плеер сайта в браузере: пауза и перемотка — там, здесь только 3D-настройки
            playBtn.Visible = backBtn.Visible = fwdBtn.Visible = seek.Visible = false;
            timeLabel.Width = 560;
            timeLabel.TextAlign = ContentAlignment.MiddleLeft;
        }
        row1.Controls.Add(timeLabel, 3, 0);
        row1.Controls.Add(seek, 4, 0);
        row1.Controls.Add(Btn("✕ Выход", 90, (_, _) => p.Close()), 5, 0);

        // ---- строка 2: в режиме браузера звук и воспроизведение контролирует сайт ----
        row2 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(6, 4, 6, 0) };
        if (!p.Capturing) { row2.Controls.Add(Lbl("Дорожка:")); row2.Controls.Add(audioBox); }
        row2.Controls.Add(Lbl("Формат:"));
        row2.Controls.Add(layoutBox);
        row2.Controls.Add(Lbl("Режим:"));
        row2.Controls.Add(modeBox);
        row2.Controls.Add(swapBox);
        if (!p.Capturing) { row2.Controls.Add(Lbl("Громкость:")); row2.Controls.Add(volume); }
        row2.Controls.Add(Btn("Тест очков", 100, (_, _) => p.ToggleTest()));
        if (p.Capturing) row2.Controls.Add(Btn("Сбросить рамку", 130, (_, _) => p.ResetCaptureFrame()));

        Controls.Add(row2);
        Controls.Add(row1);

        foreach (var cb in new[] { audioBox, layoutBox, modeBox }) { cb.BackColor = Ctl; cb.ForeColor = Fg; cb.FlatStyle = FlatStyle.Flat; }
        foreach (var l in new[] { StereoLayout.Auto, StereoLayout.Sbs, StereoLayout.Ou, StereoLayout.Mono }) layoutBox.Items.Add(Stereo.Name(l));
        foreach (var m in new[] { OutMode.Dlp, OutMode.Mono2D, OutMode.Anaglyph }) modeBox.Items.Add(Stereo.Name(m));
        displayedExternalAudio = p.ExternalAudio;
        if (displayedExternalAudio != null) audioBox.Items.Add("MP3/WAV: " + Path.GetFileName(displayedExternalAudio));
        if (p.Movie != null) foreach (var a in p.Movie.Audio) audioBox.Items.Add(a.ToString());
        if (audioBox.Items.Count == 0) { audioBox.Items.Add("нет звука"); audioBox.Enabled = false; }

        // ---- события ----
        audioBox.SelectedIndexChanged += (_, _) =>
        {
            if (syncing || p.Movie == null || audioBox.SelectedIndex < 0) return;
            int offset = p.ExternalAudio != null ? 1 : 0;
            if (audioBox.SelectedIndex < offset) return;
            int i = audioBox.SelectedIndex - offset;
            if (i < p.Movie.Audio.Count) p.SetAudio(p.Movie.Audio[i].Index);
        };
        layoutBox.SelectedIndexChanged += (_, _) => { if (!syncing) p.SetLayout((StereoLayout)layoutBox.SelectedIndex); };
        modeBox.SelectedIndexChanged += (_, _) => { if (!syncing) p.SetMode((OutMode)modeBox.SelectedIndex); };
        swapBox.CheckedChanged += (_, _) => { if (!syncing) p.SetSwap(swapBox.Checked); };
        volume.Scroll += (_, _) => { if (!syncing) p.SetVolume(volume.Value / 100f); };

        // ползунок: щелчок переносит прямо в точку, отпускание — перемотка
        seek.MouseDown += (_, e) =>
        {
            dragging = true;
            seek.Value = Math.Clamp((int)((e.X - 12) * 1000.0 / Math.Max(1, seek.Width - 24)), 0, 1000);
        };
        seek.MouseUp += (_, _) =>
        {
            if (p.Duration > 0) p.SeekTo(seek.Value / 1000.0 * p.Duration);
            dragging = false;
        };
        seek.Scroll += (_, _) => { if (p.Duration > 0) timeLabel.Text = $"{Fmt(seek.Value / 1000.0 * p.Duration)} / {Fmt(p.Duration)}"; };

        timer.Tick += (_, _) =>
        {
            Sync();
            if (!hideWhenIdle) return;
            // мышь сдвинулась где угодно — показать; 3 с покоя — спрятать (чтобы не попасть в захват)
            var c = Cursor.Position;
            if (Math.Abs(c.X - lastCursor.X) + Math.Abs(c.Y - lastCursor.Y) > 3) { lastCursor = c; lastMove = Environment.TickCount64; if (!Visible) Show(Owner); }
            else if (Visible && Environment.TickCount64 - lastMove > 3000 && !ContainsFocusOrMouse) Hide();
        };
        timer.Start();

        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - (pinned ? 40 : 16));
        Sync();
    }

    protected override bool ShowWithoutActivation => !pinned;

    /// <summary>
    /// Размер по содержимому. Считаем после показа окна: только тогда шрифты и элементы
    /// уже в масштабе своего экрана (например 125%). На узком экране вторая строка переносится.
    /// </summary>
    void FitToContent()
    {
        int frameW = Width - ClientSize.Width;
        int maxW = area.Width - 20 - frameW;
        int need = row2.Padding.Horizontal + 8;
        foreach (Control c in row2.Controls) need += c.Width + c.Margin.Horizontal;
        int w = Math.Min(Math.Max(need, LogicalToDeviceUnits(900)), maxW);
        row2.WrapContents = need > maxW;
        int rowH = 0;
        foreach (Control c in row2.Controls) rowH = Math.Max(rowH, c.Height + c.Margin.Vertical);
        int lines = need > maxW ? 2 : 1;
        ClientSize = new Size(w, row1.Height + lines * rowH + row2.Padding.Vertical + 6);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - (pinned ? 40 : 16));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(FitToContent);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitToContent();
        if (!pinned) Hide();
    }

    Button Btn(string text, int w, EventHandler click)
    {
        var b = new Button { Text = text, Width = w, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Ctl, ForeColor = Fg, Margin = new Padding(0, 0, 6, 0),
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly }; // текст не обрезается при масштабе 125–150%
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 82);
        b.Click += click;
        b.Click += (_, _) => p.Activity();
        return b;
    }

    static Label Lbl(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(8, 7, 4, 0) };

    static string Fmt(double s)
    {
        if (double.IsNaN(s) || s < 0) s = 0;
        var t = TimeSpan.FromSeconds(Math.Floor(s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>Подтянуть состояние плеера (его могли поменять клавишами).</summary>
    void Sync()
    {
        syncing = true;
        try
        {
            double dur = p.Duration, pos = p.Position;
            seek.Enabled = dur > 0;
            if (p.Capturing) timeLabel.Text = "Звук и воспроизведение — в браузере";
            else if (!dragging)
            {
                timeLabel.Text = $"{Fmt(pos)} / {Fmt(dur)}";
                seek.Value = dur > 0 ? Math.Clamp((int)(pos / dur * 1000), 0, 1000) : 0;
            }
            playBtn.Text = p.Paused ? "▶ Играть" : "⏸ Пауза";
            if (p.Movie != null && displayedExternalAudio != p.ExternalAudio)
            {
                displayedExternalAudio = p.ExternalAudio;
                audioBox.Items.Clear();
                if (displayedExternalAudio != null) audioBox.Items.Add("MP3/WAV: " + Path.GetFileName(displayedExternalAudio));
                foreach (var a in p.Movie.Audio) audioBox.Items.Add(a.ToString());
                audioBox.Enabled = audioBox.Items.Count > 1;
            }
            if (p.Movie != null && !audioBox.DroppedDown && !audioBox.Focused)
            {
                int i = p.ExternalAudio != null ? 0 : p.Movie.Audio.FindIndex(a => a.Index == p.AudioStream);
                if (i >= 0 && audioBox.SelectedIndex != i) audioBox.SelectedIndex = i;
            }
            if (!layoutBox.DroppedDown && layoutBox.SelectedIndex != (int)p.LayoutSetting) layoutBox.SelectedIndex = (int)p.LayoutSetting;
            if (!modeBox.DroppedDown && modeBox.SelectedIndex != (int)p.Mode) modeBox.SelectedIndex = (int)p.Mode;
            if (swapBox.Checked != p.Swap) swapBox.Checked = p.Swap;
            int v = (int)Math.Round(p.Volume * 100);
            if (volume.Value != v && !volume.Focused) volume.Value = Math.Clamp(v, 0, 100);
        }
        finally { syncing = false; }
    }

    /// <summary>Мышь над пультом или открыт выпадающий список — не прятать.</summary>
    public bool ContainsFocusOrMouse =>
        Visible && (Bounds.Contains(Cursor.Position) || audioBox.DroppedDown || layoutBox.DroppedDown || modeBox.DroppedDown || dragging);

    public void Reveal() { if (!pinned && !Visible) Show(Owner); }
    public void AutoHide() { if (!pinned && !hideWhenIdle && Visible) Hide(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // стрелки внутри открытого списка оставляем списку
        if (ActiveControl is ComboBox cb && (cb.DroppedDown || e.KeyCode is Keys.Up or Keys.Down)) return;
        p.HandleKey(e.KeyCode);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    public void CloseFromPlayer() { closingByPlayer = true; Close(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        timer.Stop();
        // крестик пульта закрывает фильм; закрываем плеер уже после выхода из этого обработчика,
        // иначе окно пульта закрывалось бы повторно изнутри собственного закрытия (была ошибка при выходе)
        // плеер закрывается сам (Windows сначала закрывает пульт как его «дочернее» окно) — просто закрываемся
        if (e.CloseReason == CloseReason.FormOwnerClosing) closingByPlayer = true;
        if (!closingByPlayer) { e.Cancel = true; BeginInvoke(() => { if (!p.IsDisposed) p.Close(); }); return; }
        base.OnFormClosing(e);
    }
}
