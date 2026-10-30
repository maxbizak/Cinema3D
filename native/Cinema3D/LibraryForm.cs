namespace Cinema3D;

/// <summary>Главное окно: список фильмов, выбор проектора, режима, формата и звуковой дорожки.</summary>
public sealed class LibraryForm : Form
{
    static readonly string[] Exts = { ".mkv", ".mp4", ".m4v", ".avi", ".ts", ".m2ts", ".mov", ".webm", ".wmv" };

    readonly Settings settings;
    readonly ComboBox screenBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly RadioButton rbDlp = new() { Text = "DLP 3D (очки)", AutoSize = true };
    readonly RadioButton rb2d = new() { Text = "2D", AutoSize = true };
    readonly RadioButton rbAna = new() { Text = "Анаглиф", AutoSize = true };
    readonly ListView list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
    readonly ComboBox layoutBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    readonly ComboBox audioBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly CheckBox swapBox = new() { Text = "Поменять глаза", AutoSize = true };
    readonly Label status = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(8, 6, 8, 0), ForeColor = Color.DimGray };
    readonly List<Movie> movies = new();
    bool loadingSelection;

    public LibraryForm(Settings s)
    {
        settings = s;
        Text = "3D Кинотеатр";
        Font = new Font("Segoe UI", 10f);
        Size = new Size(1080, 680);
        MinimumSize = new Size(800, 480);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // --- верх: проектор и режим ---
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 10, 8, 0), WrapContents = false };
        top.Controls.Add(new Label { Text = "Проектор:", AutoSize = true, Margin = new Padding(0, 4, 4, 0) });
        top.Controls.Add(screenBox);
        top.Controls.Add(new Label { Text = "   Режим:", AutoSize = true, Margin = new Padding(12, 4, 4, 0) });
        top.Controls.Add(rbDlp); top.Controls.Add(rb2d); top.Controls.Add(rbAna);

        // --- низ: настройки фильма и кнопки ---
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 154, ColumnCount = 1, RowCount = 3, Padding = new Padding(8, 4, 8, 4) };
        var row1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row1.Controls.Add(new Label { Text = "Формат файла:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        row1.Controls.Add(layoutBox);
        row1.Controls.Add(new Label { Text = "  Звук:", AutoSize = true, Margin = new Padding(12, 6, 4, 0) });
        row1.Controls.Add(audioBox);
        row1.Controls.Add(swapBox);
        var row2 = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var play = Btn("▶  Смотреть", 160, true);
        var testBtn = Btn("Тест очков", 120);
        var openBtn = Btn("Открыть файл…", 140);
        var folderBtn = Btn("Папка с фильмами", 160);
        var refreshBtn = Btn("Обновить", 110);
        var urlBtn = Btn("Открыть по ссылке…", 180);
        var capBtn = Btn("Из браузера (VK, YouTube)…", 230);
        row2.Controls.AddRange(new Control[] { play, testBtn, openBtn, folderBtn, refreshBtn });
        var row3 = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row3.Controls.AddRange(new Control[] { urlBtn, capBtn });
        capBtn.Click += (_, _) => StartCapture();
        bottom.Controls.Add(row1); bottom.Controls.Add(row2); bottom.Controls.Add(row3);

        list.Columns.Add("Фильм", 470);
        list.Columns.Add("Формат", 130);
        list.Columns.Add("Длительность", 110);
        list.Columns.Add("Звук", 120);
        list.Columns.Add("Продолжить с", 110);
        list.Columns.Add("Папка", 80);

        Controls.Add(list);
        Controls.Add(bottom);
        Controls.Add(top);
        Controls.Add(status);

        foreach (var l in new[] { StereoLayout.Auto, StereoLayout.Sbs, StereoLayout.Ou, StereoLayout.Mono }) layoutBox.Items.Add(Stereo.Name(l));

        // --- события ---
        rbDlp.Checked = s.OutMode == OutMode.Dlp; rb2d.Checked = s.OutMode == OutMode.Mono2D; rbAna.Checked = s.OutMode == OutMode.Anaglyph;
        rbDlp.CheckedChanged += (_, _) => SaveMode(); rb2d.CheckedChanged += (_, _) => SaveMode(); rbAna.CheckedChanged += (_, _) => SaveMode();
        screenBox.SelectedIndexChanged += (_, _) =>
        {
            if (screenBox.SelectedItem is DisplayInfo d) { settings.Screen = d.Device; settings.Save(); UpdateStatus(); }
        };
        list.SelectedIndexChanged += (_, _) => ShowSelection();
        list.DoubleClick += (_, _) => PlaySelected();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) PlaySelected(); };
        layoutBox.SelectedIndexChanged += (_, _) =>
        {
            if (loadingSelection || Selected is not Movie m) return;
            settings.For(m).StereoLayout = (StereoLayout)layoutBox.SelectedIndex; settings.Save(); RefreshRow(m);
        };
        audioBox.SelectedIndexChanged += (_, _) =>
        {
            if (loadingSelection || Selected is not Movie m || audioBox.SelectedIndex < 0 || audioBox.SelectedIndex >= m.Audio.Count) return;
            settings.For(m).AudioStream = m.Audio[audioBox.SelectedIndex].Index; settings.Save();
        };
        swapBox.CheckedChanged += (_, _) =>
        {
            if (loadingSelection || Selected is not Movie m) return;
            settings.For(m).Swap = swapBox.Checked; settings.Save();
        };
        play.Click += (_, _) => PlaySelected();
        testBtn.Click += (_, _) => RunPlayer(null, true);
        openBtn.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Title = "Открыть 3D-фильм", Filter = "Видео|" + string.Join(";", Exts.Select(e => "*" + e)) + "|Все файлы|*.*" };
            if (dlg.ShowDialog(this) == DialogResult.OK) PlayFile(dlg.FileName);
        };
        folderBtn.Click += (_, _) =>
        {
            var dir = Path.Combine(App.Root, "movies");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        };
        refreshBtn.Click += (_, _) => { LoadScreens(); LoadMovies(); };
        urlBtn.Click += (_, _) => AskUrl();
        // Ctrl+V в окне — сразу открыть скопированную ссылку
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.V && ClipboardUrl() is string u) { e.Handled = true; OpenUrl(u); }
        };
        DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) PlayFile(f[0]); };

        LoadScreens();
        LoadMovies();
        // Windows могла переименовать экраны или изменить частоту — обновлять список при возврате в окно
        Activated += (_, _) => LoadScreens();
    }

    static Button Btn(string text, int w, bool accent = false)
    {
        var b = new Button { Text = text, Width = w, Height = 38, Margin = new Padding(0, 6, 10, 0) };
        if (accent) { b.BackColor = Color.FromArgb(47, 111, 219); b.ForeColor = Color.White; b.FlatStyle = FlatStyle.Flat; }
        return b;
    }

    Movie Selected => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as Movie : null;

    void SaveMode()
    {
        settings.OutMode = rbAna.Checked ? OutMode.Anaglyph : rb2d.Checked ? OutMode.Mono2D : OutMode.Dlp;
        settings.Save();
        UpdateStatus();
    }

    void LoadScreens()
    {
        screenBox.Items.Clear();
        var all = Displays.All();
        foreach (var d in all) screenBox.Items.Add(d);
        var pick = Displays.PickProjector(settings.Screen);
        screenBox.SelectedItem = all.FirstOrDefault(d => d.Device == pick.Device);
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (screenBox.SelectedItem is not DisplayInfo d) return;
        if (settings.OutMode == OutMode.Dlp && d.Hz is < 110 or > 130)
        {
            status.ForeColor = Color.Firebrick;
            status.Text = $"Проектор работает на {d.Hz} Гц. Для DLP-очков нужно 120 Гц (Параметры → Дисплей → Дополнительные параметры дисплея).";
        }
        else
        {
            status.ForeColor = Color.DimGray;
            status.Text = "Двойной щелчок по фильму — смотреть. Во время просмотра: I — диагностика, S — поменять глаза, A — звуковая дорожка, Esc — выход.";
        }
    }

    void LoadMovies()
    {
        list.Items.Clear();
        movies.Clear();
        foreach (var folder in new[] { "movies", "media" })
        {
            var dir = Path.Combine(App.Root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f))
                movies.Add(Movie.FromFile(f, folder));
        }
        foreach (var m in movies)
        {
            var it = new ListViewItem(new[] { m.Name, "…", "", "", "", m.Folder }) { Tag = m };
            list.Items.Add(it);
        }
        if (list.Items.Count > 0) list.Items[0].Selected = true;
        if (movies.Count == 0) status.Text = "Фильмов нет. Положите файлы 3D-фильмов (SBS или OU) в папку movies и нажмите «Обновить».";

        var toProbe = movies.ToList();
        Task.Run(() =>
        {
            foreach (var m in toProbe)
            {
                m.Probe();
                try { BeginInvoke(() => { RefreshRow(m); if (Selected == m) ShowSelection(); }); } catch { return; }
            }
        });
    }

    void RefreshRow(Movie m)
    {
        var it = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag == m);
        if (it == null || !m.Probed) return;
        var st = settings.For(m);
        var lay = st.StereoLayout == StereoLayout.Auto ? m.AutoLayout : st.StereoLayout;
        it.SubItems[1].Text = m.Error != "" ? "ошибка" : Stereo.Name(lay) + (st.StereoLayout == StereoLayout.Auto ? " (авто)" : "");
        it.SubItems[2].Text = Fmt(m.Duration);
        it.SubItems[3].Text = m.Audio.Count == 0 ? "нет" : m.Audio.Count == 1 ? "1 дорожка" : $"{m.Audio.Count} дорожки";
        it.SubItems[4].Text = st.Pos > 5 ? Fmt(st.Pos) : "";
    }

    void ShowSelection()
    {
        loadingSelection = true;
        try
        {
            audioBox.Items.Clear();
            if (Selected is not Movie m) return;
            var st = settings.For(m);
            layoutBox.SelectedIndex = (int)st.StereoLayout;
            swapBox.Checked = st.Swap;
            foreach (var a in m.Audio) audioBox.Items.Add(a.ToString());
            int want = st.AudioStream >= 0 && m.Audio.Any(a => a.Index == st.AudioStream) ? st.AudioStream : m.DefaultAudio();
            audioBox.SelectedIndex = m.Audio.FindIndex(a => a.Index == want);
            audioBox.Enabled = m.Audio.Count > 1;
        }
        finally { loadingSelection = false; }
    }

    static string Fmt(double s)
    {
        if (s <= 0) return "";
        var t = TimeSpan.FromSeconds(Math.Floor(s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    void PlaySelected()
    {
        if (Selected is Movie m) RunPlayer(m, false);
    }

    /// <summary>
    /// Для DLP фильм должен идти на проектор (~120 Гц). Если выбран другой экран — спросить,
    /// чтобы фильм случайно не открылся на весь экран основного монитора.
    /// </summary>
    bool ConfirmProjector(DisplayInfo d)
    {
        if (settings.OutMode != OutMode.Dlp || Displays.Is120(d)) return true;
        return MessageBox.Show(this,
            $"Для фильма выбран экран {d}.\nЭто не похоже на проектор: для DLP-очков нужно 120 Гц.\n\n" +
            "Всё равно открыть фильм на этом экране?\n(Выбрать проектор можно вверху окна, в списке «Проектор».)",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    static string ClipboardUrl()
    {
        try
        {
            var t = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            return t.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !t.Contains(' ') ? t : null;
        }
        catch { return null; }
    }

    /// <summary>Окно «вставьте ссылку» (подставляет ссылку из буфера обмена).</summary>
    void AskUrl()
    {
        using var f = new Form { Text = "Открыть по ссылке", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(640, 130), Font = Font };
        var lbl = new Label { Text = "Ссылка на ролик (YouTube, VK Видео и др.). Ролик с двумя картинками рядом или друг над другом:",
            Left = 12, Top = 12, Width = 616, Height = 22 };
        var box = new TextBox { Left = 12, Top = 40, Width = 616, Text = ClipboardUrl() ?? "" };
        var ok = new Button { Text = "Смотреть", Left = 412, Top = 82, Width = 110, Height = 32, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", Left = 528, Top = 82, Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(this) == DialogResult.OK && box.Text.Trim() != "") OpenUrl(box.Text.Trim());
    }

    /// <summary>Узнать адреса потоков через yt-dlp (в фоне) и запустить просмотр.</summary>
    async void OpenUrl(string url)
    {
        status.ForeColor = Color.DimGray;
        status.Text = "Получаю видео по ссылке… (обычно 5–15 секунд)";
        UseWaitCursor = true;
        Enabled = false;
        string err = "";
        var m = await Task.Run(() => Movie.FromUrl(url, out err));
        Enabled = true;
        UseWaitCursor = false;
        UpdateStatus();
        if (m == null)
        {
            MessageBox.Show(this, "Не удалось открыть ссылку.\n\n" + Short(err) +
                "\n\nПроверьте интернет. Если сайт недавно обновился — обновите загрузчик (README, раздел «Видео по ссылке»).", Text);
            return;
        }
        RunPlayer(m, false);
    }

    static string Short(string s) => s.Length > 600 ? s[..600] + "…" : s;

    /// <summary>
    /// Видео из браузера: программа снимает картинку с выбранного монитора, где в браузере
    /// идёт ролик на весь экран, и выводит её на проектор как 3D.
    /// </summary>
    void StartCapture()
    {
        LoadScreens();
        if (screenBox.SelectedItem is not DisplayInfo proj || !ConfirmProjector(proj)) return;
        var others = Displays.All().Where(d => d.Device != proj.Device).ToList();
        if (others.Count == 0)
        {
            MessageBox.Show(this, "Нужен второй экран для браузера. На ноутбуке: Win+P → «Расширить», " +
                "браузер на экране ноутбука, фильм — на проекторе.", Text);
            return;
        }
        using var f = new Form { Text = "Видео из браузера", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(620, 250), Font = Font };
        var info = new Label { Left = 12, Top = 12, Width = 596, Height = 120, Text =
            "1. Откройте ролик в браузере (VK, YouTube и др.) на мониторе, выбранном ниже.\n" +
            "2. Разверните видео в браузере на весь экран (кнопка ⛶ или клавиша F) и нажмите Play.\n" +
            "3. Программа покажет его на проекторе в 3D и сама определит формат (рядом / друг над другом).\n" +
            "Звук идёт из браузера. Видео с защитой от записи экрана будет чёрным." };
        var box = new ComboBox { Left = 12, Top = 140, Width = 596, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var d in others) box.Items.Add(d);
        box.SelectedIndex = Math.Max(0, others.FindIndex(d => d.Screen.Primary));
        var ok = new Button { Text = "Начать", Left = 392, Top = 196, Width = 110, Height = 34, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", Left = 508, Top = 196, Width = 100, Height = 34, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { info, box, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK || box.SelectedItem is not DisplayInfo src) return;

        settings.Screen = proj.Device;
        settings.Save();
        Hide();
        using (var p = new PlayerForm(settings, null, false, new Options { Capture = src.Device, Screen = proj.Device })) p.ShowDialog();
        settings.Save();
        Show();
        Activate();
    }

    void PlayFile(string path)
    {
        var m = movies.FirstOrDefault(x => string.Equals(x.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                ?? Movie.FromFile(path, Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))));
        RunPlayer(m, false);
    }

    void RunPlayer(Movie m, bool test)
    {
        if (m != null)
        {
            if (!m.Probed) { Cursor = Cursors.WaitCursor; m.Probe(); Cursor = Cursors.Default; }
            if (m.Error != "") { MessageBox.Show(this, $"Не удалось открыть «{m.Name}»: {m.Error}", Text); return; }
        }
        if (screenBox.SelectedItem is not DisplayInfo d || !ConfirmProjector(d)) return;
        settings.Screen = d.Device;
        settings.Save();
        Hide();
        using (var p = new PlayerForm(settings, m, test, new Options { Screen = d.Device })) p.ShowDialog();
        settings.Save();
        Show();
        Activate();
        if (m != null) RefreshRow(m);
        ShowSelection();
    }
}
