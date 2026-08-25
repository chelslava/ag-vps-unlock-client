#!/usr/bin/env bash
# AG VPS Unlock — управление токенами и состоянием релея (/etc/agvps/state.json).
# Только root. Зависимости: bash, python3 (стандартная библиотека), без jq.
set -euo pipefail

STATE_DIR="/etc/agvps"
STATE_FILE="$STATE_DIR/state.json"

err() { printf 'Ошибка: %s\n' "$*" >&2; }
die() { err "$@"; exit 1; }

if [[ $EUID -ne 0 ]]; then
  die "требуются права root: запустите через sudo, например: sudo $0 list"
fi

usage() {
  cat <<EOF
AG VPS Unlock — управление токенами клиентов

Использование: agvps-token.sh <команда> [аргументы]

Команды:
  add <имя>            создать токен для клиента и показать секрет
  list                 таблица всех токенов (включая полные секреты)
  show <имя>           напечатать ТОЛЬКО секрет клиента
  copy <имя>           как show + подсказка по передаче клиенту
   revoke <имя>         отозвать токен клиента
   legacy-list          секреты из внешнего файла бота (только чтение)
   lock on|off|status   блокировка tcp/443 по allow-листу (по умолчанию off)
  allow list           показать allow-лист (IP, добавленные knock'ами)
  allow remove <ip>    убрать IP из allow-листа

Файл состояния: $STATE_FILE
Лог демона:     journalctl -u agvpsd -f
EOF
}

init_state() {
  mkdir -p "$STATE_DIR"
  chmod 700 "$STATE_DIR"
  if [[ ! -f "$STATE_FILE" ]]; then
    printf '%s\n' '{"tokens": [], "allow": [], "lock": false}' > "$STATE_FILE"
    chmod 600 "$STATE_FILE"
  fi
}

# Вся работа с JSON — через встроенный python3 (без jq).
# Аргументы: <подкоманда> [параметры...]; путь к state.json добавляется здесь.
state_py() {
  python3 - "$STATE_FILE" "$@" <<'PY'
import json
import os
import secrets
import sys
import time

path, cmd, args = sys.argv[1], sys.argv[2], sys.argv[3:]

with open(path, encoding="utf-8") as f:
    st = json.load(f)
st.setdefault("tokens", [])
st.setdefault("allow", [])
st.setdefault("lock", False)


def save():
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(st, f, ensure_ascii=False, indent=2)
        f.write("\n")
    os.chmod(tmp, 0o600)
    os.replace(tmp, path)


def find(name):
    for t in st["tokens"]:
        if t.get("name") == name:
            return t
    return None


if cmd == "find":
    t = find(args[0])
    if t is None:
        sys.exit(3)
    print(t["secret"])

elif cmd == "add":
    name = args[0]
    if find(name) is not None:
        sys.exit(4)
    st["tokens"].append({
        "name": name,
        "secret": secrets.token_urlsafe(32),
        "created": int(time.time()),
        "revoked": False,
        "last_used": None,
        "last_ip": None,
    })
    save()

elif cmd == "list":
    for t in st["tokens"]:
        created = time.strftime("%Y-%m-%d", time.localtime(t.get("created") or 0))
        lu = t.get("last_used")
        last = "-" if lu is None else time.strftime("%Y-%m-%d %H:%M", time.localtime(lu))
        rev = "да" if t.get("revoked") else "нет"
        row = [t.get("name", "?"), created, rev, last,
               t.get("last_ip") or "-", t["secret"]]
        print("\t".join(row))

elif cmd == "revoke":
    t = find(args[0])
    if t is None:
        sys.exit(5)
    if not t.get("revoked"):
        t["revoked"] = True
        save()
        sys.exit(0)
    sys.exit(6)

elif cmd == "lock":
    mode = args[0]
    if mode in ("on", "off"):
        want = (mode == "on")
        if bool(st["lock"]) != want:
            st["lock"] = want
            save()
    else:  # status
        print("on" if st["lock"] else "off")

elif cmd == "allow-list":
    for c in st["allow"]:
        print(c)

elif cmd == "allow-remove":
    ip = args[0]
    before = len(st["allow"])
    st["allow"] = [c for c in st["allow"] if c.split("/")[0] != ip]
    if len(st["allow"]) != before:
        save()
    else:
        sys.exit(7)

else:
    sys.exit(2)
PY
}

require_name() {
  [[ $# -ge 1 && -n ${1:-} ]] || die "укажите имя клиента, например: agvps-token.sh add my-laptop"
  [[ $1 =~ ^[A-Za-z0-9._-]{1,64}$ ]] || die "имя клиента: латиница/цифры/._- , до 64 символов"
}

cmd_add() {
  require_name "$@"
  local name="$1" rc=0 secret=""
  init_state
  state_py add "$name" || rc=$?
  case $rc in
    0) ;;
    4) die "клиент «$name» уже существует; отзовите старый токен (revoke) или выберите другое имя" ;;
    *) die "не удалось обновить $STATE_FILE" ;;
  esac
  secret=$(state_py find "$name") || die "внутренняя ошибка: токен не найден после создания"
  printf '=== ТОКЕН ДЛЯ КЛИЕНТА «%s» ===\n%s\n==============================\n' "$name" "$secret"
  printf 'Передайте этот секрет клиенту: поле «Токен» в приложении AgVpsUnlock.\n'
}

cmd_list() {
  init_state
  local rows
  rows=$(state_py list) || die "не удалось прочитать $STATE_FILE"
  printf '%-18s %-10s %-7s %-16s %-15s %s\n' \
    "ИМЯ" "СОЗДАН" "ОТОЗВАН" "ПОСЛ.ВХОД" "ПОСЛ.IP" "СЕКРЕТ (полный)"
  if [[ -z "$rows" ]]; then
    printf '(токенов пока нет — создайте: agvps-token.sh add <имя>)\n'
    return 0
  fi
  printf '%s\n' "$rows" | awk -F'\t' '{
    printf "%-18s %-10s %-7s %-16s %-15s %s\n", $1, $2, $3, $4, $5, $6
  }'
}

cmd_show() {
  require_name "$@"
  init_state
  local secret
  secret=$(state_py find "$1") || die "клиент «$1» не найден (см.: agvps-token.sh list)"
  printf '%s\n' "$secret"
}

cmd_copy() {
  require_name "$@"
  init_state
  local secret
  secret=$(state_py find "$1") || die "клиент «$1» не найден (см.: agvps-token.sh list)"
  # Секрет — в stdout (можно пайпить), подсказки — в stderr.
  printf '%s\n' "$secret"
  {
    printf '# Скопируйте строку выше (в SSH-сессии достаточно выделить её мышью).\n'
    printf '# Затем вставьте токен в приложение AgVpsUnlock: поле «Токен» → «Сохранить».\n'
  } >&2
}

cmd_revoke() {
  require_name "$@"
  init_state
  local rc=0
  state_py revoke "$1" || rc=$?
  case $rc in
    0) printf 'Токен «%s» отозван.\n' "$1" ;;
    5) die "клиент «$1» не найден (см.: agvps-token.sh list)" ;;
    6) printf 'Токен «%s» уже был отозван.\n' "$1" ;;
    *) die "не удалось обновить $STATE_FILE" ;;
  esac
}

cmd_legacy_list() {
  # Внешний файл, который пополняет бот подписок: демон принимает эти
  # секреты наравне с state.json (AGVPS_LEGACY_TOKENS в unit-файле).
  local f=${AGVPS_LEGACY_TOKENS:-/opt/agvps/tokens}
  [[ -f "$f" ]] || die "внешний файл токенов не найден: $f"
  printf 'Внешний файл: %s (только чтение; выдача и отзыв — через бот)\n' "$f"
  awk '{ printf "%3d  %s\n", NR, $1 }' "$f"
}

cmd_lock() {
  local mode=${1:-}
  init_state
  case "$mode" in
    on|off)
      state_py lock "$mode"
      printf 'Блокировка firewall: %s.\n' "$([[ $mode == on ]] && echo ВКЛ || echo ВЫКЛ)"
      if [[ $mode == on ]]; then
        printf 'Правила применит демон agvpsd в течение секунды (journalctl -u agvpsd -f).\n'
      fi
      ;;
    status)
      local lock_state cnt
      lock_state=$(state_py lock status)
      cnt=$(state_py allow-list | wc -l)
      printf 'Блокировка firewall: %s\n' "$([[ $lock_state == on ]] && echo ВКЛ || echo ВЫКЛ)"
      printf 'Allow-лист: %s адрес(ов) — просмотр: agvps-token.sh allow list\n' "$cnt"
      printf 'Блокировка опциональна и по умолчанию выключена; правила применяет демон agvpsd.\n'
      ;;
    *)
      err "укажите режим: lock on|off|status"
      exit 1
      ;;
  esac
}

cmd_allow() {
  local sub=${1:-}
  init_state
  case "$sub" in
    list)
      local rows
      rows=$(state_py allow-list) || die "не удалось прочитать $STATE_FILE"
      if [[ -z "$rows" ]]; then
        printf '(allow-лист пуст)\n'
      else
        printf '%s\n' "$rows"
      fi
      ;;
    remove)
      [[ $# -ge 2 ]] || die "укажите IP: agvps-token.sh allow remove 1.2.3.4"
      local ip="$2"
      [[ $ip =~ ^[0-9a-fA-F:.]{3,45}$ ]] || die "некорректный IP: $ip"
      local rc=0
      state_py allow-remove "$ip" || rc=$?
      case $rc in
        0) printf 'IP %s удалён из allow-листа.\n' "$ip" ;;
        7) printf 'IP %s отсутствует в allow-листе.\n' "$ip" ;;
        *) die "не удалось обновить $STATE_FILE" ;;
      esac
      ;;
    *)
      err "укажите подкоманду: allow list | allow remove <ip>"
      exit 1
      ;;
  esac
}

case ${1:-} in
  add)    shift; cmd_add "$@" ;;
  list)   cmd_list ;;
  show)   shift; cmd_show "$@" ;;
  copy)   shift; cmd_copy "$@" ;;
  revoke) shift; cmd_revoke "$@" ;;
  legacy-list) cmd_legacy_list ;;
  lock)   shift; cmd_lock "$@" ;;
  allow)  shift; cmd_allow "$@" ;;
  -h|--help|help|"") usage ;;
  *) usage >&2; err "неизвестная команда: $1"; exit 1 ;;
esac
