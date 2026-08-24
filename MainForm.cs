using System.Diagnostics;
using System.Net;
using AgVpsUnlock.Core;

namespace AgVpsUnlock;

public sealed class MainForm : Form
{
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
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(860, 640);

        BuildUi();
        RefreshAll();
    }

    private void BuildUi()
    {
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Padding = new Padding(12, 14, 12, 10),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true
        };
        top.Controls.Add(new Label
        {
            Text = "IP сервера (VPS):",
            AutoSize = true,
            Margin = new Padding(3, 8, 8, 0)
        });
        _ipBox = new TextBox { Width = 160, Text = _config.VpsIp };
        _ipBox.TextChanged += (_, _) => _probeLabel.Text = "";
        top.Controls.Add(_ipBox);
        _saveBtn = MkBtn("Сохранить", SaveConfig);
        _probeBtn = MkBtn("Проверить сервер", async () => await ProbeAsync());
        top.Controls.Add(_saveBtn);
        top.Controls.Add(_probeBtn);
        Controls.Add(top);

        var mid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(12, 0, 12, 0)
        };
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        var installsGroup = new GroupBox { Text = "Установки", Dock = DockStyle.Fill };
        _installsList = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f) };
        installsGroup.Controls.Add(_installsList);

        var stateGroup = new GroupBox { Text = "Состояние", Dock = DockStyle.Fill };
        var stateLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        stateLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        stateLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _hostsLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };
        _probeLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };
        stateLayout.Controls.Add(_hostsLabel);
        stateLayout.Controls.Add(_probeLabel);
        stateGroup.Controls.Add(stateLayout);

        mid.Controls.Add(installsGroup, 0, 0);
        mid.Controls.Add(stateGroup, 1, 0);
        Controls.Add(mid);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Padding = new Padding(12, 8, 12, 4),
            AutoSize = true
        };
        _applyBtn = MkBtn("Применить патч", ApplyPatch);
        _applyBtn.BackColor = Color.FromArgb(232, 245, 233);
        _rollbackBtn = MkBtn("Полный откат", RollbackAll);
        _rollbackBtn.BackColor = Color.FromArgb(253, 235, 236);
        actions.Controls.Add(_applyBtn);
        actions.Controls.Add(_rollbackBtn);
        Controls.Add(actions);

        _log = new TextBox
        {
            Dock = DockStyle.Bottom,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(250, 250, 250),
            Font = new Font("Consolas", 9f),
            Height = 190
        };
        Controls.Add(_log);

        // Z-order: fill panel must be behind the docked siblings.
        Controls.SetChildIndex(mid, Math.Max(0, Controls.Count - 1));
    }

    private static Button MkBtn(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(4), Margin = new Padding(3, 4, 10, 3) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Button MkBtn(string text, Func<Task> onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(4), Margin = new Padding(3, 4, 10, 3) };
        b.Click += async (_, _) => { b.Enabled = false; try { await onClick(); } finally { b.Enabled = true; } };
        return b;
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
                    _ => "неизвестно"
                };
                _installsList.Items.Add($"{st,-13} {bin}");
            }
        }
        _hostsLabel.Text = HostsManager.IsApplied()
            ? $"hosts: закреплено ({HostsManager.CurrentEntries().Count} имён)"
            : "hosts: блока нет";
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
            _probeLabel.Text = "Некорректный IP";
            return;
        }
        _probeLabel.Text = "Проверка...";
        var res = await ServerProbe.ProbeAsync(ip);
        if (!res.TcpOk)
        {
            _probeLabel.Text = $"Сервер недоступен: {res.Error ?? "таймаут"}";
            _probeLabel.ForeColor = Color.Firebrick;
            return;
        }
        if (!res.TlsOk)
        {
            _probeLabel.Text = "443 открыт, но Google-сертификат не получен — SNI-форвардер не настроен?";
            _probeLabel.ForeColor = Color.DarkOrange;
            return;
        }
        var dnsNote = res.DnsReachable switch
        {
            false => "; UDP/53 не отвечает (не критично — используется hosts)",
            true when res.DnsHijacked => "; DNS перехватывается провайдером → работает hosts",
            _ => "; DNS отвечает"
        };
        _probeLabel.Text = $"Сервер работает, туннель до Google в порядке{dnsNote}";
        _probeLabel.ForeColor = Color.ForestGreen;
    }

    private void ApplyPatch()
    {
        var ip = _config.VpsIp.Trim();
        if (!IPAddress.TryParse(ip, out _))
        {
            MessageBox.Show("Сначала укажите и сохраните IP сервера.", "Ошибка",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        UseWaitCursor = true;
        try
        {
            Log("Завершаем процессы Antigravity...");
            KillAntigravityProcesses();

            Log("Патчим бинарники...");
            var (patched, already, failed) = BinaryPatcher.ApplyAll(Log);

            Log("Закрепляем имена в hosts за сервером...");
            var ok = HostsManager.Apply(_config.RoutedHosts(), IPAddress.Parse(ip));
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
