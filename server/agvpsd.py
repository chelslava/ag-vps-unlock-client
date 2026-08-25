#!/usr/bin/env python3
"""AG VPS Unlock — knock-демон релея.

Слушает UDP :1604 (порт меняется переменной окружения AGVPS_PORT) и
принимает 26-байтные knock-пакеты от клиента AgVpsUnlock:

    байты 0-1   ASCII "AG"
    байты 2-9   uint64 BIG-ENDIAN unix-время отправки (секунды)
    байты 10-25 первые 16 байт HMAC-SHA256(
                    key = UTF8(token_secret),
                    msg = UTF8("agvps|" + str(ts)))

Окно валидности: |now - ts| <= 300 секунд. На валидный knock демон
отвечает b"K" из ТОГО ЖЕ сокета, который принял пакет (иначе connected
UDP-сокет клиента ответ не примет), добавляет IP источника в allow-лист
и обновляет last_used/last_ip. На неверный HMAC, формат или метку
времени отвечает b"X" — не чаще 5 раз в секунду на один источник.

Состояние: /etc/agvps/state.json, атомарная запись (tmp + rename),
файл 0600, каталог 0700. Секреты никогда не попадают в лог.

Совместимость: переменная AGVPS_LEGACY_TOKENS задаёт путь к внешнему
файлу секретов (один на строку), который пополняет сторонняя автоматика
— например бот подписок. Такие токены проверяются наравне с
state.json и не редактируются демоном.

Только стандартная библиотека Python 3.9+.
"""

import hashlib
import hmac
import json
import os
import signal
import socket
import sys
import time
from pathlib import Path

MAGIC = b"AG"
PKT_LEN = 26
TS_WINDOW = 300          # секунд допустимого расхождения часов
X_RATE_LIMIT = 5         # максимум 'X'-ответов на один источник...
X_RATE_WINDOW = 1.0      # ...за это количество секунд

STATE_FILE = Path(os.environ.get("AGVPS_STATE", "/etc/agvps/state.json"))
LOCK_FILE = Path(os.environ.get("AGVPS_LOCK", "/run/agvpsd.lock"))
# Внешний файл секретов (один на строку), пополняемый вне демона —
# например ботом подписок. Пусто = не использовать.
LEGACY_TOKENS = os.environ.get("AGVPS_LEGACY_TOKENS", "").strip()

_running = True


def log(msg):
    print(time.strftime("%Y-%m-%d %H:%M:%S"), msg, flush=True)


def die(msg):
    print("agvpsd:", msg, file=sys.stderr, flush=True)
    sys.exit(1)


# ---------------------------------------------------------------- состояние


def _default_state():
    return {"tokens": [], "allow": [], "lock": False}


class StateStore:
    """Загрузка/атомарное сохранение state.json с отслеживанием изменений
    на диске (CLI может править файл параллельно с демоном)."""

    def __init__(self, path):
        self.path = path
        self.last_mtime = None

    def _mtime(self):
        try:
            return os.stat(self.path).st_mtime
        except OSError:
            return None

    def changed_on_disk(self):
        m = self._mtime()
        if self.last_mtime is None or m is None:
            return True
        return m != self.last_mtime

    def load(self):
        try:
            with open(self.path, "r", encoding="utf-8") as f:
                st = json.load(f)
            if not isinstance(st, dict):
                raise ValueError("корень JSON не объект")
        except FileNotFoundError:
            st = _default_state()
            self.save(st)
            return st
        except (OSError, ValueError) as e:
            die(f"не удалось прочитать {self.path}: {e}")
        if not isinstance(st.get("tokens"), list):
            st["tokens"] = []
        if not isinstance(st.get("allow"), list):
            st["allow"] = []
        st["lock"] = bool(st.get("lock", False))
        self.last_mtime = self._mtime()
        return st

    def save(self, st):
        ensure_dirs()
        tmp = str(self.path) + ".tmp"
        data = json.dumps(st, ensure_ascii=False, indent=2) + "\n"
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, self.path)
        os.chmod(self.path, 0o600)
        self.last_mtime = self._mtime()


def ensure_dirs():
    d = STATE_FILE.parent
    d.mkdir(parents=True, exist_ok=True)
    try:
        os.chmod(d, 0o700)
    except OSError:
        pass


# ------------------------------------------------------------ внешние токены


_legacy_cache = []
_legacy_mtime = None


def load_legacy_tokens():
    """Секреты из внешнего файла (AGVPS_LEGACY_TOKENS), если задан.

    Файл пополняется вне демона (например ботом), поэтому перечитываем
    по mtime при каждом обращении. Формат совместим со старым knock.py:
    один секрет в строке (первое поле), пустые строки и #комментарии
    пропускаются. При недоступности файла возвращаем последний кэш.
    """
    global _legacy_cache, _legacy_mtime
    if not LEGACY_TOKENS:
        return []
    try:
        m = os.stat(LEGACY_TOKENS).st_mtime
    except OSError:
        return _legacy_cache
    if m == _legacy_mtime:
        return _legacy_cache
    try:
        with open(LEGACY_TOKENS, "rb") as f:
            rows = [ln.decode("utf-8", "replace").split() for ln in f]
        secrets_ = [r[0] for r in rows if r and not r[0].startswith("#")]
    except OSError:
        return _legacy_cache
    _legacy_cache = secrets_
    _legacy_mtime = m
    return _legacy_cache


# ------------------------------------------------------------- firewall lock


def _which(name):
    for d in ("/usr/local/sbin", "/usr/local/bin", "/usr/sbin",
              "/usr/bin", "/sbin", "/bin"):
        p = os.path.join(d, name)
        if os.path.isfile(p) and os.access(p, os.X_OK):
            return p
    for d in os.environ.get("PATH", "").split(os.pathsep):
        if not d:
            continue
        p = os.path.join(d, name)
        if os.path.isfile(p) and os.access(p, os.X_OK):
            return p
    return None


def split_cidrs(allow):
    v4, v6 = [], []
    for c in allow:
        (v6 if ":" in c else v4).append(c)
    return v4, v6


class Firewall:
    """Консервативное применение lock=true: только собственная таблица
    nftables `inet agvps` либо собственные цепочки iptables AGVPS/AGVPS6.
    Чужие правила никогда не изменяются и не сбрасываются; при любой
    ошибке сделанное откатывается. lock выключен по умолчанию."""

    def __init__(self):
        self.backend = None       # "nft" | "ipt" | None
        self.applied = None       # ключ последнего применённого состояния
        self.active = False
        self.created_obj = None   # создали ли таблицу/цепочку сами (nft)
        self.retry_after = 0.0

    def detect(self):
        if _which("nft"):
            self.backend = "nft"
        elif _which("iptables"):
            self.backend = "ipt"
        else:
            self.backend = None
            log("firewall: nft/iptables не найдены — блокировка не работает")

    def _run(self, cmd, quiet=False):
        rc = os.system(cmd)
        if rc != 0 and not quiet:
            log(f"firewall: ошибка (код {rc}): {cmd}")
        return rc == 0

    # -- nftables ------------------------------------------------------------

    def _nft_apply(self, v4, v6):
        nft = _which("nft")
        have_table = self._run(f"{nft} list table inet agvps >/dev/null 2>&1",
                               quiet=True)
        if self.created_obj is None:
            self.created_obj = not have_table
        if not have_table and not self._run(f"{nft} add table inet agvps"):
            return False
        if not self._run(f"{nft} list chain inet agvps input >/dev/null 2>&1",
                         quiet=True):
            if not self._run(
                f"{nft} add chain inet agvps input "
                f"'{{ type filter hook input priority -10; policy accept; }}'"
            ):
                return False
        if not self._run(f"{nft} flush chain inet agvps input"):
            return False
        if not self._run(f'{nft} add rule inet agvps input iif "lo" accept'):
            return False
        for c in v4:
            if not self._run(
                    f"{nft} add rule inet agvps input ip saddr {c} "
                    f"tcp dport 443 accept"):
                return False
        for c in v6:
            if not self._run(
                    f"{nft} add rule inet agvps input ip6 saddr {c} "
                    f"tcp dport 443 accept"):
                return False
        return self._run(f"{nft} add rule inet agvps input tcp dport 443 drop")

    def _nft_teardown(self):
        nft = _which("nft")
        if self.created_obj:
            self._run(f"{nft} delete table inet agvps", quiet=True)
        else:
            self._run(f"{nft} flush chain inet agvps input", quiet=True)

    # -- iptables ------------------------------------------------------------

    def _ipt_family(self, bin_, chain, cidrs):
        have_chain = self._run(f"{bin_} -n -L {chain} >/dev/null 2>&1",
                               quiet=True)
        if not have_chain and not self._run(f"{bin_} -N {chain}"):
            return False
        if not self._run(f"{bin_} -F {chain}"):
            return False
        for c in cidrs:
            if not self._run(
                    f"{bin_} -A {chain} -s {c} -p tcp --dport 443 -j ACCEPT"):
                return False
        if not self._run(f"{bin_} -A {chain} -p tcp --dport 443 -j DROP"):
            return False
        jump = f"-p tcp --dport 443 -j {chain}"
        if not self._run(f"{bin_} -C INPUT {jump} >/dev/null 2>&1", quiet=True):
            if not self._run(f"{bin_} -I INPUT 1 {jump}"):
                return False
        return True

    def _ipt_apply(self, v4, v6):
        ok = True
        ipt = _which("iptables")
        if ipt and (v4 or not _which("ip6tables")):
            ok &= self._ipt_family(ipt, "AGVPS", v4)
        ip6 = _which("ip6tables")
        if ip6 and v6:
            ok &= self._ipt_family(ip6, "AGVPS6", v6)
        return ok

    def _ipt_teardown(self):
        for bin_, chain in ((_which("iptables"), "AGVPS"),
                            (_which("ip6tables"), "AGVPS6")):
            if not bin_:
                continue
            self._run(f"{bin_} -D INPUT -p tcp --dport 443 -j {chain}",
                      quiet=True)
            self._run(f"{bin_} -F {chain}", quiet=True)
            self._run(f"{bin_} -X {chain}", quiet=True)

    # -- наружный интерфейс --------------------------------------------------

    def _teardown(self):
        if self.backend == "nft":
            self._nft_teardown()
        elif self.backend == "ipt":
            self._ipt_teardown()

    def apply(self, lock, allow):
        key = (bool(lock), tuple(sorted(allow)))
        if key == self.applied:
            return
        now = time.time()
        if now < self.retry_after:
            return
        if not lock:
            if self.active:
                self._teardown()
                self.active = False
                log("firewall: блокировка выключена, правила agvps сняты")
            self.applied = key
            return
        if self.backend is None:
            self.detect()
        if self.backend is None:
            self.applied = key
            return
        v4, v6 = split_cidrs(allow)
        fn = self._nft_apply if self.backend == "nft" else self._ipt_apply
        if fn(v4, v6):
            self.active = True
            self.applied = key
            log(f"firewall: tcp/443 ограничен allow-листом "
                f"(v4: {len(v4)}, v6: {len(v6)}), backend={self.backend}")
        else:
            log("firewall: применение НЕ УДАЛОСЬ, откат правил agvps")
            self._teardown()
            self.active = False
            self.retry_after = now + 30.0

    def shutdown(self):
        if self.active:
            self._teardown()
            self.active = False
            log("firewall: правила agvps сняты при остановке")


# ------------------------------------------------------------------- протокол


def normalize_ip(ip):
    fam = socket.AF_INET6 if ":" in ip else socket.AF_INET
    return socket.inet_ntop(fam, socket.inet_pton(fam, ip))


def cidr_of(ip):
    return f"{ip}/32" if ":" not in ip else f"{ip}/128"


def check_knock(payload, state, now, legacy=()):
    """Возвращает (токен|None, причина). Секреты в лог не попадают.

    legacy — кортеж секретов из внешнего файла: проверяются после
    токенов state.json, совпадение возвращает псевдо-токен bot#N.
    """
    if len(payload) != PKT_LEN or len(payload) < 10 or payload[:2] != MAGIC:
        return None, "format"
    ts = int.from_bytes(payload[2:10], "big")
    if abs(now - ts) > TS_WINDOW:
        return None, "timestamp"
    mac = payload[10:]
    msg = ("agvps|" + str(ts)).encode("utf-8")
    for tok in state["tokens"]:
        if tok.get("revoked"):
            continue
        secret = tok.get("secret") or ""
        expect = hmac.new(secret.encode("utf-8"), msg,
                          hashlib.sha256).digest()[:16]
        if hmac.compare_digest(mac, expect):
            return tok, "ok"
    for i, secret in enumerate(legacy, 1):
        expect = hmac.new(secret.encode("utf-8"), msg,
                          hashlib.sha256).digest()[:16]
        if hmac.compare_digest(mac, expect):
            return {"name": f"bot#{i}", "legacy": True}, "ok"
    return None, "hmac"


_x_hits = {}


def x_allowed(ip):
    now = time.time()
    hits = [t for t in _x_hits.get(ip, []) if now - t < X_RATE_WINDOW]
    if len(hits) >= X_RATE_LIMIT:
        _x_hits[ip] = hits
        return False
    hits.append(now)
    _x_hits[ip] = hits
    if len(_x_hits) > 4096:  # защита от распухания на скан-трафике
        cutoff = now - X_RATE_WINDOW
        for k in [k for k, v in _x_hits.items()
                  if not v or v[-1] < cutoff]:
            del _x_hits[k]
    return True


# ---------------------------------------------------------------- single-run


def _pid_alive(pid):
    """Жив ли процесс (POSIX-семантика os.kill(pid, 0)).

    На не-POSIX системах os.kill(pid, 0) имеет другие (опасные)
    семантики, поэтому там консервативно считаем процесс живым.
    """
    if os.name == "posix":
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        except PermissionError:
            return True  # процесс существует, но чужой
        except OSError:
            return True
        return True
    return True


def acquire_lock():
    pid_txt = ""
    try:
        pid_txt = LOCK_FILE.read_text(encoding="utf-8").strip()
    except OSError:
        pass
    if pid_txt.isdigit() and _pid_alive(int(pid_txt)):
        die(f"уже запущен (pid {int(pid_txt)}), lock: {LOCK_FILE}")
    try:
        fd = os.open(str(LOCK_FILE), os.O_WRONLY | os.O_CREAT | os.O_EXCL,
                     0o644)
        os.write(fd, str(os.getpid()).encode("ascii"))
        os.close(fd)
    except FileExistsError:
        die(f"lock-файл занят: {LOCK_FILE}")


def release_lock():
    try:
        LOCK_FILE.unlink()
    except OSError:
        pass


def _stop(signum, frame):
    global _running
    _running = False


# ---------------------------------------------------------------------- main


def main():
    try:
        port = int(os.environ.get("AGVPS_PORT", "1604"))
    except ValueError:
        die("AGVPS_PORT должен быть числом")
    if not 1 <= port <= 65535:
        die("AGVPS_PORT вне диапазона 1..65535")

    signal.signal(signal.SIGTERM, _stop)
    signal.signal(signal.SIGINT, _stop)
    if hasattr(signal, "SIGBREAK"):  # Windows: Ctrl+Break
        signal.signal(signal.SIGBREAK, _stop)

    acquire_lock()
    store = StateStore(STATE_FILE)
    fw = Firewall()
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        state = store.load()
        legacy = load_legacy_tokens()
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind(("0.0.0.0", port))
        sock.settimeout(1.0)
        fw.apply(state["lock"], state["allow"])
        log(f"запущен: udp/{port}, state={STATE_FILE}, "
            f"tokens={len(state['tokens'])}, "
            f"legacy={len(legacy)} ({LEGACY_TOKENS or 'нет'}), "
            f"allow={len(state['allow'])}, "
            f"lock={'on' if state['lock'] else 'off'}")

        while _running:
            # CLI мог изменить файл состояния (revoke/lock/allow) — подхватить
            if store.changed_on_disk():
                state = store.load()
                fw.apply(state["lock"], state["allow"])
            legacy = load_legacy_tokens()  # дёшево: перечитка по mtime

            try:
                data, addr = sock.recvfrom(2048)
            except socket.timeout:
                continue
            except OSError:
                break

            raw_ip = addr[0]
            try:
                ip = normalize_ip(raw_ip)
            except (OSError, ValueError):
                ip = raw_ip
            now = int(time.time())

            tok, reason = check_knock(data, state, now, legacy)
            if tok is not None:
                changed = False
                cidr = cidr_of(ip)
                if cidr not in state["allow"]:
                    state["allow"].append(cidr)
                    changed = True
                if not tok.get("legacy") and (
                        tok.get("last_used") != now
                        or tok.get("last_ip") != ip):
                    tok["last_used"] = now
                    tok["last_ip"] = ip
                    changed = True
                if changed:
                    store.save(state)
                reply = b"K"
                log(f"ok   name={tok.get('name', '?')} ip={ip}")
                if state["lock"]:
                    fw.apply(True, state["allow"])
            else:
                reply = b"X"
                log(f"bad  ip={ip} reason={reason} size={len(data)}")
                if not x_allowed(ip):
                    log(f"drop ip={ip} reason=rate-limit")
                    continue

            # Ответ строго из того же сокета: connected UdpClient клиента
            # принимает пакеты только с адреса, куда сам отправил запрос.
            try:
                sock.sendto(reply, addr)
            except OSError as e:
                log(f"warn sendto {ip}: {e}")

        log("остановлен")
    finally:
        fw.shutdown()
        sock.close()
        release_lock()


if __name__ == "__main__":
    main()
