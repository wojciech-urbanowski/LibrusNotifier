# LibrusNotifier

Aplikacja konsolowa (.NET 10), która okresowo sprawdza Librus Synergia z konta rodzica i ucznia, zapisuje dane w SQLite
i wysyła e-mailem (Gmail) powiadomienia o nowościach. Wymagania opisuje [SPECYFIKACJA.md](SPECYFIKACJA.md).

## Budowanie i testy

```sh
dotnet build
dotnet test
```

Publikacja (wymaga zainstalowanego środowiska .NET 10 na maszynie docelowej; z `--self-contained true` nie jest ono potrzebne):

```sh
dotnet publish src/LibrusNotifier -c Release -r linux-arm64 --self-contained false -o publish/linux-arm64
dotnet publish src/LibrusNotifier -c Release -r win-x64 --self-contained false -o publish/win-x64
```

## Instalacja

1. Skopiuj wynik publikacji do katalogu aplikacji, np. `/opt/librus-notifier` lub `C:\LibrusNotifier`.
2. Skopiuj [examples/librus_config.txt](examples/librus_config.txt) do katalogu aplikacji i uzupełnij dane.
   Hasło Gmail to [hasło aplikacji](https://myaccount.google.com/apppasswords) (wymaga weryfikacji dwuetapowej).
3. Sprawdź działanie jednym cyklem: `./LibrusNotifier --run-once`. Pierwsze uruchomienie (pusta baza) tylko zapisuje
   dotychczasowe dane, bez powiadomień.

Konfiguracja i dane są w katalogu pliku wykonywalnego (niezależnie od katalogu roboczego); inny katalog wskazuje
`--katalog <ścieżka>`. Baza: `data/librus.db`, logi dzienne: `data/logs`, cache ferii: `data/ferie_cache.json`.

### Raspberry Pi (cron)

Uruchamianie codziennie tuż po północy (`crontab -e`):

```
5 0 * * * /opt/librus-notifier/LibrusNotifier > /dev/null 2>&1
```

Proces działa do końca dnia według wbudowanego harmonogramu i sam się kończy (po ostatnim oknie lub o północy).
Druga instancja wykrywa blokadę `librus_notifier.lock` i od razu kończy działanie.

### Windows (Harmonogram zadań)

```powershell
schtasks /Create /TN "LibrusNotifier" /SC DAILY /ST 00:05 /TR "C:\LibrusNotifier\LibrusNotifier.exe"
```

## Działanie w skrócie

- Każdy ping loguje oba konta od nowa i najpierw czyta panel powiadomień; pobierane są listy modułów z niezerowym
  licznikiem. W pierwszym pingu po starcie, a potem co 3. ping (nie częściej niż raz na godzinę), pobierane są
  wszystkie listy, także planu lekcji i zrealizowanych lekcji.
- Nowości wykrywa porównanie list z bazą (klucz to skrót treści, wspólny dla obu kont). Rodzice dostają nowości z obu
  kont, uczeń – tylko widoczne na jego koncie; każdy element trafia do każdej grupy najwyżej raz.
- Między dwoma żądaniami HTTP do Librusa (oba konta łącznie, także kroki przekierowań) mija co najmniej losowo
  5–11 s od zakończenia poprzedniego. Wyjątek to samo logowanie: jego kroki (handshake OAuth, także przekierowania)
  następują po sobie w odstępie losowo 0–1 s, żeby nie wygasły kody autoryzacyjne; pierwszy krok logowania i odczyt
  panelu po nim czekają zwykły odstęp. Ping z przeglądem wszystkich list trwa przez to kilka minut; oczekiwanie widać
  w logu jako „(odstęp: czekano … s)”.
- Log wymienia nazwy (bez wartości) ciasteczek ustawianych przez serwer, a przy odpowiedziach z błędem HTTP także
  nazwy wysłanych ciasteczek i początek treści odpowiedzi – pomaga to diagnozować odrzucone logowania.
- Najwyżej 20 maili na cykl; niewysłane czekają na kolejne pingi (od najstarszych).
- Administratorzy (`[odbiorcy_log]`) dostają natychmiast pierwsze wystąpienie każdego błędu krytycznego w danym dniu
  oraz podsumowanie dnia (liczba pingów, żądań HTTP łącznie i na konto, nowości, maili, błędów).

### Pola kluczowe elementów

Klucz to SHA-256 z typu i znormalizowanych (białe znaki) pól poniżej. Nie zawiera identyfikatorów Librusa (różnych dla
kont) ani numeru semestru. Elementy o identycznej treści na jednej liście (np. dwa plusy z tego samego dnia) dostają
kolejne numery wystąpienia.

| Typ (`Elements.Type`) | Moduł | Pola kluczowe |
| --- | --- | --- |
| `ocena` | oceny | przedmiot, ocena, data, kategoria, nauczyciel, waga, liczona do średniej |
| `ocena_opisowa` | oceny | przedmiot, ocena, data, nauczyciel, opis |
| `frekwencja` | frekwencja | data, nr lekcji, symbol, rodzaj, przedmiot, nauczyciel |
| `wiadomosc` | wiadomości | nadawca, temat, data |
| `ogloszenie` | ogłoszenia | tytuł, autor, data |
| `terminarz` | terminarz | data, tytuł, przedmiot, nr lekcji, godzina, opis, nauczyciel |
| `zadanie_domowe` | zadania domowe | przedmiot, nauczyciel, temat, kategoria, data udostępnienia, termin |
| `zmiana_planu` | plan lekcji | data, nr lekcji, przedmiot, nauczyciel i sala, rodzaj zmiany i jej szczegóły |
| `zrealizowana_lekcja` | zrealizowane lekcje | data, nr lekcji, przedmiot, nauczyciel, temat |

`Elements.DataJson` zawiera wszystkie pola z listy (etykieta → wartość), `DetailsJson` – szczegóły do maila.
Mail ma temat z prefiksem `[Librus]` opisujący element (np. `[Librus] Nowa ocena: Matematyka – 5`), a w treści
wszystkie pola, szczegóły i informację, na którym koncie element jest widoczny.

### Szczegóły implementacji

- Pierwsze pobranie listy danego modułu z danego konta zapisuje dane bez powiadomień (tabela `Baselines`). Obejmuje to
  pustą bazę, ale też konto, którego logowanie przy pierwszym uruchomieniu się nie udało – jego zaległe dane nie
  wywołają lawiny powiadomień.
- Moduł, którego lista nie została pobrana z powodu błędu, jest pobierany w następnym pingu, nawet przy zerowych
  licznikach (liczniki opisują nowości od poprzedniego logowania, więc inaczej czekałby na przegląd list).
- Z frekwencji pomijane są obecności; z planu lekcji obserwowane są tylko lekcje ze zmianą (zastępstwo, odwołanie itp.).
- Szczegóły pobierane są tylko dla nowych wiadomości (treść) i zadań domowych. Odczyt treści wiadomości w Librusie
  oznacza ją jako przeczytaną na koncie, z którego została pobrana.
- Żądania do Librusa (także kroki przekierowań) są zapisywane w logu: konto, metoda, host i ścieżka bez parametrów,
  kod odpowiedzi i czas trwania.

## Kody wyjścia

| Kod | Znaczenie |
| --- | --- |
| 0 | koniec pracy (brak dalszych okien, zmiana dnia, `--run-once`) |
| 1 | błąd krytyczny |
| 2 | błąd argumentów lub konfiguracji |
| 3 | inna instancja już działa |
| 130 | przerwano (Ctrl+C / SIGTERM) |

## Licencja

Projekt jest udostępniony na licencji [GPL-3.0-only](LICENSE). Taką licencję ma biblioteka
[LibrusApixNet](https://www.nuget.org/packages/LibrusApixNet/), z której korzysta aplikacja, więc całość musi być
na GPL-3.0. Pozostałe zależności (MailKit, Entity Framework Core, PublicHoliday – MIT; SQLitePCLRaw – Apache-2.0) są
z nią zgodne.
