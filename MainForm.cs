using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Runtime.InteropServices;
using AgVpsUnlock.Core;

namespace AgVpsUnlock;

public sealed class MainForm : Form
{
    private static readonly Color WindowBack = Color.FromArgb(0x1B, 0x1D, 0x23);
    private static readonly Color CardBack = Color.FromArgb(0x23, 0x26, 0x2E);
    private static readonly Color BorderColor = Color.FromArgb(0x31, 0x35, 0x3F);
    private static readonly Color InputBack = Color.FromArgb(0x15, 0x17, 0x1C);
    private static readonly Color LogBack = Color.FromArgb(0x15, 0x17, 0x1C);
    private static readonly Color TextPrimary = Color.FromArgb(0xE8, 0xEA, 0xED);
    private static readonly Color TextSecondary = Color.FromArgb(0x9A, 0xA0, 0xA6);
    private static readonly Color Accent = Color.FromArgb(0x7A, 0xA2, 0xF7);
    private static readonly Color Success = Color.FromArgb(0x81, 0xC9, 0x95);
    private static readonly Color Warning = Color.FromArgb(0xFD, 0xD6, 0x63);
    private static readonly Color Danger = Color.FromArgb(0xF2, 0x8B, 0x82);
    private static readonly Color SecondaryBtnBack = Color.FromArgb(0x2A, 0x2E, 0x38);
    private static readonly Color DisabledBack = Color.FromArgb(0x22, 0x25, 0x2D);
    private static readonly Color DisabledFore = Color.FromArgb(0x5A, 0x5F, 0x69);
    private static readonly Color SelectionBack = Color.FromArgb(0x2E, 0x33, 0x40);

    private static readonly Font BaseFont = new("Segoe UI", 9.75f);
    private static readonly Font TitleFont = new("Segoe UI Semibold", 15f);
    private static readonly Font CardHeaderFont = new("Segoe UI Semibold", 11f);
    private static readonly Font CaptionFont = new("Segoe UI", 8.25f);
    private static readonly Font MonoFont = new("Consolas", 9f);

    private readonly ConfigStore _config = ConfigStore.Load();

    private TextBox _ipBox = null!;
    private Button _saveBtn = null!;
    private Button _probeBtn = null!;
    private Button _applyBtn = null!;
    private Button _rollbackBtn = null!;
    private ListBox _installsList = null!;
    private Label _hostsLabel = null!;
    private Label _probeLabel = null!;
    private TextBox _log = null!;

    public MainForm()
    {
        Text = "Antigravity VPS Unlock";
        Font = (Font)BaseFont.Clone();
        BackColor = WindowBack;
        ForeColor = TextPrimary;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 600);
        Size = new Size(900, 680);
        DoubleBuffered = true;

        BuildUi();
        RefreshAll();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int on = 1;
            _ = DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
        }
        catch
        {
            // cosmetic only
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void BuildUi()
    {
        SuspendLayout();

        var header = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = WindowBack,
            Padding = new Padding(18, 14, 18, 10)
        };
        var subtitle = new Label
        {
            Text = "Маршрутизация Google API через ваш собственный сервер",
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = TextSecondary,
            Font = new Font("Segoe UI", 9f)
        };
        var title = new Label
        {
            Text = "Antigravity VPS Unlock",
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = TextPrimary,
            Font = (Font)TitleFont.Clone(),
            Padding = new Padding(0, 0, 0, 4)
        };
        header.Controls.Add(subtitle);
        header.Controls.Add(title);

        var ipRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            BackColor = WindowBack,
            Padding = new Padding(18, 4, 18, 14)
        };

        var ipCaption = new Label
        {
            Text = "IP сервера (VPS):",
            AutoSize = true,
            ForeColor = TextSecondary,
            Margin = new Padding(0, 10, 12, 0)
        };
        _ipBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = InputBack,
            ForeColor = TextPrimary,
            Width = 212,
            Font = (Font)BaseFont.Clone(),
            Text = _config.VpsIp,
            PlaceholderText = "например, 203.0.113.10"
        };
        var ipHost = new Panel
        {
            Width = 216,
            Height = _ipBox.Height + 4,
            Padding = new Padding(1, 2, 1, 2),
            BackColor = BorderColor,
            Margin = new Padding(0, 5, 14, 0)
        };
        _ipBox.Dock = DockStyle.Fill;
        _ipBox.TextChanged += (_, _) => _probeLabel.Text = "";
        _ipBox.GotFocus += (_, _) => ipHost.BackColor = Accent;
        _ipBox.LostFocus += (_, _) => ipHost.BackColor = BorderColor;
        _ipBox.KeyDown += (_, ev) =>
        {
            if (ev.KeyCode != Keys.Enter || !_probeBtn.Enabled) return;
            ev.SuppressKeyPress = true;
            _probeBtn.PerformClick();
        };
        ipHost.Controls.Add(_ipBox);

        _saveBtn = MkBtn("Сохранить", SaveConfig,
            SecondaryBtnBack, TextPrimary,
            Color.FromArgb(0x34, 0x39, 0x45), Color.FromArgb(0x22, 0x26, 0x30));
        _probeBtn = MkBtn("Проверить сервер", ProbeAsync,
            Accent, Color.FromArgb(0x0F, 0x14, 0x22),
            Color.FromArgb(0x92, 0xB4, 0xF9), Color.FromArgb(0x69, 0x93, 0xEB));

        ipRow.Controls.Add(ipCaption);
        ipRow.Controls.Add(ipHost);
        ipRow.Controls.Add(_saveBtn);
        ipRow.Controls.Add(_probeBtn);

        var mid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WindowBack,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(18, 2, 18, 2)
        };
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        var installsCard = NewCard("Установки", out var installsBody);
        installsCard.Margin = new Padding(0, 0, 5, 0);
        _installsList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = CardBack,
            ForeColor = TextPrimary,
            Font = (Font)MonoFont.Clone(),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 19,
            IntegralHeight = false
        };
        _installsList.DrawItem += Installs_DrawItem;
        installsBody.Controls.Add(_installsList);

        var stateCard = NewCard("Состояние", out var stateBody);
        stateCard.Margin = new Padding(5, 0, 0, 0);
        var stateGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBack,
            ColumnCount = 1,
            RowCount = 3
        };
        stateGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        stateGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        stateGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _hostsLabel = MakeStatusValue();
        _probeLabel = MakeStatusValue();
        SetStatus(_hostsLabel, TextSecondary, "• нет данных");
        SetStatus(_probeLabel, TextSecondary, "• ещё не проверялся");
        stateGrid.Controls.Add(StatusCell("Файл hosts", _hostsLabel), 0, 0);
        stateGrid.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = BorderColor, Margin = Padding.Empty }, 0, 1);
        stateGrid.Controls.Add(StatusCell("Проверка сервера", _probeLabel), 0, 2);
        stateBody.Controls.Add(stateGrid);

        mid.Controls.Add(installsCard, 0, 0);
        mid.Controls.Add(stateCard, 1, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            BackColor = WindowBack,
            Padding = new Padding(18, 6, 18, 12)
        };
        _applyBtn = MkBtn("Применить патч", ApplyPatch,
            Success, Color.FromArgb(0x10, 0x1B, 0x15),
            Color.FromArgb(0x92, 0xD6, 0xA8), Color.FromArgb(0x6E, 0xB7, 0x85));
        _rollbackBtn = MkOutlinedBtn("Полный откат", RollbackAll,
            CardBack, Danger,
            Color.FromArgb(0x3A, 0x28, 0x2B), Color.FromArgb(0x2E, 0x21, 0x24));
        actions.Controls.Add(_applyBtn);
        actions.Controls.Add(_rollbackBtn);

        var logHost = new Panel
        {
            Dock = DockStyle.Bottom,
            BackColor = WindowBack,
            Padding = new Padding(18, 4, 18, 14),
            Height = 212
        };
        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            TabStop = false,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = LogBack,
            ForeColor = TextPrimary,
            Font = (Font)MonoFont.Clone()
        };
        logHost.Controls.Add(_log);

        Controls.Add(mid);
        Controls.Add(logHost);
        Controls.Add(actions);
        Controls.Add(ipRow);
        Controls.Add(header);

        ResumeLayout(true);
    }

    private static CardPanel NewCard(string title, out Control body)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBack,
            Padding = new Padding(14, 12, 14, 12)
        };
        var header = new Label
        {
            Text = title,
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = TextPrimary,
            Font = (Font)CardHeaderFont.Clone(),
            Padding = new Padding(2, 0, 0, 10)
        };
        body = new Panel { Dock = DockStyle.Fill, BackColor = CardBack, Padding = new Padding(2) };
        card.Controls.Add(body);
        card.Controls.Add(header);
        return card;
    }

    private static Panel StatusCell(string caption, Label value)
    {
        var cell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBack,
            Padding = new Padding(2, 6, 6, 6)
        };
        var cap = new Label
        {
            Text = caption,
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = TextSecondary,
            Font = (Font)CaptionFont.Clone(),
            Padding = new Padding(0, 0, 0, 4)
        };
        cell.Controls.Add(value);
        cell.Controls.Add(cap);
        return cell;
    }

    private static Label MakeStatusValue() => new()
    {
        Dock = DockStyle.Fill,
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = TextSecondary
    };

    private static void SetStatus(Label label, Color color, string text)
    {
        label.ForeColor = color;
        label.Text = text;
    }

    private void Installs_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _installsList.Items.Count) return;
        var text = _installsList.Items[e.Index].ToString() ?? "";
        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using (var bg = new SolidBrush(selected ? SelectionBack : CardBack))
            e.Graphics.FillRectangle(bg, e.Bounds);
        var color =
            text.StartsWith("НОВАЯ", StringComparison.Ordinal) ? Warning :
            text.StartsWith("пропатчен", StringComparison.Ordinal) ? Success :
            text.StartsWith("НЕ", StringComparison.Ordinal) ? Danger :
            text.StartsWith("Antigravity", StringComparison.Ordinal) ? TextSecondary :
            TextPrimary;
        TextRenderer.DrawText(e.Graphics, text, e.Font ?? MonoFont,
            new Point(e.Bounds.X + 2, e.Bounds.Y + 2), selected ? TextPrimary : color,
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private Button MkBtn(string text, Action onClick, Color back, Color fore, Color hover, Color pressed)
        => MkBtnCore(text, () => { onClick(); return Task.CompletedTask; }, back, fore, hover, pressed, border: null);

    private Button MkBtn(string text, Func<Task> onClick, Color back, Color fore, Color hover, Color pressed)
        => MkBtnCore(text, onClick, back, fore, hover, pressed, border: null);

    private Button MkOutlinedBtn(string text, Action onClick, Color back, Color fore, Color hover, Color pressed)
        => MkBtnCore(text, () => { onClick(); return Task.CompletedTask; }, back, fore, hover, pressed, border: fore);

    private Button MkBtnCore(string text, Func<Task> onClick, Color back, Color fore, Color hover, Color pressed, Color? border)
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
                Log($"[!!] {ex.Message}");
            }
            finally
            {
                b.Enabled = true;
            }
        };
        return b;
    }

    private sealed class CardPanel : Panel
    {
        private const int Radius = 8;

        public CardPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using var path = RoundedRect(ClientRectangle, Radius);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent?.BackColor ?? WindowBack);
            using var brush = new SolidBrush(BackColor);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
            using var pen = new Pen(BorderColor, 1f);
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

    private void Log(string msg)
    {
        if (_log.InvokeRequired) { _log.BeginInvoke(() => Log(msg)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
    }

    private void RefreshAll()
    {
        _installsList.Items.Clear();
        var installs = BinaryPatcher.FindInstalls();
        if (installs.Count == 0)
        {
            _installsList.Items.Add("Antigravity не найдена");
            _applyBtn.Enabled = _rollbackBtn.Enabled = false;
            return;
        }
        _applyBtn.Enabled = _rollbackBtn.Enabled = true;
        foreach (var inst in installs)
        {
            foreach (var bin in inst.Binaries)
            {
                var st = BinaryPatcher.Inspect(bin) switch
                {
                    BinaryPatcher.BinaryState.Patched => "пропатчен",
                    BinaryPatcher.BinaryState.Unpatched => "НЕ пропатчен",
                    _ => "НОВАЯ ВЕРСИЯ?"
                };
                _installsList.Items.Add($"{st,-13} {bin}");
            }
        }
        if (HostsManager.IsApplied())
            SetStatus(_hostsLabel, Success, $"✓ hosts: закреплено ({HostsManager.CurrentEntries().Count} имён)");
        else
            SetStatus(_hostsLabel, TextSecondary, "• hosts: блока нет");
    }

    private void SaveConfig()
    {
        var ip = _ipBox.Text.Trim();
        if (!IPAddress.TryParse(ip, out _))
        {
            MessageBox.Show("Введите корректный IPv4-адрес.", "Ошибка",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _config.VpsIp = ip;
        _config.Save();
        Log($"Конфигурация сохранена: {ip}");
    }

    private async Task ProbeAsync()
    {
        var ip = _ipBox.Text.Trim();
        if (!IPAddress.TryParse(ip, out _))
        {
            SetStatus(_probeLabel, Danger, "✗ Некорректный IP");
            Log($"Проверка отменена: некорректный IP «{ip}».");
            return;
        }
        SetStatus(_probeLabel, TextSecondary, $"• Проверка {ip}:443...");
        Log($"Проверка {ip}:443...");

        ProbeResult res;
        try
        {
            res = await ServerProbe.ProbeAsync(ip, _config.RoutedHosts());
        }
        catch (Exception ex)
        {
            SetStatus(_probeLabel, Danger, $"✗ Ошибка проверки: {ex.Message}");
            Log($"[!!] Проверка завершилась ошибкой: {ex.Message}");
            return;
        }

        Log(res.TcpOk ? "TCP 443: открыт" : $"TCP 443: недоступен ({res.Error ?? "таймаут"})");
        if (res.TcpOk)
            Log(res.TlsOk
                ? $"TLS: сертификат Google получен ({res.CertificateSubject ?? "subject не определён"})"
                : "TLS: сертификат Google не получен");
        foreach (var r in res.Resolved)
            Log(r.Addresses.Count == 0
                ? $"DNS {r.Host} → имя не резолвится"
                : $"DNS {r.Host} → {string.Join(", ", r.Addresses)}");
        Log(!res.DnsReachable ? "UDP/53: не отвечает"
            : res.DnsHijacked ? "UDP/53: ответ перехватывается провайдером"
            : "UDP/53: отвечает");
        if (res.RoutingLeak && !string.IsNullOrEmpty(res.LeakDetail))
            Log($"[!!] {res.LeakDetail}");

        if (!res.TcpOk)
        {
            SetStatus(_probeLabel, Danger, $"✗ Сервер недоступен: {res.Error ?? "таймаут"}");
            return;
        }
        if (!res.TlsOk)
        {
            var reason = string.IsNullOrEmpty(res.Error) ? "" : $" ({res.Error})";
            SetStatus(_probeLabel, Warning,
                "! Порт 443 открыт, но сертификат Google не получен" + reason + " — SNI-форвардер не настроен или соединение перехватывается?");
            return;
        }
        if (res.RoutingLeak)
        {
            SetStatus(_probeLabel, Danger,
                $"✗ Туннель до Google работает, НО часть имён уходит мимо сервера! {res.LeakDetail} Это вызывает ошибку «User location is not supported». Пере-примените патч и проверьте IPv6.");
            return;
        }
        var dnsNote = res.DnsReachable switch
        {
            false => "; UDP/53 не отвечает (не критично — используется hosts)",
            true when res.DnsHijacked => "; DNS перехватывается провайдером → работает hosts",
            _ => "; DNS отвечает"
        };
        SetStatus(_probeLabel, Success, $"✓ Сервер работает, туннель до Google в порядке{dnsNote}");
    }

    private bool AnyAntigravityRunning() =>
        Process.GetProcessesByName("Antigravity").Length > 0 ||
        Process.GetProcessesByName("agy").Length > 0 ||
        Process.GetProcessesByName("language_server").Length > 0;

    private void ApplyPatch()
    {
        var ip = _ipBox.Text.Trim();
        if (!IPAddress.TryParse(ip, out var addr))
        {
            MessageBox.Show("Введите корректный IPv4-адрес сервера.", "Ошибка",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _config.VpsIp = ip;
        _config.Save();

        if (AnyAntigravityRunning() && MessageBox.Show(
                "Процессы Antigravity будут принудительно закрыты (несохранённая работа будет потеряна). Продолжить?",
                "Применить патч", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        UseWaitCursor = true;
        try
        {
            Log($"Патчим на сервер {ip}...");
            Log("Завершаем процессы Antigravity...");
            KillAntigravityProcesses();

            Log("Патчим бинарники...");
            var (patched, already, failed) = BinaryPatcher.ApplyAll(Log);

            Log("Закрепляем имена в hosts за сервером...");
            var ok = HostsManager.Apply(_config.RoutedHosts(), addr);
            Log(ok ? "[OK] hosts обновлён" : "[!!] не удалось записать hosts (нужны права администратора)");

            Log($"\nГотово. Патчей: {patched}, уже было: {already}, ошибок: {failed}.");
            Log("Запустите Antigravity и войдите в аккаунт Google.");
            RefreshAll();
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void RollbackAll()
    {
        if (MessageBox.Show(
                "Будут восстановлены исходные бинарники Antigravity и удалён hosts-блок. Процессы Antigravity будут закрыты. Продолжить?",
                "Полный откат", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        UseWaitCursor = true;
        try
        {
            Log("Возвращаем бинарники к исходному состоянию...");
            KillAntigravityProcesses();
            var (restored, failed) = BinaryPatcher.RestoreAll(Log);
            var hostsOk = HostsManager.Remove();
            Log(hostsOk ? "[OK] hosts-блок удалён" : "[!!] не удалось изменить hosts");
            Log($"\nОткат завершён. Восстановлено: {restored}, ошибок: {failed}.");
            RefreshAll();
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private static void KillAntigravityProcesses()
    {
        foreach (var name in new[]
                 {
                     "Antigravity", "Antigravity IDE", "Antigravity CLI",
                     "agy", "language_server", "language_server_windows_x64"
                 })
        {
            using var p = Process.Start(new ProcessStartInfo("taskkill", $"/F /IM \"{name}.exe\"")
            { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(3000);
        }
    }
}
