using System.Drawing;

namespace AgVpsUnlock.UI;

public static class AppTheme
{
    public static readonly Color WindowBack = Color.FromArgb(0x1B, 0x1D, 0x23);
    public static readonly Color CardBack = Color.FromArgb(0x23, 0x26, 0x2E);
    public static readonly Color BorderColor = Color.FromArgb(0x31, 0x35, 0x3F);
    public static readonly Color InputBack = Color.FromArgb(0x15, 0x17, 0x1C);
    public static readonly Color LogBack = Color.FromArgb(0x15, 0x17, 0x1C);
    public static readonly Color TextPrimary = Color.FromArgb(0xE8, 0xEA, 0xED);
    public static readonly Color TextSecondary = Color.FromArgb(0x9A, 0xA0, 0xA6);
    public static readonly Color Accent = Color.FromArgb(0x7A, 0xA2, 0xF7);
    public static readonly Color Success = Color.FromArgb(0x81, 0xC9, 0x95);
    public static readonly Color Warning = Color.FromArgb(0xFD, 0xD6, 0x63);
    public static readonly Color Danger = Color.FromArgb(0xF2, 0x8B, 0x82);
    public static readonly Color SecondaryBtnBack = Color.FromArgb(0x2A, 0x2E, 0x38);
    public static readonly Color DisabledBack = Color.FromArgb(0x22, 0x25, 0x2D);
    public static readonly Color DisabledFore = Color.FromArgb(0x8A, 0x90, 0x9B);
    public static readonly Color SelectionBack = Color.FromArgb(0x2E, 0x33, 0x40);

    public static readonly Font BaseFont = new("Segoe UI", 9.75f);
    public static readonly Font TitleFont = new("Segoe UI Semibold", 15f);
    public static readonly Font CardHeaderFont = new("Segoe UI Semibold", 11f);
    public static readonly Font CaptionFont = new("Segoe UI", 8.25f);
    public static readonly Font MonoFont = new("Consolas", 9f);

    public static Button CreateButton(string text, Func<Task> onClick, Color back, Color fore, Color hover, Color pressed, Color? border = null, Action<string>? onLog = null)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 0, 12, 0),
            UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize = border is null ? 0 : 1;
        if (border is not null)
            b.FlatAppearance.BorderColor = border.Value;
        b.FlatAppearance.MouseOverBackColor = hover;
        b.FlatAppearance.MouseDownBackColor = pressed;
        b.EnabledChanged += (_, _) =>
        {
            if (b.Enabled) { b.BackColor = back; b.ForeColor = fore; }
            else { b.BackColor = DisabledBack; b.ForeColor = DisabledFore; }
        };
        b.Click += async (_, _) =>
        {
            b.Enabled = false;
            try { await onClick(); }
            catch (Exception ex)
            {
                onLog?.Invoke($"[!!] {ex.Message}");
            }
            finally
            {
                b.Enabled = true;
            }
        };
        return b;
    }

    public static Button CreateButton(string text, Action onClick, Color back, Color fore, Color hover, Color pressed, Color? border = null, Action<string>? onLog = null)
        => CreateButton(text, () => { onClick(); return Task.CompletedTask; }, back, fore, hover, pressed, border, onLog);
}
