using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using AgVpsUnlock.Core;
using AgVpsUnlock.UI;

namespace AgVpsUnlock;

public sealed class MainForm : Form
{
    private readonly ConfigStore _config = ConfigStore.Load();

    private TextBox _ipBox = null!;
    private TextBox _tokenBox = null!;
    private bool _lastInstallsFound;
    private bool _lastAllPatched;
    private bool _lastHostsApplied;
    private bool? _lastProbeGreen;
    private Button _saveBtn = null!;
    private Button _probeBtn = null!;
    private Button _applyBtn = null!;
    private Button _rollbackBtn = null!;
    private Button _refreshBtn = null!;
    private ListBox _installsList = null!;
    private Label _hostsLabel = null!;
    private Label _summaryLabel = null!;
    private Label _probeLabel = null!;
    private TextBox _log = null!;

    public MainForm()
    {
        var infoVer = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var shortVer = infoVer?.Split('+')[0];
        Text = "Antigravity VPS Unlock" + (string.IsNullOrEmpty(shortVer) ? "" : $"  v{shortVer}");
        Font = (Font)AppTheme.BaseFont.Clone();
        BackColor = AppTheme.WindowBack;
        ForeColor = AppTheme.TextPrimary;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(840, 620);
        Size = new Size(920, 700);
        DoubleBuffered = true;

        BuildUi();
        _ = RefreshAllAsync();
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
            BackColor = AppTheme.WindowBack,
            Padding = new Padding(18, 14, 18, 10)
        };
        var subtitle = new Label
        {
            Text = "Безопасный доступ к Antigravity из любого региона",
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = AppTheme.TextSecondary,
            Font = new Font("Segoe UI", 9f)
        };
        var title = new Label
        {
            Text = "Antigravity VPS Unlock",
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = AppTheme.TextPrimary,
            Font = (Font)AppTheme.TitleFont.Clone(),
            Padding = new Padding(0, 0, 0, 4)
        };
        _summaryLabel = new Label
        {
            Text = "",
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = AppTheme.TextSecondary,
            Font = new Font("Segoe UI", 9.5f),
            Padding = new Padding(0, 6, 0, 0)
        };
        header.Controls.Add(_summaryLabel);
        header.Controls.Add(subtitle);
        header.Controls.Add(title);

        var ipRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            BackColor = AppTheme.WindowBack,
            Padding = new Padding(18, 4, 18, 14)
        };

        var ipCaption = new Label
        {
            Text = "IP сервера (VPS):",
            AutoSize = true,
            ForeColor = AppTheme.TextSecondary,
            Margin = new Padding(0, 10, 12, 0)
        };
        _ipBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = AppTheme.InputBack,
            ForeColor = AppTheme.TextPrimary,
            Width = 212,
            Font = (Font)AppTheme.BaseFont.Clone(),
            Text = _config.VpsIp,
            PlaceholderText = "например, 203.0.113.10"
        };
        var ipHost = new Panel
        {
            Width = 216,
            Height = _ipBox.Height + 4,
            Padding = new Padding(1, 2, 1, 2),
            BackColor = AppTheme.BorderColor,
            Margin = new Padding(0, 5, 14, 0)
        };
        _ipBox.Dock = DockStyle.Fill;
        _ipBox.TextChanged += (_, _) => _probeLabel.Text = "";
        _ipBox.GotFocus += (_, _) => ipHost.BackColor = AppTheme.Accent;
        _ipBox.LostFocus += (_, _) => ipHost.BackColor = AppTheme.BorderColor;
        _ipBox.KeyDown += (_, ev) =>
        {
            if (ev.KeyCode != Keys.Enter || !_probeBtn.Enabled) return;
            ev.SuppressKeyPress = true;
            _probeBtn.PerformClick();
        };
        ipHost.Controls.Add(_ipBox);

        _saveBtn = AppTheme.CreateButton("Сохранить", SaveConfig,
            AppTheme.SecondaryBtnBack, AppTheme.TextPrimary,
            Color.FromArgb(0x34, 0x39, 0x45), Color.FromArgb(0x22, 0x26, 0x30), onLog: Log);
        _probeBtn = AppTheme.CreateButton("Проверить сервер", ProbeAsync,
            AppTheme.Accent, Color.FromArgb(0x0F, 0x14, 0x22),
            Color.FromArgb(0x92, 0xB4, 0xF9), Color.FromArgb(0x69, 0x93, 0xEB), onLog: Log);

        ipRow.Controls.Add(ipCaption);
        ipRow.Controls.Add(ipHost);
        ipRow.Controls.Add(_saveBtn);
        ipRow.Controls.Add(_probeBtn);

        var tokenRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            BackColor = AppTheme.WindowBack,
            Padding = new Padding(18, 0, 18, 10)
        };
        var tokCaption = new Label
        {
            Text = "Токен доступа:",
            AutoSize = true,
            ForeColor = AppTheme.TextSecondary,
            Margin = new Padding(0, 8, 12, 0)
        };
        _tokenBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = AppTheme.InputBack,
            ForeColor = AppTheme.TextPrimary,
            Width = 320,
            Font = (Font)AppTheme.BaseFont.Clone(),
            Text = _config.VpsToken,
            PlaceholderText = "выдаётся вместе с подпиской"
        };
        var tokHost = new Panel
        {
            Width = 324,
            Height = _tokenBox.Height + 4,
            Padding = new Padding(1, 2, 1, 2),
            BackColor = AppTheme.BorderColor,
            Margin = new Padding(0, 5, 14, 0)
        };
        _tokenBox.Dock = DockStyle.Fill;
        _tokenBox.GotFocus += (_, _) => tokHost.BackColor = AppTheme.Accent;
        _tokenBox.LostFocus += (_, _) => tokHost.BackColor = AppTheme.BorderColor;
        tokHost.Controls.Add(_tokenBox);
        tokenRow.Controls.Add(tokCaption);
        tokenRow.Controls.Add(tokHost);

        var mid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.WindowBack,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(18, 2, 18, 2)
        };
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        var installsCard = NewCard("Установки", out var installsBody);
        installsCard.Margin = new Padding(0, 0, 5, 0);

        var installsHeaderTools = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            BackColor = AppTheme.CardBack,
            Padding = new Padding(0, 6, 0, 0)
        };
        var addPathLink = new LinkLabel
        {
            Text = "+ Указать путь вручную...",
            AutoSize = true,
            LinkColor = AppTheme.Accent,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, 0, 12, 0)
        };
        addPathLink.Click += (_, _) => AddCustomPathDialog();
        var clearPathsLink = new LinkLabel
        {
            Text = "Сбросить кастомные пути",
            AutoSize = true,
            LinkColor = AppTheme.TextSecondary,
            LinkBehavior = LinkBehavior.HoverUnderline
        };
        clearPathsLink.Click += (_, _) => ClearCustomPaths();
        installsHeaderTools.Controls.Add(addPathLink);
        installsHeaderTools.Controls.Add(clearPathsLink);

        _installsList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = AppTheme.CardBack,
            ForeColor = AppTheme.TextPrimary,
            Font = (Font)AppTheme.MonoFont.Clone(),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Math.Max(18, AppTheme.MonoFont.Height + 6),
            IntegralHeight = false
        };
        _installsList.DrawItem += Installs_DrawItem;
        installsBody.Controls.Add(_installsList);
        installsBody.Controls.Add(installsHeaderTools);

        var stateCard = NewCard("Состояние", out var stateBody);
        stateCard.Margin = new Padding(5, 0, 0, 0);
        var stateGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.CardBack,
            ColumnCount = 1,
            RowCount = 3
        };
        stateGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        stateGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        stateGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _hostsLabel = MakeStatusValue();
        _probeLabel = MakeStatusValue();
        SetStatus(_hostsLabel, AppTheme.TextSecondary, "• нет данных");
        SetStatus(_probeLabel, AppTheme.TextSecondary, "• ещё не проверялся");
        stateGrid.Controls.Add(StatusCell("Файл hosts", _hostsLabel), 0, 0);
        stateGrid.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.BorderColor, Margin = Padding.Empty }, 0, 1);
        stateGrid.Controls.Add(StatusCell("Проверка сервера", _probeLabel), 0, 2);
        stateBody.Controls.Add(stateGrid);

        mid.Controls.Add(installsCard, 0, 0);
        mid.Controls.Add(stateCard, 1, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            BackColor = AppTheme.WindowBack,
            Padding = new Padding(18, 6, 18, 12)
        };
        _applyBtn = AppTheme.CreateButton("Применить патч", ApplyPatchAsync,
            AppTheme.Success, Color.FromArgb(0x10, 0x1B, 0x15),
            Color.FromArgb(0x92, 0xD6, 0xA8), Color.FromArgb(0x6E, 0xB7, 0x85), onLog: Log);
        _rollbackBtn = AppTheme.CreateButton("Полный откат", RollbackAllAsync,
            AppTheme.CardBack, AppTheme.Danger,
            Color.FromArgb(0x3A, 0x28, 0x2B), Color.FromArgb(0x2E, 0x21, 0x24), border: AppTheme.Danger, onLog: Log);
        _refreshBtn = AppTheme.CreateButton("Обновить", () => _ = RefreshAllAsync(),
            AppTheme.SecondaryBtnBack, AppTheme.TextSecondary,
            Color.FromArgb(0x34, 0x39, 0x45), Color.FromArgb(0x22, 0x26, 0x30), onLog: Log);
        actions.Controls.Add(_applyBtn);
        actions.Controls.Add(_rollbackBtn);
        actions.Controls.Add(_refreshBtn);

        var logHost = new Panel
        {
            Dock = DockStyle.Bottom,
            BackColor = AppTheme.WindowBack,
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
            BackColor = AppTheme.LogBack,
            ForeColor = AppTheme.TextPrimary,
            Font = (Font)AppTheme.MonoFont.Clone()
        };
        var logTools = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            BackColor = AppTheme.LogBack,
            Padding = new Padding(2, 4, 2, 6)
        };
        var copyLink = new LinkLabel
        {
            Text = "Копировать всё",
            AutoSize = true,
            LinkColor = AppTheme.Accent,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, 0, 16, 0)
        };
        copyLink.Click += (_, _) =>
        {
            try { Clipboard.SetText(_log.Text); }
            catch (Exception ex) { Log("[!!] Буфер обмена: " + ex.Message); }
        };
        var clearLink = new LinkLabel
        {
            Text = "Очистить",
            AutoSize = true,
            LinkColor = AppTheme.Accent,
            LinkBehavior = LinkBehavior.HoverUnderline
        };
        clearLink.Click += (_, _) => _log.Clear();
        logTools.Controls.Add(copyLink);
        logTools.Controls.Add(clearLink);

        logHost.Controls.Add(_log);
        logHost.Controls.Add(logTools);

        Controls.Add(mid);
        Controls.Add(logHost);
        Controls.Add(actions);
        Controls.Add(tokenRow);
        Controls.Add(ipRow);
        Controls.Add(header);

        AcceptButton = _probeBtn;
        ResumeLayout(true);
    }

    private void AddCustomPathDialog()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Выберите файл language_server.exe, agy.exe или Antigravity.exe",
            Filter = "Исполняемые файлы (*.exe)|*.exe|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (ofd.ShowDialog(this) == DialogResult.OK)
        {
            var path = ofd.FileName;
            if (!_config.CustomInstallPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _config.CustomInstallPaths.Add(path);
                _config.Save();
                Log($"Добавлен пользовательский путь: {path}");
                _ = RefreshAllAsync();
            }
        }
    }

    private void ClearCustomPaths()
    {
        if (_config.CustomInstallPaths.Count == 0) return;
        _config.CustomInstallPaths.Clear();
        _config.Save();
        Log("Пользовательские пути сброшены");
        _ = RefreshAllAsync();
    }

    private static CardPanel NewCard(string title, out Control body)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.CardBack,
            Padding = new Padding(14, 12, 14, 12)
        };
        var header = new Label
        {
            Text = title,
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = AppTheme.TextPrimary,
            Font = (Font)AppTheme.CardHeaderFont.Clone(),
            Padding = new Padding(2, 0, 0, 10)
        };
        body = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBack, Padding = new Padding(2) };
        card.Controls.Add(body);
        card.Controls.Add(header);
        return card;
    }

    private static Panel StatusCell(string caption, Label value)
    {
        var cell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.CardBack,
            Padding = new Padding(2, 6, 6, 6)
        };
        var cap = new Label
        {
            Text = caption,
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = AppTheme.TextSecondary,
            Font = (Font)AppTheme.CaptionFont.Clone(),
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
        ForeColor = AppTheme.TextSecondary
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
        using (var bg = new SolidBrush(selected ? AppTheme.SelectionBack : AppTheme.CardBack))
            e.Graphics.FillRectangle(bg, e.Bounds);
        var color =
            text.StartsWith("НОВАЯ", StringComparison.Ordinal) ? AppTheme.Warning :
            text.StartsWith("пропатчен", StringComparison.Ordinal) ? AppTheme.Success :
            text.StartsWith("НЕ", StringComparison.Ordinal) ? AppTheme.Danger :
            text.StartsWith("Antigravity", StringComparison.Ordinal) ? AppTheme.TextSecondary :
            AppTheme.TextPrimary;
        TextRenderer.DrawText(e.Graphics, text, e.Font ?? AppTheme.MonoFont,
            new Point(e.Bounds.X + 2, e.Bounds.Y + 2), selected ? AppTheme.TextPrimary : color,
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if ((e.State & DrawItemState.Focus) != 0)
            e.DrawFocusRectangle();
    }

    private void Log(string msg)
    {
        if (_log.InvokeRequired) { _log.BeginInvoke(() => Log(msg)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
    }

    private bool _refreshing;

    /// <summary>Scans installs and hosts state off the UI thread; safe to fire
    /// from the constructor (placeholder row until the scan lands).</summary>
    private async Task RefreshAllAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _installsList.BeginUpdate();
            _installsList.Items.Clear();
            _installsList.Items.Add("Сканирование установок...");
            _applyBtn.Enabled = _rollbackBtn.Enabled = false;
            _installsList.EndUpdate();

            var customPaths = _config.CustomInstallPaths.ToList();
            var data = await Task.Run(() =>
            {
                var rows = new List<(string Status, string Bin)>();
                foreach (var inst in BinaryPatcher.FindInstalls(customPaths))
                    foreach (var bin in inst.Binaries)
                    {
                        var st = BinaryPatcher.Inspect(bin) switch
                        {
                            BinaryPatcher.BinaryState.Patched => "пропатчен",
                            BinaryPatcher.BinaryState.Unpatched => "НЕ пропатчен",
                            _ => "НОВАЯ ВЕРСИЯ?"
                        };
                        rows.Add((st, bin));
                    }

                bool hostsApplied = HostsManager.IsApplied();
                int hostsCount = hostsApplied ? HostsManager.CurrentEntries().Count : 0;
                return (Rows: rows, HostsApplied: hostsApplied, HostsCount: hostsCount);
            });

            _installsList.BeginUpdate();
            _installsList.Items.Clear();
            if (data.Rows.Count == 0)
            {
                _installsList.Items.Add("Antigravity не найдена");
                _applyBtn.Enabled = _rollbackBtn.Enabled = false;
            }
            else
            {
                _applyBtn.Enabled = _rollbackBtn.Enabled = true;
                foreach (var (status, bin) in data.Rows)
                    _installsList.Items.Add($"{status,-13} {bin}");
            }
            _installsList.EndUpdate();

            if (data.HostsApplied)
                SetStatus(_hostsLabel, AppTheme.Success, $"✓ hosts: закреплено ({data.HostsCount} имён)");
            else
                SetStatus(_hostsLabel, AppTheme.TextSecondary, "• hosts: блока нет");

            _lastInstallsFound = data.Rows.Count > 0;
            _lastAllPatched = data.Rows.Count > 0 && data.Rows.All(r => r.Status == "пропатчен");
            _lastHostsApplied = data.HostsApplied;
            UpdateSummary();
        }
        catch (Exception ex)
        {
            Log($"[!!] Ошибка обновления состояния: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
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
        _config.VpsToken = _tokenBox.Text.Trim();
        _config.Save();
        Log($"Конфигурация сохранена: {ip}");
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (!_lastInstallsFound)
            SetStatus(_summaryLabel, AppTheme.Warning, "• Antigravity не найдена — установите и нажмите «Обновить»");
        else if (!IPAddress.TryParse(_ipBox.Text.Trim(), out _))
            SetStatus(_summaryLabel, AppTheme.Warning, "• Укажите IP сервера и нажмите «Сохранить»");
        else if (!_lastHostsApplied || !_lastAllPatched)
            SetStatus(_summaryLabel, AppTheme.Danger, "• Требуется патч — нажмите «Применить патч»");
        else if (_lastProbeGreen == false)
            SetStatus(_summaryLabel, AppTheme.Danger, "• Патч есть, но проверка не пройдена — нажмите «Проверить сервер»");
        else if (_lastProbeGreen == true)
            SetStatus(_summaryLabel, AppTheme.Success, "✓ Всё готово к работе");
        else
            SetStatus(_summaryLabel, AppTheme.TextSecondary, "• Нажмите «Проверить сервер» для финальной проверки");
    }

    private async Task ProbeAsync()
    {
        var ip = _ipBox.Text.Trim();
        if (!IPAddress.TryParse(ip, out _))
        {
            SetStatus(_probeLabel, AppTheme.Danger, "✗ Некорректный IP");
            Log($"Проверка отменена: некорректный IP «{ip}».");
            return;
        }
        _lastProbeGreen = null;
        if (_config.VpsToken.Length > 0)
        {
            SetStatus(_probeLabel, AppTheme.TextSecondary, "• Проверка доступа...");
            var knocked = await KnockClient.SendAsync(ip, _config.VpsToken);
            Log(knocked ? "[OK] Доступ подтверждён" : "[!!] Доступ не подтверждён — сверьте токен или напишите в поддержку");
        }
        var progress = new Progress<string>(msg => SetStatus(_probeLabel, AppTheme.TextSecondary, $"• {msg}"));
        Log($"Проверка {ip}:443...");

        ProbeResult res;
        try
        {
            res = await ServerProbe.ProbeAsync(ip, _config.RoutedHosts(), progress: progress);
        }
        catch (Exception ex)
        {
            SetStatus(_probeLabel, AppTheme.Danger, $"✗ Ошибка проверки: {ex.Message}");
            _lastProbeGreen = false;
            UpdateSummary();
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
            _lastProbeGreen = false;
            UpdateSummary();
            SetStatus(_probeLabel, AppTheme.Danger, $"✗ Сервер недоступен: {res.Error ?? "таймаут"}");
            return;
        }
        if (!res.TlsOk)
        {
            var reason = string.IsNullOrEmpty(res.Error) ? "" : $" ({res.Error})";
            _lastProbeGreen = false;
            UpdateSummary();
            SetStatus(_probeLabel, AppTheme.Warning,
                "! Порт 443 открыт, но сертификат Google не получен" + reason + " — SNI-форвардер не настроен или соединение перехватывается?");
            return;
        }
        if (res.RoutingLeak)
        {
            _lastProbeGreen = false;
            UpdateSummary();
            SetStatus(_probeLabel, AppTheme.Danger,
                $"✗ Туннель до Google работает, НО часть имён уходит мимо сервера! {res.LeakDetail} Это вызывает ошибку «User location is not supported». Пере-примените патч и проверьте IPv6.");
            return;
        }
        var dnsNote = res.DnsReachable switch
        {
            false => "; UDP/53 не отвечает (не критично — используется hosts)",
            true when res.DnsHijacked => "; DNS перехватывается провайдером → работает hosts",
            _ => "; DNS отвечает"
        };
        _lastProbeGreen = true;
        UpdateSummary();
        SetStatus(_probeLabel, AppTheme.Success, $"✓ Сервер работает, туннель до Google в порядке{dnsNote}");
    }

    private bool AnyAntigravityRunning() =>
        Process.GetProcessesByName("Antigravity").Length > 0 ||
        Process.GetProcessesByName("agy").Length > 0 ||
        Process.GetProcessesByName("language_server").Length > 0;

    private async Task ApplyPatchAsync()
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

        if (_config.VpsToken.Length > 0)
        {
            Log("Проверяем доступ...");
            var knocked = await KnockClient.SendAsync(ip, _config.VpsToken);
            Log(knocked ? "[OK] Доступ подтверждён" : "[!!] Доступ не подтверждён — сверьте токен или напишите в поддержку");
        }

        UseWaitCursor = true;
        try
        {
            var customPaths = _config.CustomInstallPaths.ToList();
            await Task.Run(() =>
            {
                Log($"Патчим на сервер {ip}...");
                Log("Завершаем процессы Antigravity...");
                KillAntigravityProcesses();

                Log("Патчим бинарники...");
                var (patched, already, failed) = BinaryPatcher.ApplyAll(Log, customPaths);

                Log("Закрепляем имена в hosts за сервером...");
                var ok = HostsManager.Apply(_config.RoutedHosts(), addr);
                Log(ok ? "[OK] hosts обновлён" : $"[!!] не удалось записать hosts: {HostsManager.LastError ?? "нужны права администратора"}");

                Log($"\nГотово. Патчей: {patched}, уже было: {already}, ошибок: {failed}.");
                Log("Запустите Antigravity и войдите в аккаунт Google.");
            });
            await RefreshAllAsync();
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async Task RollbackAllAsync()
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
            var customPaths = _config.CustomInstallPaths.ToList();
            await Task.Run(() =>
            {
                Log("Возвращаем бинарники к исходному состоянию...");
                KillAntigravityProcesses();
                var (restored, failed) = BinaryPatcher.RestoreAll(Log, customPaths);
                var hostsOk = HostsManager.Remove();
                Log(hostsOk ? "[OK] hosts-блок удалён" : $"[!!] не удалось изменить hosts: {HostsManager.LastError ?? "причина неизвестна"}");
                Log($"\nОткат завершён. Восстановлено: {restored}, ошибок: {failed}.");
            });
            await RefreshAllAsync();
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private static void KillAntigravityProcesses()
    {
        using var p = Process.Start(new ProcessStartInfo("taskkill",
            "/F /IM \"Antigravity.exe\" /IM \"Antigravity IDE.exe\" /IM \"Antigravity CLI.exe\"" +
            " /IM \"agy.exe\" /IM \"language_server.exe\" /IM \"language_server_windows_x64.exe\"")
        { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(5000);
    }
}
