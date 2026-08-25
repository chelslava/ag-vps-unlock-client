using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using AgVpsUnlock.Core;

namespace AgVpsUnlock;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return 0;
        }

        AttachConsole(AttachParentProcess);

        var config = ConfigStore.Load();
        string command = "";
        string? customIp = null;
        string? customToken = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h" or "/?")
            {
                PrintHelp();
                return 0;
            }
            if (arg is "--apply" or "--rollback" or "--probe" or "--status")
            {
                command = arg;
            }
            else if (arg == "--ip" && i + 1 < args.Length)
            {
                customIp = args[++i];
            }
            else if (arg == "--token" && i + 1 < args.Length)
            {
                customToken = args[++i];
            }
        }

        var ip = customIp ?? config.VpsIp;
        var token = customToken ?? config.VpsToken;

        switch (command)
        {
            case "--status":
                return RunStatus(config);

            case "--probe":
                return await RunProbeAsync(ip, token, config);

            case "--apply":
                return await RunApplyAsync(ip, token, config);

            case "--rollback":
                return RunRollback(config);

            default:
                Console.WriteLine($"[!!] Неизвестная команда или аргументы: {string.Join(" ", args)}");
                PrintHelp();
                return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"
Antigravity VPS Unlock CLI
Использование:
  AgVpsUnlock.exe                       Запустить графический интерфейс (GUI)
  AgVpsUnlock.exe --status              Показать статус установок Antigravity и hosts
  AgVpsUnlock.exe --probe [--ip <ip>]   Проверить доступность VPS и корректность TLS/DNS
  AgVpsUnlock.exe --apply [--ip <ip>]   Применить патч и закрепить hosts за сервером
  AgVpsUnlock.exe --rollback            Откатить патч и удалить hosts-блок
  AgVpsUnlock.exe --help                Показать эту справку
");
    }

    private static int RunStatus(ConfigStore config)
    {
        Console.WriteLine($"--- Antigravity VPS Unlock Status ---");
        Console.WriteLine($"VPS IP в конфиге: {(string.IsNullOrEmpty(config.VpsIp) ? "(не задан)" : config.VpsIp)}");
        var installs = BinaryPatcher.FindInstalls(config.CustomInstallPaths);
        Console.WriteLine($"Найдено установок Antigravity: {installs.Count}");
        foreach (var inst in installs)
        {
            Console.WriteLine($"  Папка: {inst.Root}");
            foreach (var bin in inst.Binaries)
            {
                var st = BinaryPatcher.Inspect(bin);
                Console.WriteLine($"    [{st}] {bin}");
            }
        }
        var hostsApplied = HostsManager.IsApplied();
        var entries = HostsManager.CurrentEntries();
        Console.WriteLine($"Файл hosts: {(hostsApplied ? $"Закреплен ({entries.Count} записей)" : "Блок не найден")}");
        return 0;
    }

    private static async Task<int> RunProbeAsync(string ip, string token, ConfigStore config)
    {
        if (!IPAddress.TryParse(ip, out _))
        {
            Console.WriteLine($"[!!] Некорректный IP адрес: {ip}");
            return 1;
        }

        if (!string.IsNullOrEmpty(token))
        {
            Console.WriteLine($"Отправка knock-пакета на {ip}...");
            var knocked = await KnockClient.SendAsync(ip, token);
            Console.WriteLine(knocked ? "[OK] Knock подтвержден" : "[!!] Knock не ответил");
        }

        Console.WriteLine($"Проверка сервера {ip}...");
        var res = await ServerProbe.ProbeAsync(ip, config.RoutedHosts());
        Console.WriteLine($"  TCP 443: {(res.TcpOk ? "OK" : $"Ошибка ({res.Error})")}");
        Console.WriteLine($"  TLS Google Cert: {(res.TlsOk ? $"OK ({res.CertificateSubject})" : "FAIL")}");
        Console.WriteLine($"  DNS UDP/53: {(res.DnsReachable ? (res.DnsHijacked ? "Перехватывается провайдером" : "Отвечает") : "Не отвечает")}");
        foreach (var r in res.Resolved)
        {
            Console.WriteLine($"  Host {r.Host} -> {(r.Addresses.Count == 0 ? "не резолвится" : string.Join(", ", r.Addresses))}");
        }

        if (res.RoutingLeak)
        {
            Console.WriteLine($"[!!] Обнаружена утечка маршрутизации: {res.LeakDetail}");
            return 1;
        }

        return res.TcpOk && res.TlsOk ? 0 : 1;
    }

    private static async Task<int> RunApplyAsync(string ip, string token, ConfigStore config)
    {
        if (!IPAddress.TryParse(ip, out var addr))
        {
            Console.WriteLine($"[!!] Некорректный IP адрес: {ip}");
            return 1;
        }

        if (!string.IsNullOrEmpty(token))
        {
            Console.WriteLine($"Проверка доступа через knock...");
            var knocked = await KnockClient.SendAsync(ip, token);
            Console.WriteLine(knocked ? "[OK] Доступ подтвержден" : "[!!] Доступ не подтвержден");
        }

        KillAntigravityProcesses();

        var (patched, already, failed) = BinaryPatcher.ApplyAll(Console.WriteLine, config.CustomInstallPaths);
        var hostsOk = HostsManager.Apply(config.RoutedHosts(), addr);
        Console.WriteLine(hostsOk ? "[OK] Hosts обновлен" : $"[!!] Ошибка hosts: {HostsManager.LastError}");

        config.VpsIp = ip;
        if (!string.IsNullOrEmpty(token)) config.VpsToken = token;
        config.Save();

        Console.WriteLine($"Готово. Пропатчено: {patched}, уже было: {already}, ошибок: {failed}");
        return (failed == 0 && hostsOk) ? 0 : 1;
    }

    private static int RunRollback(ConfigStore config)
    {
        KillAntigravityProcesses();
        var (restored, failed) = BinaryPatcher.RestoreAll(Console.WriteLine, config.CustomInstallPaths);
        var hostsOk = HostsManager.Remove();
        Console.WriteLine(hostsOk ? "[OK] Hosts очищен" : $"[!!] Ошибка hosts: {HostsManager.LastError}");
        Console.WriteLine($"Откат завершен. Восстановлено: {restored}, ошибок: {failed}");
        return (failed == 0 && hostsOk) ? 0 : 1;
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
