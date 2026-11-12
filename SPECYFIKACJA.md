# Specyfikacja aplikacji LibrusNotifier

## 1. Cel i zakres

LibrusNotifier to aplikacja konsolowa, która okresowo pobiera informacje o uczniu z Librus Synergia, zapisuje je w lokalnej bazie danych i wysyła powiadomienia o nowościach do rodziców i ucznia. Pierwsza wersja wysyła powiadomienia e-mailem.

- Aplikacja korzysta z konta rodzica i konta ucznia (tego samego ucznia). Konta mogą udostępniać różne informacje.
- Informacje widoczne tylko na koncie ucznia również trafiają do rodziców.
- Informacja widoczna na obu kontach nie może wywołać dwóch powiadomień do tej samej grupy odbiorców.

## 2. Platformy i zależności

- **Środowisko:** .NET 10, aplikacja konsolowa.
- **Platformy podstawowe:** Windows x64 (`win-x64`) i Raspberry Pi OS 64-bit na Raspberry Pi 4 (`linux-arm64`). Kod nie powinien blokować uruchomienia na innych architekturach wspieranych przez .NET 10 i zależności; wariant 32-bitowy (`linux-arm`) wymaga osobnego sprawdzenia zależności.
- **Librus:** pakiet NuGet [LibrusApixNet](https://www.nuget.org/packages/LibrusApixNet/) (port `librus-apix`). Lokalne repozytorium `D:\Wojtek\DEV\NET\LibrusApixNet` służy tylko jako materiał pomocniczy; aplikacja używa pakietu NuGet.
- **Baza danych:** SQLite przez Entity Framework Core.
- **Poczta:** MailKit, konto Gmail.
- **Święta:** pakiet NuGet [PublicHoliday](https://www.nuget.org/packages/PublicHoliday/) (obliczenia lokalne).
- **Ferie i wakacje:** [OpenHolidays API](https://www.openholidaysapi.org/en/) z lokalną pamięcią podręczną; województwo ustawia sekcja `[kalendarz]`.

## 3. Uruchamianie

- Katalog aplikacji (konfiguracja i dane) to domyślnie katalog pliku wykonywalnego, niezależnie od katalogu roboczego procesu. Argument `--katalog <ścieżka>` wskazuje inny katalog.
- Baza: `data/librus.db`.
- `--run-once` wykonuje jeden cykl wszystkich sprawdzeń i kończy proces, bez planowania kolejnych. Można łączyć z `--katalog`.
- Zwykłe uruchamianie: automatycznie w środku nocy (cron na Raspberry Pi, Harmonogram zadań na Windows).
- **Jedna instancja:** przy starcie aplikacja zakłada blokadę systemową na pliku w katalogu aplikacji (zwalnianą także po awarii procesu). Jeśli blokada jest zajęta, zapisuje komunikat w logu i od razu kończy działanie.

## 4. Konfiguracja

Jeden plik `librus_config.txt` w katalogu aplikacji, UTF-8, wczytywany raz przy starcie (zmiana wymaga restartu).

### Format

- Puste linie i linie zaczynające się od `#` lub `;` są pomijane. Komentarze na końcu linii z wartością nie są obsługiwane (żeby nie psuć haseł).
- Para klucz–wartość jest dzielona na pierwszym `=`. Wartości są dosłowne: bez zmiennych i sekwencji ucieczki.
- Parametry SMTP Gmaila są wbudowane w integrację, nie w konfigurację.

```ini
[rodzic]
login=LOGIN_RODZICA
haslo=HASLO_RODZICA

[uczen]
login=LOGIN_UCZNIA
haslo=HASLO_UCZNIA

[sesja]
limit_bezczynnosci_minuty=9

[gmail]
email=ADRES_NADAWCY
app_password=HASLO_APLIKACJI

[odbiorcy_rodzice]
rodzic@example.com

[odbiorcy_uczen]
# Lista może pozostać pusta.
uczen@example.com

[odbiorcy_log]
administrator@example.com

[kalendarz]
wojewodztwo=dolnoslaskie
```

### Sekcje

| Sekcja | Zawartość | Wymagana |
| --- | --- | --- |
| `[rodzic]`, `[uczen]` | login i hasło konta | tak |
| `[gmail]` | adres nadawcy, hasło aplikacji (app password) | tak |
| `[kalendarz]` | województwo | tak |
| `[odbiorcy_rodzice]` | adresy rodziców | tak, min. 1 adres |
| `[odbiorcy_log]` | adresy administratorów (log działania i komunikaty administracyjne) | tak, min. 1 adres |
| `[odbiorcy_uczen]` | adresy ucznia | nagłówek tak, lista może być pusta |
| `[sesja]` | `limit_bezczynnosci_minuty` | nie; domyślnie 9 |

### Reguły

- **Listy adresów:** jeden adres na wiersz, bez kluczy; wiersz to cały adres (także z `=`), spacje na brzegach są usuwane, duplikaty (bez rozróżniania wielkości liter) są usuwane.
- **`[sesja]`:** jeśli występuje, `limit_bezczynnosci_minuty` jest wymagany i musi być dodatnią liczbą minut (ułamki z kropką) mieszczącą się w zakresie `TimeSpan`.
- Nieznane sekcje, nieznane klucze i powtórzone klucze są błędami konfiguracji.
- **Błędy:** brak pliku lub wymaganej sekcji, niepoprawny adres lub wartość kończy program przed pobieraniem danych. Opis błędu trafia na konsolę i do lokalnego logu, wskazuje plik oraz sekcję lub numer wiersza i nie ujawnia haseł.
- Program nie tworzy plików z przykładowymi hasłami. Konfiguracja, baza, cache kalendarza i logi nie trafiają do repozytorium; wzorzec bez danych dostępowych jest w `examples/librus_config.txt`.

## 5. Sesje Librusa

W obrębie jednego cyklu aplikacja używa ponownie tokenu, zamiast logować się przed każdym żądaniem. Każdy cykl zaczyna się jednak od nowego logowania (patrz ostatni punkt poniżej).

- Konta rodzica i ucznia mają osobne tokeny, trzymane wyłącznie w pamięci (nie w bazie).
- Sesja jest używana, dopóki Librus ją akceptuje. Brak limitu czasu od logowania; obowiązuje limit bezczynności (domyślnie 9 minut, `[sesja]`). To limit przyjęty przez aplikację, nie potwierdzony czas ważności sesji po stronie serwera.
- Czas ostatniej aktywności (osobno dla każdego konta) ustawia się po poprawnym logowaniu i po każdej odpowiedzi HTTP 2xx w zalogowanej sesji, także wewnątrz operacji wielożądaniowych. Błędy HTTP i sieci go nie odnawiają.
- Przed operacją wymagającą sesji sprawdzany jest czas od ostatniej aktywności. Dopiero przerwa dłuższa niż limit (dokładnie na granicy sesja jeszcze działa) usuwa lokalny token i ciasteczka oraz powoduje ponowne logowanie tego konta.
- Sesja nie jest odświeżana w tle.
- Każdy ping zaczyna się od nowego zalogowania obu kont niezależnie od limitu bezczynności (patrz sekcja 6); sesja jest ponownie używana tylko w obrębie jednego cyklu.

## 6. Dane i wykrywanie nowości

- Obserwowane dane (z obu kont): oceny (także opisowe), wiadomości odebrane, ogłoszenia, terminarz, zadania domowe, frekwencja, plan lekcji (zmiany), zrealizowane lekcje.
- Nowości wykrywa aplikacja, porównując zawartość **list** z zapisaną w SQLite. Mechanizmu `Notifications` z biblioteki nie używamy do wykrywania, tylko odczytujemy panel powiadomień (liczniki).
- Cel: jak najmniej żądań do Librusa. Aplikacja nie iteruje po poszczególnych elementach (wiadomościach, terminach itd.) w poszukiwaniu nowości.
- **Odstęp między żądaniami:** między dwoma kolejnymi żądaniami HTTP do Librusa (oba konta łącznie, także kolejne kroki przekierowań) mija co najmniej losowo 5–11 s, liczone od zakończenia poprzedniego żądania. Wyjątek stanowi samo logowanie (handshake OAuth, także kroki przekierowań): jego pierwsze żądanie czeka zwykły odstęp od poprzedniego ruchu, a każde kolejne tylko losowo 0–1 s (kody autoryzacyjne OAuth żyją krótko). Odczyt panelu po zalogowaniu znów podlega zwykłemu odstępowi.
- **Diagnostyka w logu:** linia każdego żądania wymienia nazwy (nigdy wartości) ciasteczek ustawionych przez serwer („, ustawia: …”). Dla odpowiedzi z kodem 400 i wyższym (poza oczekiwanym 401 na korzeniu API) dochodzi linia „szczegóły błędu” z nazwami wysłanych ciasteczek, typem i początkiem treści (do 300 znaków).

### Kolejność sprawdzeń

1. **Ping (każdy cykl, każde konto)** zaczyna się od nowego zalogowania (poprzedni token jest odrzucany), bo liczniki panelu są przypisane do tokenu i opisują nowości od poprzedniego logowania. Pierwszym żądaniem po każdym zalogowaniu, także wymuszonym w trakcie cyklu (np. po wygaśnięciu sesji), jest odczyt panelu powiadomień (panel startowy konta: `/rodzic/index` dla rodzica i `/uczen/index` dla ucznia – tak wskazuje Portal Rodzina, a konto rodzica dostaje na `/uczen/index` kod 403 „Brak dostępu”; liczniki: oceny, frekwencja, wiadomości, ogłoszenia, zadania domowe, terminarz).
2. **Wzbudzenie „jest coś nowego”:** niezerowy licznik któregokolwiek modułu, niezależnie od tego, czy odczyt panelu wynikał z harmonogramu. Aplikacja pobiera wtedy listy modułów z niezerowym licznikiem i porównuje je z bazą.
3. **Przegląd list (zabezpieczenie):** w pierwszym pingu po starcie aplikacji (także po restarcie w ciągu dnia, bo liczniki panelu nie obejmują zmian z czasu, gdy aplikacja nie działała), a potem co 3. ping, ale nie częściej niż raz na godzinę, aplikacja pobiera listy wszystkich modułów, także spoza panelu (plan lekcji, zrealizowane lekcje). Licznik pingów i czas ostatniego przeglądu są trzymane w pamięci procesu (po restarcie liczenie zaczyna się od zera). Wychwytuje to nowości, które liczniki mogły pominąć (np. po zalogowaniu rodzica w przeglądarce).
4. **Szczegóły** pojedynczego elementu są pobierane dopiero po wykryciu, że jest nowy, wyłącznie po to, by zbudować treść maila, i tylko dla typów, w których szczegóły zawierają więcej niż lista (np. treść wiadomości). Jeśli lista wystarcza, szczegółów się nie pobiera. O tym, czy element jest nowy, decyduje zawsze lista.

### Zakres list

- Listy stronicowane (wiadomości): tylko pierwsza strona.
- Terminarz: bieżący miesiąc i jeden miesiąc naprzód.
- Pozostałe: oceny (oba semestry, jedna strona), frekwencja, ogłoszenia, zadania domowe (ostatnie 7 dni, jak w bibliotece), plan lekcji (bieżący tydzień), zrealizowane lekcje (ostatnie 7 dni, pierwsza strona).

### Semestr i rok szkolny

- Klucz elementu nie zawiera numeru semestru, więc zmiana semestru nie powoduje ponownych powiadomień. Nowe oceny semestralne i roczne są zwykłymi nowymi elementami.
- Zmiana roku szkolnego nie wymaga osobnej obsługi: ponieważ zniknięcia elementów są ignorowane, czyszczenie danych przez Librus nie wywołuje powiadomień, a dane nowego roku są zwykłymi nowymi elementami. Stare wpisy zostają w bazie.

### Baza i deduplikacja

- Każdy element ma klucz wyliczony z treści (typ + kluczowe pola, np. przedmiot, data, treść, w postaci skrótu), niezależny od identyfikatorów konta. Ten sam element z obu kont ma więc jeden wpis; zapisywane jest, z których kont jest widoczny.
- Dla każdego elementu i grupy odbiorców (rodzice, uczeń) zapisywane jest, czy powiadomienie zostało wysłane, żeby nie wysłać go drugi raz.
- **Schemat:** tabela `Elements` (typ, klucz, dane w JSON, konta widoczności, daty pierwszego i ostatniego wykrycia) oraz tabela `Notifications` (element, grupa odbiorców, status wysyłki).
- **Pierwsze uruchomienie** (pusta baza) robi pełny przegląd list i tylko zapisuje dotychczasowe dane, bez powiadomień.
- **`--run-once`** wykonuje ping i pełny przegląd list (licznik pingów w pamięci nie ma historii); powiadomienia wysyłane są normalnie.
- **Edycje i usunięcia nie są śledzone.** Element o nowym kluczu jest nowością, także gdy w Librusie jest to edycja istniejącego (np. zmieniona data wydarzenia): wysyłane jest powiadomienie „nowy”, a w bazie zostaje stary wpis. Zniknięcie elementu z listy jest ignorowane.

## 7. Powiadomienia e-mail

- **Adresaci:** rodzice dostają sumę danych z obu kont; uczeń tylko elementy widoczne na jego koncie.
- **Format:** osobny mail na każdy nowy element, z tematem opisującym element, np. nową ocenę z matematyki.
- **Treść:** mail jest w HTML-u (multipart/alternative z wersją tekstową jako zapasową). Pola elementu (`etykieta: wartość`) są tabelą HTML, szczegóły (np. treść wiadomości) osobną tabelą; wartości z Librusa są kodowane, nigdy nie trafiają do HTML-a jako znaczniki. Maile do administratorów (podsumowanie dnia, błędy) pozostają czystym tekstem.
- **Daty względem dzisiaj:** każda data `rrrr-mm-dd` (z godziną lub bez) w temacie i treści powiadomienia dostaje w nawiasie dzień tygodnia i odległość od dnia wysyłki, np. `2026-11-13 (piątek, jutro)`: `dziś`, `jutro`, `pojutrze`, `za N dni`, a dla przeszłości `wczoraj`, `przedwczoraj`, `N dni temu`. W wersji HTML data wraz z opisem jest pogrubiona, gdy dotyczy dziś, jutra albo – gdy dziś jest dniem wolnym (weekend, święto, ferie) – pierwszego dnia szkolnego po wolnym; tekst i temat nie mają pogrubień. Bez opisu zostają daty techniczne: „Wykryto” i pole „Data dodania”; w HTML-u „Data dodania” jest dodatkowo szara, żeby nie mylić jej z datą, której dotyczy powiadomienie.
- **Limit:** w jednym cyklu wysyłanych jest najwyżej 20 maili (łącznie dla wszystkich grup odbiorców), by nie przekroczyć limitów Gmaila. Pozostałe elementy zostają niewysłane i trafiają w kolejnych pingach, od najstarszych.
- **Błędy wysyłki:** znacznik „wysłano” jest ustawiany dopiero po udanej wysyłce. Niewysłane elementy są ponawiane w każdym kolejnym pingu, także gdy liczniki są zerowe. Administratorzy (`[odbiorcy_log]`) są informowani o błędzie, o ile mail da się wysłać.

## 8. Harmonogram odpytywania

Harmonogram jest stały, zakodowany w aplikacji i nie podlega konfiguracji.

- **Czas** w całej aplikacji (harmonogram, typ dnia, zmiana dnia) liczony jest w stałej strefie `Europe/Warsaw`, niezależnie od ustawień systemu.
- **Typ dnia** wg priorytetu: wakacje/ferie (OpenHolidays, ferie zimowe i letnie to jeden typ) > dzień wolny (weekend, święto z PublicHoliday) > dzień szkolny.
- Dni wolne ustalone przez szkołę poza kalendarzem państwowym i ferii (np. dni dyrektorskie) nie są znane aplikacji i są traktowane jak dni szkolne.
- **Dzień „przed szkołą”:** dzień wolny lub wakacyjny, po którym następny dzień jest szkolny.
- **Terminy** (cykl sprawdzeń powtarza się w oknie co interwał z losowym odchyleniem; poza podanymi oknami aplikacja nie odpytuje Librusa):

| Typ dnia | Okno | Interwał |
| --- | --- | --- |
| Szkolny | 6:00–16:00 | 15 min ± losowo 4 minuty |
| Szkolny, wieczór | 16:00–22:00 | 25 min ± losowo 5 min |
| Szkolny, noc (0:00–6:00 i 22:00–24:00) | cała noc | 1 h ± losowo 10 min |
| Wolny | 8:00–22:00 | 3 h ± losowo 20 min |
| Wolny „przed szkołą” | 8:00–12:00 | 3 h ± losowo 20 min |
| | 12:00–20:00 | 1 h ± losowo 10 min |
| | 20:00–23:00 | 25 min ± losowo 5 min |
| Wakacje/ferie | cała doba | 6 h + losowo 0–60 min |
| Wakacje/ferie „przed szkołą” | jak wolny „przed szkołą” | |

- **Odchylenie:** „+” oznacza wyłącznie dodatnie, „±” symetryczne odchylenie od interwału.
- **Ferie i wakacje** pochodzą z lokalnego cache na dysku (w `data`), odświeżanego z OpenHolidays API co dobę. Gdy API jest niedostępne, używany jest istniejący cache; gdy go brak, ferie i wakacje są pomijane (liczą się tylko weekendy i święta), a aplikacja zapisuje ostrzeżenie w logu.

## 9. Cykl dobowy

- Przy starcie aplikacja zapamiętuje w pamięci procesu (nie w bazie) bieżący dzień kalendarzowy i obsługuje zadania tylko do końca tego dnia.
- Przed każdym żądaniem do Librusa i po jego zakończeniu sprawdza, czy dzień się zmienił; jeśli tak, kończy działanie.
- Jeśli start wypada w oknie harmonogramu, pierwszy ping następuje od razu, kolejne według interwału. Poza oknem aplikacja czeka do początku następnego okna tego dnia.
- Gdy do końca dnia nie ma już żadnego okna, aplikacja wysyła podsumowanie dnia i kończy działanie od razu (nie czeka do północy).
- `--run-once` ignoruje okna harmonogramu i kończy działanie po jednym cyklu.

## 10. Logowanie

- **Błędy Librusa** (logowanie, sieć, odpowiedź serwera): bieżące sprawdzenie danego konta jest przerywane bez ponowień w tym samym cyklu, a kolejna próba następuje w następnym pingu. Konta rodzica i ucznia są niezależne: błąd jednego nie wstrzymuje drugiego.
- **Żądania do Librusa:** każde żądanie HTTP jest zapisywane w logu lokalnym (czas, konto rodzica/ucznia, metoda, ścieżka bez ciasteczek, tokenów i danych logowania, kod odpowiedzi, czas trwania), żeby można było wykryć nadmierne odpytywanie serwisu. Podsumowanie dnia dla administratorów zawiera liczbę żądań, łącznie i na konto.
- **Log lokalny:** wypis na konsolę i plik dzienny w `data/logs` (poziomy info i błąd, bez haseł i tokenów); pliki starsze niż 30 dni są usuwane.
- **Log do administratorów** (`[odbiorcy_log]`): podsumowanie dnia wysyłane przy zakończeniu działania (zmiana dnia lub `--run-once`) oraz natychmiastowy mail przy błędzie krytycznym (np. błąd logowania, brak dostępu do Librusa, nieudana wysyłka). Ten sam błąd w ciągu dnia jest zgłaszany natychmiast tylko raz.

## 11. Do ustalenia przy implementacji

- Pola kluczowe i dane JSON dla poszczególnych typów elementów.
- Szablony i tematy maili dla poszczególnych typów.
