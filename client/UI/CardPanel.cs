using System.Drawing;
using System.Drawing.Drawing2D;

namespace AgVpsUnlock.UI;

public sealed class CardPanel : Panel
{
    private const int Radius = 8;

    public CardPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = AppTheme.CardBack;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var path = RoundedRect(ClientRectangle, Radius);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? AppTheme.WindowBack);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        using var pen = new Pen(AppTheme.BorderColor, 1f);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
