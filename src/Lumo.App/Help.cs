using Lumo.Core;

namespace Lumo.App;

/// <summary>使用说明。首次启动弹一次，设置里随时可以再打开。</summary>
internal static class HelpContent
{
    public const string Title = "Lumo 使用说明";

    /// <summary>说明正文。带快捷键占位符，由 <see cref="Format"/> 填入实际键位。</summary>
    private const string Template = """
        Lumo 是个翻译工具，界面上的按钮不多，常用操作就三个。

        【%(trigger)s】翻译选中文字
          ① 在任意程序里，用鼠标划选一段文字
          ② 按 %(trigger)s
          划选的内容会自动填进主窗口并翻译，译文可以一键「输入」回原处。

        【%(ocr)s】截图翻译
          按 %(ocr)s 后拖动鼠标框选屏幕上的一块区域，
          识别其中的文字并翻译。适合翻译视频、幻灯片、游戏画面。

        监听剪贴板（托盘菜单里开关）
          开启后，复制任何文字都会自动翻译。
          复制密码之类不想被打扰时，在托盘右键关掉即可。
          窗口已经开着时只会把内容填进去，不会抢焦点、不会自动翻译。

        其他
          · 窗口内按 Ctrl+Enter 翻译输入框里的内容
          · %(mini)s 显示/隐藏主窗口
          · 关闭窗口会缩到托盘，右键托盘图标可以退出
          · 以上快捷键都能在「设置 → 快捷键」里改

        关于翻译引擎
          默认用微软 Edge 的翻译接口，免登录、国内可直连。
          设置里可以换成 DeepL、OpenAI 兼容接口（DeepSeek / Kimi / Ollama 等），
          或者用模板接入任意自己的 HTTP 接口。
        """;

    public static string Format(AppConfig cfg) => Template
        .Replace("%(trigger)s", cfg.HotkeyTrigger)
        .Replace("%(ocr)s", cfg.HotkeyOcr)
        .Replace("%(mini)s", cfg.HotkeyMini);

    /// <summary>设置页「使用说明」标签用的多行文本框。</summary>
    public static TextBox CreateReadOnlyText(string text, int width, int height)
    {
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Text = text,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            BackColor = SystemColors.Window,
            Dock = DockStyle.Fill,
            TabStop = true,
        };
        // 等布局完成后再滚动到顶部。设置里没这句的话，文本可能一显示就停在中间。
        box.HandleCreated += (_, _) => { box.SelectionStart = 0; box.ScrollToCaret(); };
        return box;
    }
}

/// <summary>首次启动的说明对话框。带「下回不再提示」勾选框。</summary>
internal sealed class HelpDialog : Form
{
    private readonly CheckBox _dontShowAgain;

    public HelpDialog(AppConfig cfg, bool showCheckbox = true)
    {
        Text = HelpContent.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 420);

        var body = HelpContent.CreateReadOnlyText(HelpContent.Format(cfg), 0, 0);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14, 12, 14, 10),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        var title = new Label
        {
            Text = "欢迎使用 Lumo",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            Dock = DockStyle.Top,
        };

        // 勾选框默认不勾：是否记住「别再弹」由用户决定，
        // 而不是程序弹完就偷偷把状态改成 false。
        _dontShowAgain = new CheckBox
        {
            Text = "下回启动时不再提示（设置 → 使用说明 里随时可以再看）",
            AutoSize = true,
            Checked = false,
            Anchor = AnchorStyles.Left,
        };

        var ok = new Button
        {
            Text = "知道了",
            DialogResult = DialogResult.OK,
            Width = 92,
            Height = 28,
            Anchor = AnchorStyles.Right,
        };
        ok.Click += (_, _) => { Close(); };

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(body, 0, 1);
        layout.Controls.Add(_dontShowAgain, 0, 2);
        // 设置页里复用同一个对话框时不需要勾选框，隐藏但保留在布局中
        _dontShowAgain.Visible = showCheckbox;

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(0, 6, 14, 0),
            FlowDirection = FlowDirection.RightToLeft,
        };
        footer.Controls.Add(ok);

        Controls.Add(layout);
        Controls.Add(footer);
        AcceptButton = ok;
        CancelButton = ok;
    }

    /// <summary>用户是否勾了「下回不再提示」。</summary>
    public bool DontShowAgain => _dontShowAgain.Checked;
}
