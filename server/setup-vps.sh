#!/usr/bin/env bash
# AG VPS Unlock — установка серверной части на Ubuntu/Debian (systemd).
# Копирует agvpsd.py и agvpsd.service, включает службу.
# Правила firewall во время установки НЕ меняются — только подсказки.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_PATH="/usr/local/bin/agvpsd.py"
UNIT_PATH="/etc/systemd/system/agvpsd.service"
STATE_DIR="/etc/agvps"

err() { printf 'Ошибка: %s\n' "$*" >&2; }
die() { err "$@"; exit 1; }
info() { printf '%s\n' "$*"; }

if [[ $EUID -ne 0 ]]; then
  die "требуются права root: запустите через sudo, например: sudo ./setup-vps.sh"
fi

[[ -f "$SCRIPT_DIR/agvpsd.py" ]] || die "не найден $SCRIPT_DIR/agvpsd.py — запускайте из каталога server/"
[[ -f "$SCRIPT_DIR/agvpsd.service" ]] || die "не найден $SCRIPT_DIR/agvpsd.service"
command -v systemctl >/dev/null 2>&1 || die "systemctl не найден: скрипт рассчитан на Ubuntu/Debian с systemd"
command -v python3 >/dev/null 2>&1 || die "python3 не найден: установите его (apt install python3)"

# Повторный запуск: останавливаем нашу же службу, иначе порт busy.
if systemctl list-unit-files 2>/dev/null | grep -q '^agvpsd.service'; then
  systemctl stop agvpsd.service 2>/dev/null || true
fi

PORT="1604"
if command -v ss >/dev/null 2>&1 && \
   ss -lun 2>/dev/null | awk '{print $5}' | grep -Eq "(^|:)${PORT}\$"; then
  err "UDP ${PORT} уже занят другим процессом — служба agvpsd не сможет запуститься."
  err "Найдите владельца порта:  ss -lunp | grep ${PORT}"
  err "остановите его (например: systemctl disable --now agvps-knock) и повторите установку."
  exit 1
fi

# Существующий файл токенов от сторонней автоматики (бот подписок)?
# Демон будет принимать эти секреты наравне со state.json.
LEGACY_HINT="/opt/agvps/tokens"

info "==> Копирование демона в $BIN_PATH"
install -m 0755 "$SCRIPT_DIR/agvpsd.py" "$BIN_PATH"

info "==> Копирование CLI в /usr/local/bin/agvps-token.sh"
install -m 0755 "$SCRIPT_DIR/agvps-token.sh" "/usr/local/bin/agvps-token.sh"

info "==> Копирование unit-файла в $UNIT_PATH"
install -m 0644 "$SCRIPT_DIR/agvpsd.service" "$UNIT_PATH"
if [[ -f "$LEGACY_HINT" ]]; then
  info "==> Найден внешний файл токенов $LEGACY_HINT — включаю режим совместимости"
  if ! grep -q '^Environment=AGVPS_LEGACY_TOKENS=' "$UNIT_PATH"; then
    sed -i "/^Environment=AGVPS_PORT=/a Environment=AGVPS_LEGACY_TOKENS=$LEGACY_HINT" "$UNIT_PATH"
  fi
fi

info "==> Подготовка $STATE_DIR (700) и файла состояния (600)"
mkdir -p "$STATE_DIR"
chmod 700 "$STATE_DIR"
if [[ ! -f "$STATE_DIR/state.json" ]]; then
  printf '%s\n' '{"tokens": [], "allow": [], "lock": false}' > "$STATE_DIR/state.json"
fi
chmod 600 "$STATE_DIR/state.json"

info "==> systemctl daemon-reload && enable --now agvpsd"
systemctl daemon-reload
systemctl enable --now agvpsd.service

if systemctl is-active --quiet agvpsd.service; then
  info "==> Служба agvpsd запущена"
else
  err "служба agvpsd не запустилась — смотрите журнал:"
  err "  journalctl -u agvpsd -n 50 --no-pager"
  exit 1
fi

cat <<'EOF'

Готово. Дальнейшие шаги:

  1. Создайте токен клиента:
       sudo agvps-token.sh add my-laptop

  2. Скопируйте секрет для передачи клиенту:
       sudo agvps-token.sh copy my-laptop

  3. В приложении AgVpsUnlock введите IP этого сервера и токен,
     нажмите «Сохранить», затем «Проверить сервер».

  4. Убедитесь, что UDP 1604 доступен извне. Если ufw активен:
       sudo ufw allow 1604/udp
     (правила firewall при установке намеренно не менялись)

  5. Опционально закройте tcp/443 для всех, кроме knock-клиентов:
       sudo agvps-token.sh lock on      # включить
       sudo agvps-token.sh lock status  # текущее состояние

Лог демона:   journalctl -u agvpsd -f
Токены:       sudo agvps-token.sh list
EOF
