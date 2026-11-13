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
`--katalog <ścieżka>`. Baza: `data/librus.db`, kopie zapasowe bazy: `data/backups`, logi dzienne: `data/logs`,
cache ferii: `data/ferie_cache.json`.

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
- Zmiany terminu i usunięcia (terminarz, plan lekcji, zadania domowe): wpis, który zniknął z list kont, a ma dzisiejszą lub
  przyszłą datę, jest zgłaszany. Gdy w tym samym cyklu pojawił się wpis o tej samej treści i innej dacie, przychodzi jeden
  mail „Zmiana terminu” zamiast „nowy wpis”; w pozostałych przypadkach „Usunięto …” (z podpowiedzią o możliwym przeniesieniu,
  gdy da się ją ustalić). Szczegóły w sekcji 6 specyfikacji.
- Zestawienia dzienne (sekcja 8 specyfikacji): o 6:30 w dni nauki „Odprawa poranna” z listą „Dzisiaj” i kolejnych wydarzeń
  (temat tylko o dzisiejszym dniu, bez listy ocen), o 15:00 lista „Jutro”
  (albo pierwszego dnia nauki po wolnym) i kolejnych; w wakacje i ferie popołudniowe tylko wtedy, gdy są przyszłe
  wydarzenia. Powstają wyłącznie z danych w bazie, bez żądań do Librusa, i zawsze po pingu: ping w toku jest dokańczany
  (oba konta), a zestawienie zaległe po starcie aplikacji (także w nowej bazie) wychodzi po pierwszym pingu; po 15:00,
  gdy dziś nic jeszcze nie wysłano, wychodzi tylko popołudniowe. Każde wydarzenie to osobna karta na całą szerokość
  (bez kolumn, żeby szczegóły były czytelne na telefonie) z paskiem i plakietką rodzaju w pastelowym kolorze
  (sprawdzian – czerwony mocniejszy, kartkówka – czerwony lżejszy, zadanie domowe – fioletowy, zastępstwo – zielony,
  odwołane zajęcia – pomarańczowy, dzień wolny – niebieski, wydarzenie – żółty, inne – szary). Na końcu zestawienia
  popołudniowego jest lista ocen z bieżącego semestru: wiersz na przedmiot (alfabetycznie), w nim nazwa i oceny (same wartości,
  od najstarszej do najnowszej) w kolorach jak w mailu o nowej ocenie; oceny zawijają się do kolejnych linii, więc lista nie
  jest szersza niż ekran telefonu; w wakacje (lipiec, sierpień) listy nie ma.
- Kopia zapasowa bazy: przy każdym starcie (przed migracją) powstaje `data/backups/librus-RRRR-MM-DD_GGmm.db` (czas
  z dokładnością do minuty; jeśli plik o tej nazwie już jest, kopii się nie powtarza). Kopie starsze niż miesiąc są kasowane.
  Nieudana kopia wstrzymuje kasowanie starych.
- Vacuum bazy: na zakończenie działania w dni roku podzielne przez 10 (10., 20., 30. … dzień roku); wynik (rozmiar przed
  i po, czas) jest na końcu podsumowania dnia dla administratorów.
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
Mail ma temat z prefiksem `[Librus]` opisujący element (np. `[Librus] Nowa ocena: Matematyka - 5`), a w treści
wszystkie pola, szczegóły i informację, na którym koncie element jest widoczny. Układ jest dla telefonu, bez kolumn
o stałej szerokości: zmiana w planie lekcji, wpis w terminarzu i wiadomość są kartą z kolorowym paskiem (w zmianie planu
zmienione pola jako „stare -> nowe”, treść wiadomości pod kartą na całą szerokość), pozostałe typy to linie
„etykieta: wartość”.

Mail o nowej ocenie ma dodatkowo:

- pastelowe tło pola z oceną (6 – intensywny zielony, 5 – zielony, 4 – słaby zielony, 3 – żółty, 2 – pomarańczowy,
  1 – czerwony; sam „+” – miętowy odcień zieleni, sam „-” – łososiowy, pomiędzy pomarańczowym a czerwonym; „4+” i „3-”
  mają kolor swojej cyfry; inne oznaczenia, np. „nb”, „np”, „zw”, i oceny opisowe są szare; waga ma szarą czcionkę, żeby
  nie myliła się z oceną),
- raport z listą ocen z tego przedmiotu i z tego samego semestru, od najnowszej do najstarszej (ocena z powiadomienia jest
  oznaczona „(nowa)”, a nagłówek podaje numer semestru). Po starcie nowego semestru lub roku szkolnego raport zaczyna się
  od nowa: nie zawiera ocen z poprzedniego semestru ani z poprzedniego roku. Raport powstaje z bazy; oceny, które zniknęły
  z listy w Librusie (np. poprawione), są pomijane, a uczeń widzi w nim tylko oceny ze swojego konta.

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
