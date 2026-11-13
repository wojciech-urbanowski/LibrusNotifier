#!/usr/bin/env bash
# Aktualizacja LibrusNotifier na Raspberry Pi: zamyka działającą instancję, podmienia pliki programu na nową wersję
# i uruchamia aplikację z powrotem w tle. Konfiguracja (librus_config.txt) i dane (data/) nie są ruszane.
#
# Użycie:
#   aktualizuj-rpi.sh [--katalog KATALOG] [WERSJA | ARCHIWUM]
#
#   (bez argumentu)    najnowsze wydanie z GitHuba
#   0.3.0              wskazana wersja z GitHuba (także v0.3.0)
#   plik.tar.gz        archiwum z dysku (np. skopiowane przez scp)
#   -k, --katalog      katalog aplikacji (ten z plikiem LibrusNotifier.dll i librus_config.txt)
#   -h, --help         ten opis
#
# Katalog aplikacji skrypt ustala w tej kolejności:
#   1. opcja --katalog
#   2. zmienna środowiskowa LIBRUS_DIR
#   3. katalog ze skryptem, jeśli jest w nim zainstalowana aplikacja (LibrusNotifier.dll)
#   4. bieżący katalog, jeśli jest w nim zainstalowana aplikacja
# Przy pierwszej instalacji (pusty katalog) trzeba podać katalog jawnie (1 lub 2).
#
# Kopie poprzednich plików programu trafiają do katalogu LibrusNotifier-poprzednie obok katalogu aplikacji
# (inny katalog wskazuje zmienna LIBRUS_BACKUP_DIR). Katalog nadrzędny aplikacji musi być zapisywalny.

set -euo pipefail

REPO="wojciech-urbanowski/LibrusNotifier"
STOP_TIMEOUT=180      # sekundy na łagodne zamknięcie (SIGTERM) przed SIGKILL
KEEP_BACKUPS=3        # tyle ostatnich kopii poprzednich plików programu zostaje
START_TIMEOUT=30      # sekundy na potwierdzenie startu w logu

info() { printf '[%s] %s\n' "$(date '+%H:%M:%S')" "$*"; }
warn() { printf '[%s] UWAGA: %s\n' "$(date '+%H:%M:%S')" "$*" >&2; }
die()  { printf '[%s] BŁĄD: %s\n' "$(date '+%H:%M:%S')" "$*" >&2; exit 1; }

# Wypisuje blok komentarza z początku pliku (od drugiej linii do pierwszej pustej) bez znaków '#'.
usage() { sed -n '2,/^$/{/^$/d;s/^# \{0,1\}//;p;}' "${BASH_SOURCE[0]}"; }

# ---------- Argumenty i katalog aplikacji ----------

APP_DIR="${LIBRUS_DIR:-}"
SOURCE=""
while (($# > 0)); do
    case "$1" in
        -h | --help) usage; exit 0 ;;
        -k | --katalog)
            (($# >= 2)) || die "Opcja $1 wymaga podania katalogu."
            APP_DIR="$2"
            shift 2 ;;
        --katalog=*) APP_DIR="${1#*=}"; shift ;;
        -*) die "Nieznana opcja: $1 (opis: --help)." ;;
        *)
            [[ -z "$SOURCE" ]] || die "Za dużo argumentów: $1 (opis: --help)."
            SOURCE="$1"
            shift ;;
    esac
done

if [[ -z "$APP_DIR" ]]; then
    SCRIPT_DIR="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
    for candidate in "$SCRIPT_DIR" "$PWD"; do
        if [[ -f "$candidate/LibrusNotifier.dll" ]]; then
            APP_DIR="$candidate"
            break
        fi
    done
    [[ -n "$APP_DIR" ]] || die "Nie wiem, gdzie jest aplikacja. Podaj katalog: --katalog KATALOG albo zmienna LIBRUS_DIR (opis: --help)."
fi

[[ -d "$APP_DIR" ]] || die "Brak katalogu aplikacji: $APP_DIR"
APP_DIR="$(readlink -f "$APP_DIR")"
PARENT_DIR="$(dirname "$APP_DIR")"
BACKUP_DIR="${LIBRUS_BACKUP_DIR:-$PARENT_DIR/LibrusNotifier-poprzednie}"
info "Katalog aplikacji: $APP_DIR"
[[ -f "$APP_DIR/librus_config.txt" ]] || warn "W $APP_DIR nie ma librus_config.txt - aplikacja nie wystartuje bez konfiguracji."

if command -v dotnet > /dev/null 2>&1; then
    dotnet --list-runtimes 2> /dev/null | grep -q '^Microsoft\.NETCore\.App 10\.' \
        || warn "Nie widzę środowiska .NET 10 (Microsoft.NETCore.App) - build wymaga go na maszynie docelowej."
else
    warn "Nie znaleziono polecenia dotnet - build wymaga środowiska .NET 10 na maszynie docelowej."
fi

# ---------- 1. Archiwum z nową wersją (przed zatrzymaniem aplikacji, żeby przerwa była jak najkrótsza) ----------

WORK_DIR="$(mktemp -d "$PARENT_DIR/.aktualizacja.XXXXXX")"
trap 'rm -rf "$WORK_DIR"' EXIT

ARCHIVE=""
if [[ -n "$SOURCE" && -f "$SOURCE" ]]; then
    ARCHIVE="$(readlink -f "$SOURCE")"
    info "Używam archiwum z dysku: $ARCHIVE"
else
    command -v curl > /dev/null 2>&1 || die "Brak polecenia curl (potrzebne do pobrania wydania)."
    if [[ -z "$SOURCE" ]]; then
        info "Szukam najnowszego wydania w $REPO."
        URL="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
            | grep -o '"browser_download_url": *"[^"]*linux-arm64\.tar\.gz"' | head -n 1 | sed 's/.*: *"\(.*\)"/\1/')" \
            || true
        [[ -n "$URL" ]] || die "Nie udało się ustalić adresu najnowszego wydania (GitHub API)."
    else
        [[ "$SOURCE" =~ ^v?[0-9]+(\.[0-9A-Za-z-]+)*$ ]] || die "Argument '$SOURCE' nie jest ani plikiem, ani numerem wersji (np. 0.3.0)."
        VERSION="${SOURCE#v}"
        URL="https://github.com/$REPO/releases/download/v$VERSION/LibrusNotifier-$VERSION-linux-arm64.tar.gz"
    fi
    ARCHIVE="$WORK_DIR/$(basename "$URL")"
    info "Pobieram $URL"
    curl -fL --progress-bar -o "$ARCHIVE" "$URL" || die "Pobieranie nie powiodło się: $URL"
fi

NEW_DIR="$WORK_DIR/nowa"
mkdir -p "$NEW_DIR"
tar -xzf "$ARCHIVE" -C "$NEW_DIR" --no-same-owner || die "Nie udało się rozpakować archiwum: $ARCHIVE"
[[ -f "$NEW_DIR/LibrusNotifier" && -f "$NEW_DIR/LibrusNotifier.dll" ]] \
    || die "To nie wygląda na archiwum LibrusNotifier (brak LibrusNotifier i LibrusNotifier.dll)."
chmod +x "$NEW_DIR/LibrusNotifier"
info "Archiwum rozpakowane i sprawdzone."

# ---------- 2. Zamknięcie działającej instancji ----------

# Procesy aplikacji z tego katalogu: plik wykonywalny (apphost) albo "dotnet .../LibrusNotifier.dll".
find_pids() {
    local pid exe cmd
    for pid in $(pgrep -f 'LibrusNotifier' || true); do
        [[ "$pid" == "$$" ]] && continue
        exe="$(readlink -f "/proc/$pid/exe" 2> /dev/null || true)"
        cmd="$(tr '\0' ' ' < "/proc/$pid/cmdline" 2> /dev/null || true)"
        if [[ "$exe" == "$APP_DIR/LibrusNotifier" || "$cmd" == *"$APP_DIR/LibrusNotifier.dll"* ]]; then
            echo "$pid"
        fi
    done
}

PIDS="$(find_pids | tr '\n' ' ')"
PIDS="${PIDS% }"
if [[ -z "$PIDS" ]]; then
    info "Aplikacja nie działa - nie ma czego zamykać."
else
    info "Zamykam działającą aplikację (PID: $PIDS), SIGTERM."
    info "Przerwana instancja kończy bez podsumowania dnia; kolejna odrobi należne zestawienia dzienne."
    # shellcheck disable=SC2086
    kill -TERM $PIDS || die "Nie udało się wysłać SIGTERM (inny użytkownik? spróbuj przez sudo)."
    for ((i = 0; i < STOP_TIMEOUT; i++)); do
        [[ -z "$(find_pids)" ]] && break
        sleep 1
    done
    LEFT="$(find_pids | tr '\n' ' ')"
    LEFT="${LEFT% }"
    if [[ -n "$LEFT" ]]; then
        warn "Aplikacja nie zamknęła się w ${STOP_TIMEOUT} s - wymuszam SIGKILL (PID: $LEFT)."
        # shellcheck disable=SC2086
        kill -KILL $LEFT || true
        sleep 2
    fi
    [[ -z "$(find_pids)" ]] || die "Nie udało się zamknąć aplikacji."
    info "Aplikacja zamknięta."
fi

# ---------- 3. Kopia poprzednich plików programu i podmiana ----------

# Pliki programu (wynik publikacji); konfiguracja, data/ i blokada nie są na tej liście.
mapfile -t OLD_FILES < <(cd "$APP_DIR" && find . -maxdepth 1 -type f \
    \( -name 'LibrusNotifier' -o -name '*.dll' -o -name '*.so' -o -name '*.pdb' -o -name '*.deps.json' -o -name '*.runtimeconfig.json' \) \
    -printf '%f\n')

if ((${#OLD_FILES[@]} > 0)); then
    mkdir -p "$BACKUP_DIR"
    BACKUP="$BACKUP_DIR/program-$(date '+%Y%m%d-%H%M%S').tar.gz"
    tar -czf "$BACKUP" -C "$APP_DIR" "${OLD_FILES[@]}"
    info "Kopia poprzednich plików programu: $BACKUP"
    ls -1t "$BACKUP_DIR"/program-*.tar.gz 2> /dev/null | tail -n +$((KEEP_BACKUPS + 1)) | xargs -r rm -f --
    (cd "$APP_DIR" && rm -f -- "${OLD_FILES[@]}")
else
    info "Brak poprzednich plików programu (pierwsza instalacja)."
fi

cp -a "$NEW_DIR"/. "$APP_DIR"/
chmod +x "$APP_DIR/LibrusNotifier"
if [[ "$(id -u)" == 0 ]]; then
    chown -R --reference="$APP_DIR" "$APP_DIR" || warn "Nie udało się ustawić właściciela plików."
fi
info "Pliki programu podmienione."

# ---------- 4. Uruchomienie w tle ----------

mkdir -p "$APP_DIR/data"
STARTUP_LOG="$APP_DIR/data/uruchomienie.log"
cd "$APP_DIR"
setsid nohup ./LibrusNotifier > "$STARTUP_LOG" 2>&1 < /dev/null &
info "Uruchamiam w tle: $APP_DIR/LibrusNotifier"

NEW_PID=""
for ((i = 0; i < START_TIMEOUT; i++)); do
    sleep 1
    NEW_PID="$(find_pids | head -n 1)"
    if [[ -n "$NEW_PID" ]] && grep -q 'Start LibrusNotifier' "$STARTUP_LOG" 2> /dev/null; then
        break
    fi
    [[ -z "$NEW_PID" && $i -ge 3 ]] && break
done

if [[ -z "$NEW_PID" ]]; then
    warn "Aplikacja nie działa po starcie. Ostatnie linie z $STARTUP_LOG:"
    tail -n 15 "$STARTUP_LOG" >&2 || true
    [[ -n "${BACKUP:-}" ]] && warn "Poprzednią wersję przywrócisz: tar -xzf '$BACKUP' -C '$APP_DIR' && '$APP_DIR/LibrusNotifier' &"
    exit 1
fi

info "Aplikacja działa (PID: $NEW_PID)."
grep -m 1 'Start LibrusNotifier' "$STARTUP_LOG" || true
LATEST_LOG="$(ls -1t "$APP_DIR"/data/logs/librus-*.log 2> /dev/null | head -n 1 || true)"
[[ -n "$LATEST_LOG" ]] && info "Log dzienny: $LATEST_LOG (podgląd: tail -f '$LATEST_LOG')"
info "Gotowe."
