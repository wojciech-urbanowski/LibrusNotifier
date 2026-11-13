#!/usr/bin/env bash
# Wypisuje aktualne wersje stabilne przeglądarek z oficjalnych źródeł, żeby odświeżyć listy wersji w
# src/LibrusNotifier/Librus/BrowserCatalog.cs (losowany profil przeglądarki musi wyglądać na aktualny).
#
# Źródła:
#   Firefox  product-details.mozilla.org (najnowsza wersja i ESR)
#   Chrome   versionhistory.googleapis.com (najwyższa wersja major, którą dostaje większość użytkowników)
#   Edge     edgeupdates.microsoft.com (kontrola; wersja major Edge = wersja major Chromium)
#   Safari   developer.apple.com, notatki wydań (do ręcznego wyboru wersji dostępnych na macOS)
#
# Użycie: odswiez-wersje-przegladarek.sh
# Wymaga: curl, grep, sed, awk, paste. Nie zmienia żadnych plików.

set -euo pipefail

info() { printf '%s\n' "$*"; }
warn() { printf 'UWAGA: %s\n' "$*" >&2; }
die()  { printf 'BŁĄD: %s\n' "$*" >&2; exit 1; }

fetch() { curl -fsS -m 30 "$1"; }
major_of() { printf '%s' "$1" | grep -oE '^[0-9]+'; }

# --- Firefox ---
firefox_json="$(fetch https://product-details.mozilla.org/1.0/firefox_versions.json)" || die "Nie udało się pobrać wersji Firefoxa."
firefox_latest="$(major_of "$(printf '%s' "$firefox_json" | grep -oE '"LATEST_FIREFOX_VERSION": *"[0-9.]+"' | grep -oE '[0-9][0-9.]*')")"
firefox_esr="$(major_of "$(printf '%s' "$firefox_json" | grep -oE '"FIREFOX_ESR": *"[0-9.]+' | grep -oE '[0-9][0-9.]*')")"
[[ -n "$firefox_latest" && -n "$firefox_esr" ]] || die "Nie udało się odczytać wersji Firefoxa."

# --- Chrome: najwyższa wersja major, której łączny udział w wdrożeniu wynosi co najmniej 0,5 ---
chrome_major="$(fetch 'https://versionhistory.googleapis.com/v1/chrome/platforms/win/channels/stable/versions/all/releases?filter=endtime=none&pageSize=50' |
    grep -E '"(fraction|version)"' | paste - - |
    sed -E 's/.*"fraction": *([0-9.]+),[[:space:]]*"version": *"([0-9]+)\..*/\2 \1/' |
    awk '{ sum[$1] += $2 } END { for (m in sum) if (sum[m] >= 0.5) print m }' | sort -n | tail -1)" || true
[[ -n "$chrome_major" ]] || die "Nie udało się ustalić wersji stabilnej Chrome."

# --- Edge (kontrola) ---
edge_major="$(fetch https://edgeupdates.microsoft.com/api/products | sed 's/{"Product":/\n{"Product":/g' | grep '^{"Product":"Stable"' |
    grep -oE '"ProductVersion":"[0-9]+' | grep -oE '[0-9]+$' | sort -n | tail -1)" || true

# Lista: najnowsza z wagą 4, kolejne starsze z wagami 3, 2, 1 (ile pozycji - tyle wag od 4 w dół).
weighted() { # $1 = najwyższa wersja, $2 = liczba pozycji
    local out="" i
    for ((i = 0; i < $2; i++)); do out+="${out:+, }new(\"$(($1 - i))\", $((4 - i)))"; done
    printf '%s' "$out"
}

firefox_list="$(weighted "$firefox_latest" 3)"
if (( firefox_esr < firefox_latest - 2 )); then firefox_list+=", new(\"$firefox_esr\", 1)"; fi

info "Stan na $(date '+%Y-%m-%d'). Wpisz w BrowserCatalog.cs (lista Families) i ustaw VerifiedOn:"
info
info "  Chrome : [$(weighted "$chrome_major" 4)]"
info "  Edge   : [$(weighted "$chrome_major" 3)]"
info "  Firefox: [$firefox_list]   (ESR: $firefox_esr)"
info "  Safari : wybierz z listy poniżej wersje z macOS (najnowsza z wagą 4)"
info
info "  VerifiedOn = $(date '+new(%Y, %-m, %-d)')"
info
if [[ -n "$edge_major" && "$edge_major" != "$chrome_major" ]]; then
    warn "Wersja stabilna Edge ($edge_major) różni się od Chrome ($chrome_major); to normalne w dniach tuż po wydaniu - dobierz Edge do rzeczywistej wersji ($edge_major)."
elif [[ -z "$edge_major" ]]; then
    warn "Nie udało się odczytać wersji Edge; przyjmij wersję Chrome ($chrome_major)."
fi

# --- Safari: notatki wydań Apple (x.0 nazywa się safari-x-release-notes, kolejne safari-x_y-release-notes) ---
info "Safari (wydania z notatek Apple; 'macOS' = dostępne na macOS):"
notes_url="https://developer.apple.com/documentation/safari-release-notes"
safari_line() { # $1 = wersja, $2 = adres
    local text released overview platform="bez macOS"
    text="$(curl -fsS -m 20 "$2" 2>/dev/null)" || return 1
    released="$(printf '%s\n' "$text" | grep -m1 '^Released' || true)"
    overview="$(printf '%s\n' "$text" | grep -m1 -E '^Safari [0-9.]+( beta)? is available for' || true)"
    [[ "$overview" == *macOS* ]] && platform="macOS"
    [[ "$released" == *beta* ]] && platform="BETA - pomiń"
    released="$(printf '%s' "$released" | sed -E 's/^Released ([^—]*)—.*/\1/')"
    printf '  Safari %-5s %s (%s)\n' "$1" "${released% }" "$platform"
}
major=26
while safari_line "$major.0" "$notes_url/safari-$major-release-notes.md"; do
    misses=0
    for minor in 1 2 3 4 5 6 7 8 9; do
        if safari_line "$major.$minor" "$notes_url/safari-${major}_${minor}-release-notes.md"; then misses=0
        elif (( ++misses >= 2 )); then break
        fi
    done
    major=$((major + 1))
done
