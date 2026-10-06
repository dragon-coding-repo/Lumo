namespace Lumo.App;

/// <summary>
/// 扁平按钮，绘制<b>完整四边框</b>。
/// </summary>
/// <remarks>
/// WinForms 的 <see cref="FlatStyle.Flat"/> 只画上/左/右三条边，不画下边 ——
/// 它假设按钮会贴在容器边缘，下边框由容器提供。
/// 但我们的按钮排在工具栏中间，下边悬空，看起来就是「框线断了」。
///
/// 这里保留 Flat 风格（不用 FlatStyle.System 的立体感），改为自绘四边框，
/// 顺带统一 hover / 按下 / 禁用的配色。
/// </remarks>
internal sealed class FlatButton : Button
{
    public FlatButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;   // 关掉自绘的三边框，改为完全自绘
        UseVisualStyleBackColor = false;
        BackColor = Color.FromArgb(250, 250, 251);
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9f);
        Cursor = Cursors.Hand;
        Height = 30;
    }

    /// <summary>设成主操作：实心主色、白字。</summary>
    /// <remarks>
    /// 项目里启了 UseWPF（为了拿 UIAutomation 程序集），WPF 的序列化器
    /// 会对所有公开属性报 WFO1000。这个属性只跟自绘有关，与序列化无关。
    /// </remarks>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsPrimary { get; set; }

    private Color BorderColor =>
        IsPrimary ? Theme.Accent : (Enabled ? Theme.Border : Theme.BorderMuted);

    private Color FillColor
    {
        get
        {
            if (!Enabled) return IsPrimary ? Theme.AccentMuted : Theme.SurfaceMuted;
            if (IsPrimary) return MouseIsDown ? Theme.Accent : (IsMouseOver ? Theme.AccentHover : Theme.Accent);
            return IsMouseOver ? Theme.SurfaceHover : (MouseIsDown ? Theme.SurfaceDown : Color.FromArgb(250, 250, 251));
        }
    }

    private bool MouseIsDown => Enabled && !Focused && MouseButtons == MouseButtons.Left;

    private bool IsMouseOver => Enabled && ClientRectangle.Contains(PointToClient(Cursor.Position));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? SystemColors.Control);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var brush = new SolidBrush(FillColor))
            g.FillRectangle(brush, r);

        // 完整矩形：四条边都画
        using (var pen = new Pen(BorderColor))
            g.DrawRectangle(pen, r);

        // 文字用基类绘制（居中）
        TextRenderer.DrawText(
            g, Text, Font, r, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
}

/// <summary>界面配色。集中一处，避免同样的颜色散落在各处写死。</summary>
internal static class Theme
{
    public static readonly Color Accent = Color.FromArgb(59, 110, 245);
    public static readonly Color AccentHover = Color.FromArgb(47, 93, 224);
    /// <summary>禁用/执行中的主色，保留「这是个主操作」的观感但明显变淡。</summary>
    public static readonly Color AccentMuted = Color.FromArgb(140, 168, 230);

    public static readonly Color Surface = Color.FromArgb(250, 250, 251);
    public static readonly Color SurfaceHover = Color.FromArgb(238, 240, 245);
    public static readonly Color SurfaceDown = Color.FromArgb(226, 229, 236);
    public static readonly Color SurfaceMuted = Color.FromArgb(242, 243, 246);

    public static readonly Color Text = Color.FromArgb(26, 29, 33);
    public static readonly Color TextDim = Color.FromArgb(107, 114, 128);
    public static readonly Color Border = Color.FromArgb(206, 210, 217);
    public static readonly Color BorderMuted = Color.FromArgb(228, 230, 234);
    public static readonly Color Danger = Color.FromArgb(217, 58, 58);
}
