using Lumo.Core;
using Lumo.Core.Engines;

namespace Lumo.App;

/// <summary>历史记录窗口。</summary>
internal sealed class HistoryForm : Form
{
    private readonly AppContext _ctx;
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10f) };
    private readonly TextBox _search = new() { Dock = DockStyle.Fill };
    private readonly Button _star = new() { Text = "★ 收藏", FlatStyle = FlatStyle.Flat };
    private readonly Button _del = new() { Text = "删除", FlatStyle = FlatStyle.Flat };
    private readonly Action<HistoryEntry> _onPick;

    public HistoryForm(AppContext ctx, Action<HistoryEntry> onPick)
    {
        _ctx = ctx;
        _onPick = onPick;

        Text = "翻译历史";
        ClientSize = new Size(520, 460);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9f);
        MinimumSize = new Size(360, 300);

        _search.PlaceholderText = "搜索原文或译文…";
        _search.TextChanged += (_, _) => Refresh_();
        _list.DoubleClick += (_, _) => PickSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();

        _star.Click += (_, _) => ToggleStar();
        _del.Click += (_, _) => DeleteSelected();

        var searchBar = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 6, 8, 6) };
        searchBar.Controls.Add(_search);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 4, 8, 4),
            FlowDirection = FlowDirection.RightToLeft,
        };
        buttons.Controls.Add(_del);
        buttons.Controls.Add(_star);
        var clear = new FlatButton { Text = "清空未收藏", Width = 108, Height = 30 };
        clear.Click += (_, _) => ClearAll();
        buttons.Controls.Add(clear);

        Controls.Add(_list);
        Controls.Add(searchBar);
        Controls.Add(buttons);

        Refresh_();
    }

    private void Refresh_()
    {
        var entries = _ctx.HistoryStore.Search(_search.Text);
        _list.Items.Clear();
        foreach (var e in entries)
        {
            _list.Items.Add(new Row(e));
        }
        UpdateButtons();
    }

    private HistoryEntry? Selected =>
        _list.SelectedItem is Row r ? r.Entry : null;

    private void UpdateButtons()
    {
        var e = Selected;
        _star.Enabled = e is not null;
        _del.Enabled = e is not null;
        _star.Text = e is { Starred: true } ? "★ 已收藏" : "☆ 收藏";
    }

    private void PickSelected()
    {
        if (Selected is not { } e) return;
        _onPick(e);
        Close();
    }

    private void ToggleStar()
    {
        if (Selected is not { } e) return;
        try { _ctx.HistoryStore.ToggleStar(e.Id); Refresh_(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }

    private void DeleteSelected()
    {
        if (Selected is not { } e) return;
        try { _ctx.HistoryStore.Remove(e.Id); Refresh_(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }

    private void ClearAll()
    {
        if (MessageBox.Show(this, "清空所有未收藏的翻译历史？此操作不可撤销。",
                "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;
        try
        {
            var n = _ctx.HistoryStore.ClearUnstarred();
            Refresh_();
            MessageBox.Show(this, n > 0 ? $"已清除 {n} 条。" : "没有可清除的记录。",
                "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }

    private sealed class Row
    {
        public Row(HistoryEntry entry) => Entry = entry;
        public HistoryEntry Entry { get; }

        public override string ToString()
        {
            var e = Entry;
            var star = e.Starred ? "★ " : "";
            var when = TimeAgo(e.Timestamp);
            return $"{star}{Truncate(e.SourceText, 34)}   →   {Truncate(e.TargetText, 34)}   [{Lang.DisplayName(e.From)}→{Lang.DisplayName(e.To)} · {e.Engine} · {when}]";
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..(max - 1)] + "…";

        private static string TimeAgo(long unix)
        {
            var diff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix;
            if (diff < 60) return "刚刚";
            if (diff < 3600) return $"{diff / 60} 分钟前";
            if (diff < 86400) return $"{diff / 3600} 小时前";
            if (diff < 604800) return $"{diff / 86400} 天前";
            return DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("MM-dd");
        }
    }
}

/// <summary>设置窗口。按引擎动态渲染需要 Key / 模型 / 模板的字段。</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppContext _ctx;
    private readonly Action? _onSaved;
    private readonly Dictionary<string, Control> _editors = new(StringComparer.Ordinal);
    private readonly Panel _engineFields = new() { Dock = DockStyle.Fill, AutoScroll = true };

    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };

    /// <param name="onSaved">点「保存」后回调。</param>
    public SettingsForm(AppContext ctx, Action? onSaved = null)
    {
        _ctx = ctx;
        _onSaved = onSaved;
        Text = "Lumo 设置";
        ClientSize = new Size(560, 620);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9f);
        MinimumSize = new Size(460, 420);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildEngineTab());
        tabs.TabPages.Add(BuildHotkeyTab());
        tabs.TabPages.Add(BuildHelpTab());
        tabs.TabPages.Add(BuildAboutTab());

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8, 8, 12, 8),
            FlowDirection = FlowDirection.RightToLeft,
        };
        // 用 FlatButton 而不是裸 Button：裸的 FlatStyle.Flat 只画三边框，
        // 而且高度 28 在 9pt 雅黑下会把字的上下沿切掉 —— 用户看到的就是「字不全」。
        var save = new FlatButton { Text = "保存", IsPrimary = true, Width = 92, Height = 32 };
        save.Click += (_, _) => Save();
        var cancel = new FlatButton { Text = "取消", Width = 80, Height = 32 };
        cancel.Click += (_, _) => { Close(); };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);

        Controls.Add(tabs);
        Controls.Add(buttons);
        Controls.Add(_status);
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("常规") { Padding = new Padding(10) };
        var form = NewForm();
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 说明列按内容
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // 控件列吃剩余

        var cfg = _ctx.Config;

        AddField(form, "默认目标语言", "",
            MakeSelectField(
                Lang.Common.Select(l => l.Code).ToList(),
                Lang.Common.ToDictionary(l => l.Code, l => l.Name),
                cfg.TargetLang,
                v => cfg.TargetLang = v));

        AddField(form, "自动判断翻译方向", "原文是外语就译成中文，是中文就译成外语",
            MakeCheckField(cfg.AutoTarget, v => cfg.AutoTarget = v));

        AddField(form, "监听剪贴板", "开启后复制任何文字都会自动翻译",
            MakeCheckField(cfg.ListenClipboard, v => cfg.ListenClipboard = v));

        AddField(form, "剪贴板防抖（毫秒）", "", MakeNumberField(cfg.ClipboardDebounceMs, 50, 3000, v => cfg.ClipboardDebounceMs = v));

        AddField(form, "自动复制译文", "翻译完成后直接复制到剪贴板",
            MakeCheckField(cfg.AutoCopy, v => cfg.AutoCopy = v));

        AddField(form, "保存翻译历史", "方便回看之前翻译过的内容",
            MakeCheckField(cfg.SaveHistory, v => cfg.SaveHistory = v));

        AddField(form, "历史条数上限", "", MakeNumberField(cfg.HistoryLimit, 0, 10000, v => cfg.HistoryLimit = v));

        AddField(form, "窗口置顶", "翻译时不被其他窗口盖住",
            MakeCheckField(cfg.StayOnTop, v => cfg.StayOnTop = v));

        AddField(form, "窗口不透明度（%）", "", MakeNumberField((int)(cfg.Opacity * 100), 30, 100, v => cfg.Opacity = v / 100.0));

        AddField(form, "关闭时最小化到托盘", "关掉窗口后继续在后台运行",
            MakeCheckField(cfg.CloseToTray, v => cfg.CloseToTray = v));

        AddField(form, "开机自动启动", "登录 Windows 后自动运行（需自行创建启动项）",
            MakeCheckField(cfg.LaunchOnBoot, v => cfg.LaunchOnBoot = v));

        AddField(form, "代理地址", "例如 http://127.0.0.1:7890，留空则不用",
            MakeTextField(cfg.Proxy ?? "", v => cfg.Proxy = string.IsNullOrWhiteSpace(v) ? null : v));

        AddField(form, "请求超时（秒）", "", MakeNumberField(cfg.HttpTimeoutSecs, 3, 120, v => cfg.HttpTimeoutSecs = v));

        page.Controls.Add(form);
        return page;
    }

    /// <summary>
    /// 往表单里加一行「标题+说明 | 控件」。
    /// </summary>
    /// <remarks>
    /// 之前所有字段都塞进 <c>FlowLayoutPanel</c>，靠手算控件高度来排版。
    /// 那样做必然会出事：控件的实际高度随字体、DPI、系统主题变，
    /// 一旦超出算好的行高就会被下一行的面板压住 —— 表现就是「灰色注释被盖住」。
    /// 改用 <c>TableLayoutPanel</c>：行高由引擎分配，不需要我们猜。
    /// </remarks>
    private void AddField(TableLayoutPanel form, string label, string hint, Control editor)
    {
        var row = form.RowCount++;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 第 0 列：标题在上，说明在下，用一个容器竖排
        var labelBox = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = hint.Length > 0 ? 2 : 1,
            Margin = new Padding(0, 2, 8, 2),
        };
        labelBox.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (hint.Length > 0)
        {
            labelBox.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            labelBox.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            labelBox.Controls.Add(new Label
            {
                Text = label, AutoSize = true, Anchor = AnchorStyles.Left,
            }, 0, 0);
            labelBox.Controls.Add(new Label
            {
                Text = hint, AutoSize = true, ForeColor = Theme.TextDim,
                Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 0, 0),
            }, 0, 1);
        }
        else
        {
            labelBox.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            labelBox.Controls.Add(new Label
            {
                Text = label, AutoSize = true, Anchor = AnchorStyles.Left,
            }, 0, 0);
        }

        editor.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        editor.Margin = new Padding(0, 2, 0, 2);
        form.Controls.Add(labelBox, 0, row);
        form.Controls.Add(editor, 1, row);
    }

    /// <summary>新建一个两列的设置表单：说明列 + 控件列。</summary>
    private static TableLayoutPanel NewForm() => new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        ColumnCount = 2,
        RowCount = 0,
        Padding = new Padding(6, 4, 10, 4),
    };

    private TabPage BuildEngineTab()
    {
        var page = new TabPage("引擎") { Padding = new Padding(10) };
        var form = NewForm();
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var cfg = _ctx.Config;

        // 引擎下拉单独占一行：切换时要重建下面的字段区，放进表格不好处理
        var picker = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            Width = 220, Height = 26,
        };
        foreach (var e in EngineFactory.Available) picker.Items.Add(new EngineItem(e));
        SelectByCode(picker, cfg.DefaultEngine);
        AddField(form, "默认引擎", "切换后下方会显示该引擎需要的配置项", picker);
        picker.SelectedIndexChanged += (_, _) => RenderEngineFields();

        // 引擎专属字段容器：放在表格外，用 Dock 挂到页面底部
        _engineFields.Dock = DockStyle.Bottom;
        _engineFields.Height = 320;
        _engineFields.AutoScroll = true;
        _engineFields.Padding = new Padding(6, 4, 10, 4);

        page.Controls.Add(form);
        page.Controls.Add(_engineFields);
        RenderEngineFields();
        return page;
    }

    private void RenderEngineFields()
    {
        _engineFields.SuspendLayout();
        _engineFields.Controls.Clear();
        _editors.Clear();

        var cfg = _ctx.Config;
        var form = NewForm();
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var current = EngineFactory.Available.FirstOrDefault(e => e.Id == cfg.DefaultEngine);
        var ec = cfg.EngineConfigs.GetValueOrDefault(current?.Id ?? EngineIds.Google) ?? new EngineConfig();
        cfg.EngineConfigs[current?.Id ?? EngineIds.Google] = ec;

        AddField(form, "失败时回退到", "主引擎失败时自动改用，留空则不启用",
            MakeSelectField(
                new List<string> { "" }.Concat(EngineFactory.Available.Select(e => e.Id)).ToList(),
                new Dictionary<string, string> { [""] = "不启用" }
                    .Concat(EngineFactory.Available.ToDictionary(e => e.Id, e => e.Name)).ToDictionary(k => k.Key, v => v.Value),
                cfg.FallbackEngines.FirstOrDefault() ?? "",
                id => cfg.FallbackEngines = string.IsNullOrEmpty(id) ? new List<string>() : new List<string> { id }));

        if (current is { RequiresKey: true })
        {
            AddField(form, $"{current.Name} API Key", "在官网申请后粘贴，密钥只保存在本地",
                MakeSecretField(ec.Key ?? "", v => ec.Key = v));
        }

        switch (current?.Id)
        {
            case EngineIds.OpenAi:
                AddField(form, "接口地址", "留空用 api.openai.com；填 https://api.deepseek.com/v1 可切到 DeepSeek",
                    MakeTextField(ec.BaseUrl ?? "", v => ec.BaseUrl = v));
                AddField(form, "模型名称", "例如 gpt-4o-mini、deepseek-chat",
                    MakeTextField(ec.Get("model") ?? "", v => ec.Extra["model"] = v));
                AddField(form, "本地服务", "Ollama / LM Studio 这类本地服务不需要 Key",
                    MakeCheckField(ec.Get("local") == "1", v => ec.Extra["local"] = v ? "1" : ""));
                break;

            case EngineIds.Custom:
                AddField(form, "请求地址", "支持占位符：{{text}} {{from}} {{to}} {{key}}",
                    MakeTextField(ec.Get("url") ?? "", v => ec.Extra["url"] = v));
                AddField(form, "请求方法", "GET / POST / PUT / PATCH",
                    MakeTextField(ec.Get("method") ?? "POST", v => ec.Extra["method"] = v.ToUpperInvariant()));
                AddField(form, "请求头", "每行一个，例如 Authorization: Bearer {{key}}",
                    MakeTextField(ec.Get("headers") ?? "", v => ec.Extra["headers"] = v));
                AddField(form, "请求体模板", """{"q":"{{text}}","to":"{{to}}"}""",
                    MakeTextField(ec.Get("body") ?? "", v => ec.Extra["body"] = v));
                AddField(form, "结果取值路径", "留空取整段响应；如 {{json:data.translation}}",
                    MakeTextField(ec.Get("result") ?? "", v => ec.Extra["result"] = v));
                break;

            case EngineIds.Google:
                AddField(form, "Cookie（可选）", "填入浏览器里的 NID 可提高配额，留空即可",
                    MakeTextField(ec.Get("cookie") ?? "", v => ec.Extra["cookie"] = v));
                break;
        }

        _engineFields.Controls.Add(form);
        _engineFields.ResumeLayout(true);
    }

    private TabPage BuildHotkeyTab()
    {
        var page = new TabPage("快捷键") { Padding = new Padding(10) };
        var form = NewForm();
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var cfg = _ctx.Config;
        AddField(form, "翻译选中文字", "在任意程序里选中一段文字，按下这个快捷键",
            MakeTextField(cfg.HotkeyTrigger, v => cfg.HotkeyTrigger = v));
        AddField(form, "截图翻译", "框选屏幕区域并识别文字",
            MakeTextField(cfg.HotkeyOcr, v => cfg.HotkeyOcr = v));
        AddField(form, "显示 / 隐藏窗口", "快速唤出主界面",
            MakeTextField(cfg.HotkeyMini, v => cfg.HotkeyMini = v));

        page.Controls.Add(form);
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,
            Height = 66,
            Text = "格式示例：Alt+D、Ctrl+Shift+T、F2\r\n必须带修饰键（单独一个键会抢走正常输入）\r\n" +
                   "改完点保存生效；若与其他软件冲突，程序启动时会写日志提示",
            ForeColor = Theme.TextDim,
            Padding = new Padding(6, 8, 0, 0),
        });
        return page;
    }

    /// <summary>「使用说明」标签页。内容与首次启动弹窗共用同一份文案。</summary>
    /// <remarks>
    /// 说明只放在首次弹窗里是不够的 —— 用户当时不一定有心思看，
    /// 过几天想不起来怎么用就找不到地方了。这里给一个常驻入口。
    /// </remarks>
    private TabPage BuildHelpTab()
    {
        var page = new TabPage("使用说明") { Padding = new Padding(10) };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var body = HelpContent.CreateReadOnlyText(HelpContent.Format(_ctx.Config), 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 10, 0, 0),
        };
        var reopen = new FlatButton { Text = "重新显示首次提示", Width = 156, Height = 32 };
        reopen.Click += (_, _) =>
        {
            using var dlg = new HelpDialog(_ctx.Config);
            dlg.ShowDialog(this);
            if (dlg.DontShowAgain && _ctx.Config.ShowFirstRunHelp)
            {
                _ctx.Config.ShowFirstRunHelp = false;
                _onSaved?.Invoke();
            }
        };
        buttons.Controls.Add(reopen);

        layout.Controls.Add(body, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildAboutTab()
    {
        var page = new TabPage("关于") { Padding = new Padding(16) };
        var stats = _ctx.Translator.CacheStats();
        var langs = Win.Ocr.AvailableLanguages();
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text =
                "Lumo 翻译\r\n" +
                "轻量级 Windows 翻译工具\r\n\r\n" +
                $"配置目录\r\n{_ctx.ConfigDir}\r\n\r\n" +
                $"翻译缓存\r\n{stats.Entries} 条 · 约 {stats.Bytes / 1024} KB · 命中率 {stats.HitRate:P0}\r\n\r\n" +
                $"已安装 OCR 语言包\r\n{(langs.Count == 0 ? "（未检测到，建议在「设置→语言和区域」补装）" : string.Join("、", langs))}\r\n\r\n" +
                "截图翻译使用 Windows 自带 OCR，不捆绑任何模型文件。",
        });
        return page;
    }

    // ── 控件工厂 ──
    //
    // 全部只造控件本身，不负责排版。排版统一交给 AddField + TableLayoutPanel ——
    // 手算控件高度早晚会出问题（见 AddField 的注释）。

    private static TextBox MakeTextField(string value, Action<string> onChange)
    {
        var box = new TextBox { Text = value, Width = 220, Height = 26 };
        box.TextChanged += (_, _) => onChange(box.Text.Trim());
        return box;
    }

    private static TextBox MakeSecretField(string value, Action<string> onChange)
    {
        var box = new TextBox
        {
            Text = value, Width = 220, Height = 26, UseSystemPasswordChar = true,
        };
        box.TextChanged += (_, _) => onChange(box.Text.Trim());
        return box;
    }

    private static NumericUpDown MakeNumberField(int value, int min, int max, Action<int> onChange)
    {
        var box = new NumericUpDown
        {
            Width = 110, Height = 26,
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
        };
        box.ValueChanged += (_, _) => onChange((int)box.Value);
        return box;
    }

    private static CheckBox MakeCheckField(bool value, Action<bool> onChange)
    {
        var box = new CheckBox { Checked = value, Width = 24, Height = 24 };
        box.CheckedChanged += (_, _) => onChange(box.Checked);
        return box;
    }

    private static ComboBox MakeSelectField(
        List<string> values, Dictionary<string, string> names, string value, Action<string> onChange)
    {
        var box = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Width = 220, Height = 26,
        };
        foreach (var v in values)
            box.Items.Add(new Option(v, names.GetValueOrDefault(v, v)));
        SelectByCode(box, value);
        box.SelectedIndexChanged += (_, _) =>
        {
            if (box.SelectedItem is Option o) onChange(o.Code);
        };
        return box;
    }

    /// <summary>按 code 选中对应项；找不到就退回第一项。</summary>
    private static void SelectByCode(ComboBox box, string code)
    {
        foreach (var item in box.Items)
        {
            switch (item)
            {
                case Option o when o.Code == code:
                    box.SelectedItem = o;
                    return;
                case EngineItem e when e.Info.Id == code:
                    box.SelectedItem = e;
                    return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    /// <summary>
    /// 下拉选项。必须是<b>引用类型</b>。
    /// </summary>
    /// <remarks>
    /// 之前这里用 <c>KeyValuePair&lt;string,string&gt;</c>，那是 struct ——
    /// WinForms 的 ComboBox 对值类型集合的 SelectedItem 赋值/读取会抛
    /// InvalidCastException，表现为「点开设置就崩」。换成 class 就好了。
    /// </remarks>
    /// <summary>引擎下拉的选项项。</summary>
    private sealed class EngineItem
    {
        public EngineItem(EngineInfo info) => Info = info;
        public EngineInfo Info { get; }
        public override string ToString() => Info.Name;
    }

    private sealed class Option
    {
        public Option(string code, string name) { Code = code; Name = name; }
        public string Code { get; }
        public string Name { get; }
        public override string ToString() => Name;
    }

    private void Save()
    {
        try
        {
            _ctx.Save(_ctx.Config);
            // 通知主窗口：托盘开关的勾选状态、剪贴板定时器的启停都靠它同步。
            // 少了这一步，在设置里改「监听剪贴板」只会写进配置文件，
            // 运行中的定时器完全不受影响 —— 看起来改了，其实没生效。
            _onSaved?.Invoke();
            _status.Text = "设置已保存";
            MessageBox.Show(this, "设置已保存。快捷键改动需要重启程序后生效。",
                "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = $"保存失败：{ex.Message}";
            MessageBox.Show(this, $"保存失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
