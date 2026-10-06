using System.Diagnostics;
using System.Runtime.InteropServices;
using Lumo.App.Win;
using Lumo.Core;
using Lumo.Core.Engines;

namespace Lumo.App;

/// <summary>全局共享状态。跨线程访问，字段本身只做读写，不需要再套锁。</summary>
internal sealed class AppContext
{
    public required string ConfigDir { get; init; }
    public required string LogPath { get; init; }
    public required ConfigStore ConfigStore { get; init; }
    public required HistoryStore HistoryStore { get; init; }
    public required Translator Translator { get; init; }

    private AppConfig _config = new();
    public AppConfig Config
    {
        get => _config;
        set => _config = value;
    }

    public void Reload()
    {
        _config = ConfigStore.Load();
        Translator.UpdateConfig(_config);
    }

    public void Save(AppConfig cfg)
    {
        ConfigStore.Save(cfg);
        _config = cfg;
        Translator.UpdateConfig(cfg);
        HistoryStore.SetLimit(cfg.HistoryLimit);
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 兜底异常处理：未捕获的异常默认只弹一个 .NET 的原始对话框，
        // 用户既看不懂也留不下线索。这里统一写成日志文件再给一句人话。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal("界面线程", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportFatal("后台线程", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumo");
        Directory.CreateDirectory(dir);

        var log = Path.Combine(dir, "lumo.log");
        var configStore = new ConfigStore(Path.Combine(dir, "config.json"));
        var config = configStore.Load();

        var ctx = new AppContext
        {
            ConfigDir = dir,
            LogPath = log,
            ConfigStore = configStore,
            HistoryStore = new HistoryStore(Path.Combine(dir, "history.json"), config.HistoryLimit),
            Translator = new Translator(config),
            Config = config,
        };

        Application.Run(new MainForm(ctx));
    }

    /// <summary>往日志文件追加一行。失败静默 —— 日志本身不该引发新问题。</summary>
    internal static void log_line(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumo");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "lumo.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不了就算了
        }
    }

    /// <summary>记录未捕获异常并提示用户。写日志失败也不能再抛。</summary>
    internal static void ReportFatal(string where, Exception? ex)
    {
        log_line($"[{where}] {ex?.ToString() ?? "(no exception object)"}{Environment.NewLine}");

        try
        {
            // 把堆栈也显示出来。之前只给一句「Specified cast is not valid」，
            // 光看这句话根本定位不到是哪一行，排查全靠猜。
            var message = ex is null
                ? "未知错误"
                : ex is InvalidCastException or NullReferenceException
                    // 这两类是编码/初始化顺序问题，最有价值的是文件+行号
                    ? $"{ex.Message}\n\n{ex.StackTrace}"
                    : ex.Message;
            MessageBox.Show(
                $"{where}出错了：\n\n{message}\n\n" +
                $"完整日志：%APPDATA%\\Lumo\\lumo.log",
                "Lumo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // 托盘模式下 MessageBox 也可能失败，忽略
        }

        if (ex is not null) Environment.Exit(1);
    }
}

/// <summary>主窗口。</summary>
internal sealed class MainForm : Form
{
    private readonly AppContext _ctx;
    private HotkeyManager? _hotkeys;
    private NotifyIcon _tray = null!;
    private ToolStripMenuItem _clipToggle = null!;
    private System.Windows.Forms.Timer? _clipTimer;

    // 输入区
    private readonly TextBox _input = new()
    {
        Multiline = true,
        BorderStyle = BorderStyle.None,
        // 原文可能很长，必须能滚动。默认隐藏滚动条时用户会以为内容被截断了。
        ScrollBars = ScrollBars.Vertical,
        WordWrap = true,
    };
    private readonly Label _counter = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly ComboBox _from = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _to = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly Button _swap = new() { Text = "⇄", FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
    private readonly FlatButton _translate = new() { Text = "翻译", IsPrimary = true };
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 300 };

    // 结果区
    private readonly RichTextBox _result = new() { Multiline = true, BorderStyle = BorderStyle.None, ReadOnly = true };
    private readonly Label _resultMeta = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly FlowLayoutPanel _resultActions = new() { Dock = DockStyle.Bottom };

    // 底栏
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    private CancellationTokenSource? _cts;
    private string? _lastClipboardText;
    private long _requestSeq;

    public MainForm(AppContext ctx)
    {
        _ctx = ctx;
        // 刻意不传 Handle：访问它会强制创建句柄并触发 WndProc，
        // 那时 _hotkeys 还没赋值完，直接 NRE。句柄在 OnHandleCreated 里补上。
        _hotkeys = new HotkeyManager();

        Text = "Lumo 翻译";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 520);
        MinimumSize = new Size(400, 300);
        TopMost = ctx.Config.StayOnTop;
        Opacity = ctx.Config.Opacity;

        // 剪贴板监听：轮询内容而不是装全局钩子。空载开销可以忽略。
        //
        // ⚠ 必须在 BuildUi / SetupTray **之前**创建：SetupTray() 会调
        // SyncFromConfig()，而后者要用 _clipTimer 启停监听。顺序反了就是
        // 构造函数里访问未初始化字段 —— 现象是启动即崩溃的 NullReferenceException，
        // 编译和单元测试都发现不了。
        _clipTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Clamp(ctx.Config.ClipboardDebounceMs, 50, 3000),
        };
        _clipTimer.Tick += OnClipboardTick;

        BuildUi();
        ApplyConfigToUi();
        SetupTray();   // 内部会调 SyncFromConfig()，此时上面这些都已就绪

        // 窗口显示出来之后再弹，否则会被自己盖住
        Shown += (_, _) => MaybeShowFirstRunHelp();

        // 刻意不在启动时自动隐藏：用户双击就是想看它，进来什么都看不到
        // 会以为没启动。托盘是「之后想收起时」用的，不是启动行为。
    }

    // ── 界面 ──

    private void BuildUi()
    {
        var pad = new Padding(12);

        _input.Font = new Font("Microsoft YaHei UI", 10.5f);
        _result.Font = new Font("Microsoft YaHei UI", 10.5f);
        _result.BackColor = SystemColors.Window;
        _input.BackColor = SystemColors.Window;

        foreach (var c in new[] { _from, _to })
        {
            foreach (var (code, name) in Lang.Common) c.Items.Add(new LangItem(code, name));
            // 按最长语言名实测宽度。之前写死 110px，
            // 「简体中文」在部分字体/DPI 下会被下拉箭头挤掉，显示成「简体」。
            c.Width = MeasureComboWidth(c, Lang.Common.Select(l => l.Name));
            c.SelectedIndexChanged += (_, _) => SaveLang();
        }
        _swap.Width = 32;
        _swap.Height = 26;
        _swap.Click += (_, _) => SwapLanguages();
        _translate.Width = 68;
        _translate.Height = 28;
        _translate.Click += (_, _) => _ = DoTranslateAsync();
        _input.KeyDown += OnInputKeyDown;
        _input.TextChanged += (_, _) => UpdateCounter();

        // 语言栏：左边的语言选择 + 右边的功能按钮，两块各占一列。
        // 之前用 FlowLayoutPanel 加一个写死的 340px 间隔来「顶到右边」——
        // 窗口一变窄或字体变宽，按钮就被挤出可视区了（用户实测踩到）。
        // 改用两列 TableLayoutPanel：左边自动宽度，右边吃掉剩余空间并右对齐。
        var btnClear = MakeButton("清空", () =>
        {
            _input.Clear();
            SetResult("");
            SetMeta("");
            UpdateCounter();
            _input.Focus();
        });

        var btnOcr = MakeButton("截图", async () => await ScreenshotTranslateAsync());
        var btnHistory = MakeButton("历史", () => ShowDialogOnTop(new HistoryForm(_ctx, entry =>
        {
            SetInput(entry.SourceText);
            _from.SelectedItem = FindLang(_from, entry.From);
            _to.SelectedItem = FindLang(_to, entry.To);
            SetResult(entry.TargetText);
            SetMeta($"{Lang.DisplayName(entry.From)} → {Lang.DisplayName(entry.To)} · {entry.Engine}");
            ShowResultActions();
        })));
        var btnSettings = MakeButton("设置", () => ShowDialogOnTop(new SettingsForm(_ctx, SyncFromConfig)));

        // 右侧按钮组。FlowDirection.RightToLeft 表示「先加的排最右」，
        // 所以添加顺序要反过来写：截图在最右，翻译在最左。
        //
        // 这里刻意只用一层 FlowLayoutPanel。之前试过「主操作面板里再套一个
        // 次要操作面板」，结果整个顶栏塌了 —— FlowLayoutPanel 里嵌 Dock=Fill
        // 的子面板不会参与尺寸计算，再放进 AutoSize 列更是自相矛盾。
        var rightButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 4, 6, 0),
        };
        rightButtons.Controls.AddRange(new Control[]
        {
            btnOcr, btnHistory, btnSettings, btnClear, _translate,
        });

        // 必须用 AutoSize 让它报告内容的自然宽度。
        // 之前写的是 Dock = Fill —— Fill 的语义是「填满分配到的空间」，
        // 面板本身不报告两个下拉框需要多宽，于是 AutoSize 列分给它的宽度
        // 不够，第二个下拉框就被裁成只剩一个「简」字。
        var leftSelects = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(10, 4, 0, 0),
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
        };
        leftSelects.Controls.Add(_from);
        leftSelects.Controls.Add(_swap);
        leftSelects.Controls.Add(_to);

        // 两列：语言选择自动宽 | 按钮组吃掉剩余空间并右对齐。
        // 中间不需要显式的弹性列 —— 按钮组本身就是 Percent 100。
        var langBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
        };
        langBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        langBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        langBar.Controls.Add(leftSelects, 0, 0);
        langBar.Controls.Add(rightButtons, 1, 0);

        // 输入区
        var inputPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 4) };
        _input.Dock = DockStyle.Fill;
        _counter.Location = new Point(12, 2);
        _counter.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        inputPanel.Controls.Add(_input);
        inputPanel.Controls.Add(_counter);

        // 结果区。
        //
        // 停靠顺序规则（WinForms）：控件按 **加入顺序的逆序** 停靠，
        // 也就是「后加的先停靠、占最外层」。所以会 Fill 的控件必须 **最先加**
        // （索引 0，最后才停靠，处于最内层），Top/Bottom 的再加在外面。
        //
        // 写反的后果：_result 占满整个面板，标题栏和操作栏直接叠在它上面，
        // 译文的第一行正好被标题栏遮住 —— 表现为「翻译结果开头丢了」。
        var resultPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };

        // 1) Fill，最先加
        _result.Dock = DockStyle.Fill;
        resultPanel.Controls.Add(_result);

        // 2) 标题栏 Top
        var resultHead = new Panel { Dock = DockStyle.Top, Height = 26, Padding = new Padding(0, 2, 0, 2) };
        _resultMeta.Location = new Point(0, 2);
        resultHead.Controls.Add(_resultMeta);
        resultPanel.Controls.Add(resultHead);

        // 3) 操作栏 Bottom，最后加 → 最外层
        _resultActions.Height = 34;
        resultPanel.Controls.Add(_resultActions);

        _translate.Width = 68;
        _translate.Height = 30;
        _translate.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        _translate.Margin = new Padding(0, 0, 6, 0);   // 主操作与次要操作之间留一点分隔

        // 底栏
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(12, 4, 12, 4) };
        statusBar.Controls.Add(_status);

        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        split.Controls.Add(inputPanel, 0, 0);
        split.Controls.Add(resultPanel, 0, 1);

        // RowCount、RowStyles 的个数、Controls 的行号必须严格对应。
        // 之前删掉了操作栏那一行却忘了同步 RowStyles，结果 split（输入+结果区）
        // 落在了 Absolute 36px 的行里，整个内容区被压成一条 —— 界面上只剩顶栏。
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));   // 顶栏
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 输入 + 结果
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // 状态栏
        root.Controls.Add(langBar, 0, 0);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(statusBar, 0, 2);

        Controls.Add(root);
    }

    /// <summary>按最宽的选项算出下拉框需要的宽度。</summary>
    /// <remarks>
    /// 写死宽度在两种情况下会翻车：字体变大（用户改了系统字号）、
    /// 以及 DPI 缩放（PerMonitorV2 下 150% 会等比放大）。
    /// 实测宽度而不是估算，才能保证「繁体中文」「自动检测」这类长名字不被截断。
    /// </remarks>
    private static int MeasureComboWidth(ComboBox box, IEnumerable<string> options)
    {
        // ComboBox 的非客户区（下拉箭头、边框、左右内边距）不在 MeasureText 里，
        // 给足余量，否则差几个像素还是会截断。
        const int arrowAndPadding = 36;
        var widest = options
            .Select(o => TextRenderer.MeasureText(o, box.Font).Width)
            .DefaultIfEmpty(80)
            .Max();
        // 下限 112：96dpi 下实测约 84px，留 28px 余量应对字号/缩放变化。
        // 曾经取 130，顶栏右侧就放不下按钮组了 —— 宽度要算全局，不能只看单个控件。
        return Math.Clamp(widest + arrowAndPadding, 112, 200);
    }

    /// <summary>统一的次要操作按钮。自绘四边框，宽度按文字自适应。</summary>
    private static FlatButton MakeButton(string text, Action onClick)
    {
        // 收窄策略：左右留白 22 -> 14，按钮间距 6 -> 4。
        // 五个按钮挤在顶栏右半边，留白比文字本身还占地方。
        var b = new FlatButton { Text = text, AutoSize = true, Padding = new Padding(4, 0, 4, 0) };
        b.Width = TextRenderer.MeasureText(text, b.Font).Width + 14;
        b.Margin = new Padding(0, 0, 4, 0);
        b.Click += (_, _) => onClick();
        return b;
    }

    private sealed record LangItem(string Code, string Name)
    {
        public override string ToString() => Name;
    }

    private static LangItem? FindLang(ComboBox box, string code) =>
        box.Items.Cast<LangItem>().FirstOrDefault(i => i.Code == code);

    // ── 配置同步 ──

    private void ApplyConfigToUi()
    {
        var c = _ctx.Config;
        _from.SelectedItem = FindLang(_from, c.SourceLang) ?? _from.Items[0];
        _to.SelectedItem = FindLang(_to, c.TargetLang) ?? _to.Items[1];
        UpdateSwapState();
        TopMost = c.StayOnTop;
        Opacity = c.Opacity;
        SetStatus($"选中任意文字后按 {c.HotkeyTrigger} 翻译 · 框选画面按 {c.HotkeyOcr} 截图翻译 · 窗口内 Ctrl+Enter");
    }

    private void SaveLang()
    {
        if (_from.SelectedItem is not LangItem f || _to.SelectedItem is not LangItem t) return;
        var cfg = _ctx.Config;
        cfg.SourceLang = f.Code;
        cfg.TargetLang = t.Code;
        UpdateSwapState();
        TrySave(out _);
    }

    private bool TrySave([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        try
        {
            _ctx.Save(_ctx.Config);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            SetStatus($"配置保存失败：{ex.Message}");
            Program.log_line($"保存配置失败: {ex}");
            return false;
        }
    }

    // ── 交互 ──

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; _ = DoTranslateAsync(); }
    }

    private void UpdateCounter()
    {
        var n = _input.TextLength;
        _counter.Text = $"{n} 字符";
        _counter.ForeColor = n > 20000 ? Color.Firebrick : SystemColors.GrayText;
        _translate.Enabled = n > 0 && n <= 20000 && !_translate.Text.Contains("…");
    }

    /// <summary>源语言是「自动检测」时没有可交换的方向，按钮置灰。</summary>
    /// <remarks>
    /// 之前按钮始终可点，点了只在状态栏给一句提示 —— 看着能点却没反应，
    /// 比直接置灰更让人困惑。
    /// </remarks>
    private void UpdateSwapState()
    {
        var code = (_from.SelectedItem as LangItem)?.Code;
        var canSwap = code is not (null or "auto");
        _swap.Enabled = canSwap;
        _swap.ForeColor = canSwap ? Theme.Text : Theme.TextDim;
        // 提示走 ToolTip 控件 —— Button 本身没有 ToolTipText 属性。
        _toolTip.SetToolTip(_swap, canSwap ? "交换语言方向" : "源语言为「自动检测」时无法交换，请先指定具体语言");
    }

    private void SwapLanguages()
    {
        if (_from.SelectedItem is not LangItem f) return;
        if (f.Code == "auto")
        {
            SetStatus("源语言是「自动检测」，没有可交换的方向");
            return;
        }
        var oldTo = (_to.SelectedItem as LangItem)?.Code ?? "zh";
        _from.SelectedItem = FindLang(_from, oldTo);
        _to.SelectedItem = FindLang(_to, f.Code);
        UpdateSwapState();
    }

    private void SetStatus(string text) => _status.Text = text;

    private void SetMeta(string text) => _resultMeta.Text = text;

    /// <summary>程序填充输入框，并滚回顶部让人看到开头。</summary>
    /// <remarks>
    /// 和结果区同一个问题：赋值后光标停在末尾，视图被滚到末尾。
    /// 但输入框这里更要注意 —— 长文本的**开头**才是信息量最大的部分
    /// （划词翻译往往前两句就说明了这段话在讲什么）。
    /// 光标位置不影响翻译，Ctrl+Enter 读的是整框内容。
    /// </remarks>
    private void SetInput(string text)
    {
        _input.Text = text;
        _input.SelectionStart = 0;
        _input.SelectionLength = 0;
        _input.ScrollToCaret();
        if (_input.IsHandleCreated)
        {
            Native.SendMessage(_input, 0x00B7 /* EM_SCROLLCARET */, IntPtr.Zero, IntPtr.Zero);
        }
        UpdateCounter();
    }

    /// <summary>显示译文并把结果区滚回顶部。</summary>
    /// <remarks>
    /// 直接给 <c>RichTextBox.Text</c> 赋值后，光标会停在文本末尾，
    /// WinForms 随即把视图滚到光标处 —— 于是长译文一显示出来就只看得见后半段，
    /// 开头那半句被顶到上面去了，看起来就像「翻译不全」。
    /// 这里显式把选区归零再滚回顶部。
    /// </remarks>
    private void SetResult(string text)
    {
        _result.Text = text;
        ScrollResultToTop();
    }

    /// <summary>把结果区滚回顶部。</summary>
    /// <remarks>
    /// <c>ScrollToCaret</c> 在控件尚未完成布局时可能静默失效，
    /// 所以既用选区归零 + ScrollToCaret，也发一条 EM_SCROLLCARET 消息兜底。
    /// </remarks>
    private void ScrollResultToTop()
    {
        _result.SelectionStart = 0;
        _result.SelectionLength = 0;
        _result.ScrollToCaret();
        if (_result.IsHandleCreated)
        {
            Native.SendMessage(_result, 0x00B7 /* EM_SCROLLCARET */, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void ShowResultActions()
    {
        _resultActions.SuspendLayout();
        _resultActions.Controls.Clear();
        _resultActions.Controls.Add(MakeButton("复制", () =>
        {
            if (string.IsNullOrEmpty(_result.Text)) return;
            Native.WriteClipboardText(_result.Text);
            SetStatus("已复制到剪贴板");
        }));
        _resultActions.Controls.Add(MakeButton("输入", () =>
        {
            if (string.IsNullOrEmpty(_result.Text)) return;
            try
            {
                Native.SendText(_result.Text, replaceAll: false);
                SetStatus("已输入到原窗口");
            }
            catch (Exception ex) { SetStatus($"输入失败：{ex.Message}"); }
        }));
        _resultActions.Controls.Add(MakeButton("当作原文", () =>
        {
            if (string.IsNullOrEmpty(_result.Text)) return;
            (_input.Text, _result.Text) = (_result.Text, _input.Text);
            UpdateCounter();
        }));
        _resultActions.Controls.Add(MakeButton("重译", () => _ = DoTranslateAsync()));
        _resultActions.ResumeLayout(true);
    }

    private async Task DoTranslateAsync()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) return;

        // 用户连按 Ctrl+Enter 时只认最后一次
        var seq = Interlocked.Increment(ref _requestSeq);
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _translate.Enabled = false;
        _translate.BackColor = Theme.AccentMuted;
        _translate.Text = "翻译中…";
        SetStatus("请求中…");
        _resultActions.SuspendLayout();
        _resultActions.Controls.Clear();
        _resultActions.ResumeLayout(true);

        try
        {
            var from = (_from.SelectedItem as LangItem)?.Code ?? "auto";
            var to = (_to.SelectedItem as LangItem)?.Code ?? "zh";
            var outcome = await _ctx.Translator.TranslateAsync(text, from, to, ct);
            if (seq != _requestSeq) return;

            if (!string.IsNullOrEmpty(outcome.TargetText))
            {
                SetResult(outcome.TargetText);
                var badges = new List<string>
                {
                    $"{Lang.DisplayName(outcome.Source)} → {Lang.DisplayName(outcome.Target)}",
                    EngineName(outcome.Engine),
                };
                if (outcome.Cached) badges.Add("缓存");
                if (outcome.ElapsedMs > 0) badges.Add($"{outcome.ElapsedMs}ms");
                // 译文字数：既是诊断手段（判断"显示不全"到底是滚动还是真截断），
                // 也方便用户核对长文有没有丢内容。
                badges.Add($"译文 {outcome.TargetText!.Length} 字符");
                if (outcome.Note is not null) badges.Add(outcome.Note);
                SetMeta(string.Join("  ·  ", badges));
                ShowResultActions();
                SetStatus("就绪");
            }
            else
            {
                SetResult("");
                SetStatus(outcome.Note ?? "无需翻译");
            }

            if (_ctx.Config.AutoCopy && !string.IsNullOrEmpty(outcome.TargetText))
            {
                Native.WriteClipboardText(outcome.TargetText);
                SetStatus("已翻译并复制");
            }
        }
        catch (OperationCanceledException)
        {
            // 用户又发起了新请求，静默丢弃
        }
        catch (TranslateException ex)
        {
            if (seq != _requestSeq) return;
            SetStatus("翻译失败");
            ShowError(ex.Message, ex.IsAuthError);
        }
        catch (Exception ex)
        {
            SetStatus($"翻译失败：{ex.Message}");
        }
        finally
        {
            if (seq == _requestSeq)
            {
                _translate.Text = "翻译";
                UpdateCounter();
            }
        }
    }

    /// <summary>
    /// 把错误显示在结果区，而不是弹模态框。
    /// </summary>
    /// <remarks>
    /// 模态 MessageBox 有三个实际问题：挡住界面、用户没法复制文字去搜索、
    /// 连续翻译时会堆一排框。这里改成内联显示，「重试」按钮也留在旁边。
    /// </remarks>
    private void ShowError(string message, bool warn = false)
    {
        _resultActions.SuspendLayout();
        _resultActions.Controls.Clear();
        _resultActions.Controls.Add(MakeButton("重试", () => _ = DoTranslateAsync()));
        if (warn || message.Contains("API Key") || message.Contains("引擎"))
        {
            _resultActions.Controls.Add(MakeButton("去设置", () => ShowDialogOnTop(new SettingsForm(_ctx, SyncFromConfig))));
        }
        _resultActions.ResumeLayout(true);

        var box = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = warn ? Color.FromArgb(252, 245, 230) : Color.FromArgb(253, 236, 236),
            Padding = new Padding(10),
        };
        var label = new Label
        {
            Dock = DockStyle.Fill,
            Text = message,
            ForeColor = warn ? Color.FromArgb(140, 90, 10) : Color.Firebrick,
            AutoEllipsis = false,
        };
        box.Controls.Add(label);
        SetResult("");
        _result.Controls.Add(box);
    }

    private static string EngineName(string id) => id switch
    {
        EngineIds.Google => "Google",
        EngineIds.Bing => "必应",
        EngineIds.DeepL => "DeepL",
        EngineIds.OpenAi => "AI",
        EngineIds.Custom => "自定义",
        _ => id,
    };

    // ── 截图翻译 ──

    private async Task ScreenshotTranslateAsync()
    {
        using var picker = new RegionPicker();
        var region = picker.Pick(this);
        if (region is null) return;

        var (x, y, w, h) = region.Value;
        if (w < 24 || h < 12) { SetStatus("选区太小"); return; }
        if ((long)w * h > 12_000_000) { SetStatus("选区太大，请缩小范围"); return; }

        SetStatus("正在识别…");
        _translate.Text = "识别中…";
        try
        {
            var bgra = await Task.Run(() => Native.CaptureRegion(x, y, w, h));
            var bmp = Bmp.EncodeBgra(bgra, w, h);
            var target = (_to.SelectedItem as LangItem)?.Code ?? "zh";
            var ocr = await Task.Run(() => Ocr.Recognize(bmp, target));

            SetInput(ocr.Text);
            // 截图期间遮罩是置顶的，它一关，主窗口很可能还压在别的程序底下。
            // 不主动唤起的话，用户会盯着原屏幕等一个「看起来没发生」的翻译。
            // OCR 本身要几百毫秒，这段时间焦点早就不在主窗口了。
            ShowAndFocus();
            SetStatus("识别完成，正在翻译…");
            await DoTranslateAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"截图翻译失败：{ex.Message}");
            ShowError("截图翻译失败：" + ex.Message, warn: true);
        }
        finally
        {
            _translate.Text = "翻译";
            UpdateCounter();
        }
    }

    // ── 剪贴板 ──

    private void OnClipboardTick(object? sender, EventArgs e)
    {
        if (!_ctx.Config.ListenClipboard) return;
        var text = Native.ReadClipboardText();
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return;
        if (text == _lastClipboardText) return;
        _lastClipboardText = text;

        // 用户正在输入框里打字 —— 绝不能覆盖
        if (_input.Focused && _input.TextLength > 0) return;

        // 窗口已经开着并且用户正在用：只把内容放进去，不抢焦点、不自动翻译。
        // 抢焦点会把用户从别处拽过来，覆盖输入框会把正在写的东西抹掉 ——
        // 这两点让应用变成「只能靠剪贴板」，主操作按钮反而没人用。
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            if (_input.TextLength == 0)
            {
                SetInput(text);
                UpdateCounter();
                SetStatus("已从剪贴板填入，点「翻译」开始翻译");
            }
            return;
        }

        // 窗口隐藏着 —— 这才是「复制即翻译」该发挥的场景：
        // 唤起窗口、填入、立刻翻译，全程用户无需操作。
        ShowAndFocus();
        SetInput(text);
        UpdateCounter();
        _ = DoTranslateAsync();
    }

    // ── 窗口 / 托盘 ──

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowAndFocus());
        menu.Items.Add("截图翻译", null, async (_, _) => await ScreenshotTranslateAsync());
        menu.Items.Add(new ToolStripSeparator());

        // 剪贴板监听开关。放在托盘里比藏进设置页合理 ——
        // 这是个需要随手切换的行为（复制密码时想临时关掉之类），
        // 托盘菜单右键一下就够，不必先打开设置窗口。
        _clipToggle = new ToolStripMenuItem("监听剪贴板")
        {
            Checked = _ctx.Config.ListenClipboard,
            CheckOnClick = false,   // 手动处理，好把改动落到配置里
        };
        _clipToggle.Click += (_, _) => ToggleClipboardWatch(!_ctx.Config.ListenClipboard);
        menu.Items.Add(_clipToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) => ShowDialogOnTop(new SettingsForm(_ctx, SyncFromConfig)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            TrySave(out _);
            Application.Exit();
        });

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            // NotifyIcon.Text 上限 63 字符（UTF-16 计），超了会抛 ArgumentException，
            // 那会让整个托盘图标都建不出来 —— 比显示不全严重得多。
            Text = "Lumo 翻译",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowAndFocus();

        // 托盘建好后统一同步一次，保证开关勾选和定时器状态跟配置一致
        SyncFromConfig();
    }

    /// <summary>首次运行时讲清楚这几个功能怎么用。</summary>
    /// <remarks>
    /// 「选中文字后按快捷键」这类交互，光看菜单名是猜不出来的 ——
    /// 用户不知道要先选中、也不知道按哪个键。首次启动弹一次窗，
    /// 比在 README 里写十遍都管用。
    /// <para>
    /// 是否「别再弹」由用户勾选决定，而不是程序弹完就偷偷改状态。
    /// 后者一旦保存失败就会变成每次都弹，而且完全无从排查。
    /// </para>
    /// </remarks>
    private void MaybeShowFirstRunHelp()
    {
        if (!_ctx.Config.ShowFirstRunHelp) return;

        using var dialog = new HelpDialog(_ctx.Config);
        ShowDialogOnTop(dialog);

        // 勾了才记住「别再弹」；没勾就下次照弹，语义完全交给用户。
        if (!dialog.DontShowAgain) return;

        _ctx.Config.ShowFirstRunHelp = false;
        if (!TrySave(out var error))
        {
            // 存不住就直接告诉用户，否则下次照弹他只会觉得「这软件有毛病」。
            MessageBox.Show(this,
                $"没能记住这个选择：{error}\n\n下次启动还会再提示一次。",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 模态显示对话框。
    /// </summary>
    /// <remarks>
    /// 主窗口默认是 TopMost，而普通对话框不是 —— 结果对话框会被主窗口压住，
    /// 看起来像「弹窗跑到后面去了」，实际点不到。
    /// 这里在弹窗期间临时关掉主窗口的置顶，并让对话框自己置顶，关闭后还原。
    /// </remarks>
    private void ShowDialogOnTop(Form dialog)
    {
        var wasTopMost = TopMost;
        TopMost = false;
        dialog.TopMost = true;
        dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
        dialog.MinimizeBox = false;
        dialog.MaximizeBox = false;
        dialog.ShowInTaskbar = false;
        dialog.StartPosition = FormStartPosition.CenterParent;
        try
        {
            dialog.ShowDialog(this);
        }
        finally
        {
            dialog.TopMost = false;
            TopMost = wasTopMost;
        }
    }

    public void ShowAndFocus()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    /// <summary>配置变化后，把界面状态同步过来。</summary>
    /// <remarks>
    /// 拖动类设置在设置窗口里直接改了，这里负责让运行时状态跟上：
    /// 托盘开关的对勾、剪贴板定时器的启停、置顶、透明度。
    /// </remarks>
    private void SyncFromConfig()
    {
        var c = _ctx.Config;

        // 全程判空：这个方法在构造函数早期就会被调到，字段就绪顺序一旦被
        // 调整，这里不能跟着一起崩 —— 宁可少同步一项，也别启动失败。
        if (_clipTimer is not null)
        {
            _clipTimer.Interval = Math.Clamp(c.ClipboardDebounceMs, 50, 3000);
            if (c.ListenClipboard)
            {
                if (!_clipTimer.Enabled) _clipTimer.Start();
                _lastClipboardText = Native.ReadClipboardText();
            }
            else if (_clipTimer.Enabled)
            {
                _clipTimer.Stop();
            }
        }

        if (_clipToggle is not null) _clipToggle.Checked = c.ListenClipboard;
        if (_swap is not null) UpdateSwapState();

        TopMost = c.StayOnTop;
        Opacity = c.Opacity;
    }

    /// <summary>开关剪贴板监听，并立刻生效。</summary>
    /// <remarks>
    /// 改配置只是持久化，真正要紧的是启停定时器 —— 关闭时必须
    /// 真正 <c>Stop()</c>，否则一个每 350ms 醒一次的定时器还在空转，
    /// 开着监听的 CPU 占用和关着是一样的，这个开关就形同虚设。
    /// </remarks>
    private void ToggleClipboardWatch(bool enabled)
    {
        if (enabled) _clipTimer?.Start();
        else _clipTimer?.Stop();

        _ctx.Config.ListenClipboard = enabled;
        if (_clipToggle is not null) _clipToggle.Checked = enabled;
        TrySave(out _);

        // 状态栏也要同步，否则主窗口里看不出当前是什么状态。
        SetStatus(enabled
            ? "剪贴板监听已开启，复制文字会自动翻译"
            : "剪贴板监听已关闭");
        if (enabled) _lastClipboardText = Native.ReadClipboardText();
    }

    /// <summary>取应用自己的图标作为托盘图标。</summary>
    /// <remarks>
    /// 之前用的是 <c>SystemIcons.Application</c> —— 那是 Windows 的通用占位图标，
    // 托盘里会显示成一个跟任何程序都没区别的默认图标，用户根本认不出来。
    /// ExtractAssociatedIcon 从当前 exe 里读图标资源，拿不到时再退回系统图标，
    /// 绝不能让托盘图标为空（空图标在某些 shell 上会直接不显示）。
    /// </remarks>
    private static Icon LoadAppIcon()
    {
        try
        {
            var path = Application.ExecutablePath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch (Exception ex)
        {
            Program.log_line($"读取程序图标失败: {ex.Message}");
        }
        return SystemIcons.Application;
    }

    private void GrabSelection()
    {
        var text = Selection.Grab();
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("没有选中文字");
            return;
        }
        ShowAndFocus();
        SetInput(text);
        UpdateCounter();
        _ = DoTranslateAsync();
    }

    // ── 全局快捷键 ──

    /// <summary>句柄就绪后把快捷键真正注册上去。</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotkeys?.Attach(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        // 句柄创建阶段就会走到这里，比字段赋值更早，所以必须容错。
        _hotkeys?.OnMessage(ref m);
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var c = _ctx.Config;
        // 句柄此刻已由 OnHandleCreated 挂好，这里只是登记动作。
        if (_hotkeys is not { } hk) return;
        if (!hk.Register(c.HotkeyTrigger, GrabSelection))
            SetStatus($"快捷键 {c.HotkeyTrigger} 注册失败，可能被其他软件占用");
        hk.Register(c.HotkeyOcr, async () => await ScreenshotTranslateAsync());
        hk.Register(c.HotkeyMini, () =>
        {
            if (Visible && WindowState != FormWindowState.Minimized) Hide();
            else ShowAndFocus();
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && _ctx.Config.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _clipTimer?.Stop();
        _hotkeys?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        TrySave(out _);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hotkeys?.Dispose();
        base.Dispose(disposing);
    }
}
