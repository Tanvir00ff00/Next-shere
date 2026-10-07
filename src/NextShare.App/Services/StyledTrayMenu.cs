using System.ComponentModel;
using D = System.Drawing;
using F = System.Windows.Forms;
using System.Drawing.Drawing2D;
using System.Windows.Media;

namespace NextShare.App.Services;

/// <summary>Native tray-menu behaviour with Next Share's Material palette.</summary>
internal sealed class StyledTrayMenu : F.ContextMenuStrip
{
    private readonly F.ToolStripLabel header;
    private readonly F.ToolStripSeparator divider = new();
    private readonly D.Bitmap logo;
    private readonly D.Font menuFont = new("Segoe UI", 10.5f);
    internal F.ToolStripMenuItem OpenItem { get; }
    internal F.ToolStripMenuItem ExitItem { get; }
    internal string ReceiverStatus { get; set; } = "Receiving is on";

    public StyledTrayMenu(D.Icon icon, Action open, Action exit)
    {
        logo = icon.ToBitmap();
        Font = menuFont; ShowImageMargin = false; ShowCheckMargin = false;
        AutoSize = true;
        header = new F.ToolStripLabel("Next Share") { Name = "BrandHeader", AutoSize = false, Image = logo };
        OpenItem = new F.ToolStripMenuItem("Open Next Share") { Name = "OpenApp", AutoSize = false };
        ExitItem = new F.ToolStripMenuItem("Exit") { Name = "ExitApp", AutoSize = false };
        OpenItem.Click += (_, _) => open(); ExitItem.Click += (_, _) => exit();
        Items.AddRange([header, divider, OpenItem, ExitItem]);
        foreach (F.ToolStripItem item in Items) item.Margin = F.Padding.Empty;
        ApplyTheme(); ApplyMetrics();
    }

    internal void ApplyTheme()
    {
        if (F.SystemInformation.HighContrast)
        {
            Renderer = new F.ToolStripSystemRenderer(); BackColor = D.SystemColors.Menu; ForeColor = D.SystemColors.MenuText;
        }
        else
        {
            var palette = new MenuPalette(Token("Surface"), Token("SurfaceHigh"), Token("Primary"),
                Token("PrimaryContainer"), Token("Ink"), Token("Muted"), Token("Outline"), Token("ErrorColor"));
            Renderer = new MenuRenderer(this, palette); BackColor = palette.Surface; ForeColor = palette.Ink;
        }
        ApplyRegion(); Invalidate(true);
    }

    private static D.Color Token(string name)
    {
        var color = ((SolidColorBrush)System.Windows.Application.Current.Resources[name]).Color;
        return D.Color.FromArgb(color.A, color.R, color.G, color.B);
    }
    private void ApplyMetrics()
    {
        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);
        Padding = new F.Padding(Scale(7));
        int width = Scale(246);
        header.Size = new D.Size(width, Scale(64));
        OpenItem.Size = ExitItem.Size = new D.Size(width, Scale(44));
        divider.AutoSize = false; divider.Size = new D.Size(width, Scale(12));
        MinimumSize = new D.Size(width + Padding.Horizontal, 0);
        MaximumSize = new D.Size(width + Padding.Horizontal, 0);
        PerformLayout();
        ApplyRegion();
    }
    private void ApplyRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        var old = Region;
        using var outline = MenuRenderer.Round(new D.RectangleF(0, 0, Width, Height), DeviceDpi / 96f * 16);
        Region = F.SystemInformation.HighContrast ? null : new D.Region(outline);
        old?.Dispose();
    }
    protected override void OnOpening(CancelEventArgs e) { ApplyTheme(); ApplyMetrics(); base.OnOpening(e); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); ApplyRegion(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ApplyMetrics(); }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { logo.Dispose(); menuFont.Dispose(); }
    }

    private sealed record MenuPalette(D.Color Surface, D.Color SurfaceHigh, D.Color Primary, D.Color Container,
        D.Color Ink, D.Color Muted, D.Color Outline, D.Color Error);

    private sealed class MenuRenderer(StyledTrayMenu menu, MenuPalette colors) : F.ToolStripRenderer
    {
        internal static GraphicsPath Round(D.RectangleF rect, float radius)
        {
            var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
        private static void Fill(D.Graphics graphics, D.RectangleF bounds, float radius, D.Color color)
        { using var path = Round(bounds, radius); using var brush = new D.SolidBrush(color); graphics.FillPath(brush, path); }
        protected override void OnRenderToolStripBackground(F.ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Fill(e.Graphics, new D.RectangleF(0, 0, e.ToolStrip.Width, e.ToolStrip.Height), 16 * e.Graphics.DpiX / 96, colors.Surface);
        }
        protected override void OnRenderToolStripBorder(F.ToolStripRenderEventArgs e)
        {
            using var path = Round(new D.RectangleF(.5f, .5f, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 16 * e.Graphics.DpiX / 96);
            using var pen = new D.Pen(D.Color.FromArgb(90, colors.Outline)); e.Graphics.DrawPath(pen, path);
        }
        protected override void OnRenderMenuItemBackground(F.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected) return;
            float s = e.Graphics.DpiX / 96;
            var color = e.Item.Name == "ExitApp" ? Blend(colors.Error, colors.Surface, .13f) : colors.Container;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Fill(e.Graphics, new D.RectangleF(2 * s, 2 * s, e.Item.Width - 4 * s, e.Item.Height - 4 * s), 11 * s, color);
        }
        protected override void OnRenderSeparator(F.ToolStripSeparatorRenderEventArgs e)
        {
            float s = e.Graphics.DpiX / 96;
            using var pen = new D.Pen(Blend(colors.Outline, colors.Surface, .5f));
            e.Graphics.DrawLine(pen, 12 * s, e.Item.Height / 2f, e.Item.Width - 12 * s, e.Item.Height / 2f);
        }
        protected override void OnRenderItemImage(F.ToolStripItemImageRenderEventArgs e)
        {
            if (e.Item.Name != "BrandHeader" || e.Image is null) return;
            float s = e.Graphics.DpiX / 96;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(e.Image, new D.RectangleF(10 * s, 13 * s, 34 * s, 34 * s));
        }
        protected override void OnRenderItemText(F.ToolStripItemTextRenderEventArgs e)
        {
            float s = e.Graphics.DpiX / 96;
            int Scale(float value) => (int)Math.Round(value * s);
            var flags = F.TextFormatFlags.Left | F.TextFormatFlags.VerticalCenter | F.TextFormatFlags.SingleLine | F.TextFormatFlags.NoPrefix;
            if (e.Item.Name == "BrandHeader")
            {
                using var title = new D.Font("Segoe UI", 11.5f, D.FontStyle.Bold);
                using var detail = new D.Font("Segoe UI", 9f);
                F.TextRenderer.DrawText(e.Graphics, "Next Share", title, new D.Rectangle(Scale(55), Scale(9), e.Item.Width - Scale(65), Scale(24)), colors.Ink, flags);
                using var dot = new D.SolidBrush(colors.Primary);
                e.Graphics.FillEllipse(dot, Scale(58), Scale(39), Scale(5), Scale(5));
                F.TextRenderer.DrawText(e.Graphics, menu.ReceiverStatus, detail, new D.Rectangle(Scale(67), Scale(30), e.Item.Width - Scale(75), Scale(23)), colors.Muted, flags);
                return;
            }
            bool exit = e.Item.Name == "ExitApp";
            var ink = exit ? colors.Error : colors.Primary;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new D.Pen(ink, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float x = 16 * s, y = (e.Item.Height - 20 * s) / 2;
            if (exit)
            {
                e.Graphics.DrawArc(pen, x + 2 * s, y + 3 * s, 16 * s, 16 * s, -50, 280);
                e.Graphics.DrawLine(pen, x + 10 * s, y, x + 10 * s, y + 9 * s);
            }
            else
            {
                e.Graphics.DrawLines(pen, new D.PointF[] { new(x + 9 * s, y + 3 * s), new(x + 2 * s, y + 3 * s), new(x + 2 * s, y + 18 * s), new(x + 17 * s, y + 18 * s), new(x + 17 * s, y + 11 * s) });
                e.Graphics.DrawLine(pen, x + 9 * s, y + 10 * s, x + 19 * s, y);
                e.Graphics.DrawLines(pen, new D.PointF[] { new(x + 12 * s, y), new(x + 19 * s, y), new(x + 19 * s, y + 7 * s) });
            }
            F.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, new D.Rectangle(Scale(51), 0, e.Item.Width - Scale(60), e.Item.Height), exit ? colors.Error : colors.Ink, flags);
        }
        private static D.Color Blend(D.Color foreground, D.Color background, float amount) => D.Color.FromArgb(
            (int)(foreground.R * amount + background.R * (1 - amount)),
            (int)(foreground.G * amount + background.G * (1 - amount)),
            (int)(foreground.B * amount + background.B * (1 - amount)));
    }
}
