namespace Lumo.App;

/// <summary>
/// 框选屏幕区域。
/// </summary>
/// <remarks>
/// 之前这层遮罩 <c>Opacity = 0.25</c> 配深灰底色，实际几乎完全透明，
/// 用户根本看不到「有个框选界面弹出来了」—— 表现就是「截图界面框没出来」。
/// 另外当时用 <c>Application.DoEvents()</c> 轮询等结果，既烧 CPU 又不靠谱，
/// 窗口重绘经常赶不上。这里改成正经的模态对话框。
/// </remarks>
/// <summary>
/// 暴露了 WinForms 默认关闭的双缓冲的窗体。
/// </summary>
/// <remarks>
/// <see cref="Control.DoubleBuffered"/> 和 <see cref="Control.SetStyle"/> 都是
/// protected，外部拿不到，只能靠继承暴露。选区框逐帧重画时没有它就会拖出残影。
/// </remarks>
internal sealed class BufferedOverlayForm : Form
{
    public BufferedOverlayForm()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw, true);
    }
}

/// <summary>同上，带双缓冲的 Panel。</summary>
internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
}

internal sealed class RegionPicker : IDisposable
{
    private readonly Form _overlay;
    private readonly Panel _box;
    private readonly Label _hint;
    private readonly Label _size;
    private Point? _origin;
    private (int X, int Y, int W, int H)? _result;
    private bool _done;

    public RegionPicker()
    {
        var screen = SystemInformation.VirtualScreen;

        // 遮罩层：半透明深色，把屏幕压暗，让选区边界看得清。
        // Opacity 不能太低 —— 0.25 配深灰等于全透明，用户会以为功能没触发。
        // 用 BufferedOverlayForm：OnMouseMove 每帧重画选区框，没有双缓冲的话
        // Windows 不会自动擦掉上一帧，拖动时会拖出一条残影。
        _overlay = new BufferedOverlayForm
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            // 多显示器时 VirtualScreen 的 X/Y 可能是负数，必须照搬
            Bounds = screen,
            TopMost = true,
            ShowInTaskbar = false,
            Cursor = Cursors.Cross,
            BackColor = Color.FromArgb(20, 22, 26),
            Opacity = 0.45,
            KeyPreview = true,
        };

        _box = new BufferedPanel
        {
            BackColor = Color.FromArgb(59, 110, 245, 40),
            // 三边框在截图场景下会让人误以为选区没闭合，用两层绘制出实心描边
            BorderStyle = BorderStyle.None,
            Visible = false,
        };
        _box.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(80, 160, 255), 2);
            e.Graphics.DrawRectangle(pen, 0, 0, _box.Width - 1, _box.Height - 1);
        };

        _hint = new Label
        {
            Text = "拖动鼠标框选要翻译的区域　·　Esc 取消",
            AutoSize = true,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(220, 32, 36, 42),
            Font = new Font("Microsoft YaHei UI", 10f),
            Padding = new Padding(14, 8, 14, 8),
        };
        _hint.Location = new Point(
            screen.X + (screen.Width - _hint.Width) / 2,
            screen.Y + 60);

        // 实时显示选区大小，让用户知道框到多少了
        _size = new Label
        {
            AutoSize = true,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(220, 32, 36, 42),
            Font = new Font("Microsoft YaHei UI", 9f),
            Padding = new Padding(8, 3, 8, 3),
            Visible = false,
        };

        _overlay.Controls.AddRange(new Control[] { _box, _hint, _size });

        _overlay.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Finish(null); };
        _overlay.MouseDown += OnMouseDown;
        _overlay.MouseMove += OnMouseMove;
        _overlay.MouseUp += OnMouseUp;
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        _origin = e.Location;
        _box.Location = e.Location;
        _box.Size = Size.Empty;
        _box.Visible = true;
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_origin is not { } o) return;
        var x = Math.Min(o.X, e.X);
        var y = Math.Min(o.Y, e.Y);
        var w = Math.Abs(e.X - o.X);
        var h = Math.Abs(e.Y - o.Y);
        _box.Location = new Point(x, y);
        _box.Size = new Size(w, h);

        // 整层重画：只 Invalidate(_box) 的话，移动过程中新位置和旧位置之间
        // 的区域不会被清除，仍然会留残影。
        _overlay.Invalidate();

        _size.Visible = w > 8 && h > 8;
        if (_size.Visible)
        {
            _size.Text = $"{w} × {h}";
            _size.Location = new Point(x, Math.Max(0, y - _size.Height - 6));
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (_origin is not { } o) return;
        var x = Math.Min(o.X, e.X);
        var y = Math.Min(o.Y, e.Y);
        var w = Math.Abs(e.X - o.X);
        var h = Math.Abs(e.Y - o.Y);
        // 单击（没有拖动）视为取消，不给一个 0×0 的选区。
        if (w < 8 || h < 8) { Finish(null); return; }

        // e.Location 是相对遮罩窗口的，加上窗口原点才是真正的屏幕坐标。
        // 多显示器下 VirtualScreen 的原点可能是负数，这一步不能省。
        Finish((_overlay.Bounds.X + x, _overlay.Bounds.Y + y, w, h));
    }

    /// <summary>显示遮罩并等待选区完成。用户取消时返回 <c>null</c>。</summary>
    public (int X, int Y, int W, int H)? Pick(IWin32Window? owner = null)
    {
        _done = false;
        _result = null;
        _overlay.ShowDialog(owner as IWin32Window);
        return _result;
    }

    private void Finish((int X, int Y, int W, int H)? result)
    {
        if (_done) return;
        _done = true;
        _result = result;
        // 必须 Close：ShowDialog() 会一直阻塞到窗体被关闭为止。
        // 只设标志不关窗的话，框会永远停在那儿等一个永远不会来的结束。
        // （在本窗体自己的事件处理器里调 Close 是安全的。）
        _overlay.Close();
    }

    public void Dispose()
    {
        _overlay.Dispose();
    }
}
