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
- `--run-once` wykonuje jeden cykl wszystkich sprawdzeń oraz należne zestawienia dzienne (sekcja 8) i kończy proces, bez planowania kolejnych. Można łączyć z `--katalog`.
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
- Lista ocen w Librusie obejmuje oba semestry, a oceny z poprzednich lat zostają w bazie, więc **raport ocen z przedmiotu** (sekcja 7) obejmuje tylko oceny z okresu nowej oceny: tego samego semestru (pole „Semestr” z kolumny w Librusie, nie data wpisu) i tego samego roku szkolnego (z daty oceny; rok zaczyna się 1 września). Pierwsza ocena drugiego semestru ma więc raport tylko z tego semestru, a pierwsza ocena nowego roku nie dostaje ocen z poprzedniego, nawet gdyby jeszcze nie zniknęły z listy. Okres, którego nie da się odczytać (brak lub niepoprawny semestr, nieczytelna data), nie wyklucza żadnej oceny.
- Zmiana roku szkolnego nie wymaga osobnej obsługi: ponieważ wpisy z datą w przeszłości są ignorowane przy wykrywaniu zniknięć (patrz „Zmiany terminu i usunięcia”), czyszczenie danych przez Librus nie wywołuje powiadomień, a dane nowego roku są zwykłymi nowymi elementami. Stare wpisy zostają w bazie.

### Baza i deduplikacja

- Każdy element ma klucz wyliczony z treści (typ + kluczowe pola, np. przedmiot, data, treść, w postaci skrótu), niezależny od identyfikatorów konta. Ten sam element z obu kont ma więc jeden wpis; zapisywane jest, z których kont jest widoczny.
- Dla każdego elementu i grupy odbiorców (rodzice, uczeń) zapisywane jest, czy powiadomienie zostało wysłane, żeby nie wysłać go drugi raz.
- **Schemat:** tabela `Elements` (typ, klucz, dane w JSON, konta widoczności, daty pierwszego i ostatniego wykrycia), tabela `Notifications` (element, grupa odbiorców, rodzaj: nowy / zmiana terminu / usunięcie, powiązany element, status wysyłki), tabela `Baselines` (konto, moduł, czas pierwszego i ostatniego udanego pobrania listy) oraz tabela `DigestLogs` (wysłane zestawienia dzienne, patrz sekcja 8).
- **Ostatnie pobranie listy:** każde udane pobranie listy modułu z konta zapisuje ten sam znacznik czasu w `Baselines.LastFetchUtc` i w `LastSeenUtc` elementów widzianych na liście. Element, którego `LastSeenUtc` jest starszy niż ostatnie pobranie listy z kont, na których był widoczny, zniknął z listy. Na tej podstawie działa wykrywanie zmian terminu i usunięć (niżej), a zestawienia dzienne pomijają takie elementy. W `Elements.GoneHandledUtc` zapisywane jest, że zniknięcie już obsłużono; ponowne pojawienie się elementu na liście czyści znacznik.
- **Kopie zapasowe:** przy starcie aplikacji (po założeniu blokady instancji, przed migracją schematu, także w `--run-once`) baza jest kopiowana do `data/backups/librus-RRRR-MM-DD_GGmm.db`; nazwa zawiera czas startu (Europe/Warsaw) z dokładnością do minuty. Jeśli plik o takiej nazwie już istnieje, kopii się nie powtarza. Kopia powstaje przez interfejs kopii zapasowych SQLite (spójna) pod nazwą tymczasową `….db.tmp`, którą dostaje docelową dopiero po zakończeniu. Po kopii kasowane są kopie starsze niż miesiąc (wg czasu w nazwie, z dokładnością do minuty; pliki o innych nazwach nie są ruszane). Brak pliku bazy (pierwsze uruchomienie) pomija kopię. Błąd kopii jest zapisywany w logu jako błąd (trafia do podsumowania dnia), aplikacja działa dalej, a stare kopie zostają, żeby nie zostać bez żadnej.
- **Vacuum:** przy zakończeniu działania (zmiana dnia, brak dalszych okien, `--run-once`, błąd krytyczny; nie po przerwaniu Ctrl+C / SIGTERM) w dni roku podzielne przez 10 (10., 20., 30. … dzień roku; liczony jest dzień, w którym aplikację uruchomiono) baza jest kompaktowana poleceniem `VACUUM`, już po zamknięciu wszystkich połączeń z bazą. Wynik (rozmiar pliku przed i po, czas, ewentualny błąd) trafia na koniec podsumowania dnia dla administratorów (sekcja 11). Błąd vacuum jest też zapisywany w logu jako błąd; aplikacja kończy działanie normalnie.
- **Pierwsze uruchomienie** (pusta baza) robi pełny przegląd list i tylko zapisuje dotychczasowe dane, bez powiadomień.
- **`--run-once`** wykonuje ping i pełny przegląd list (licznik pingów w pamięci nie ma historii); powiadomienia wysyłane są normalnie.
- **Edycje i usunięcia.** Element o nowym kluczu jest nowością, także gdy w Librusie jest to edycja istniejącego (np. zmieniona data wydarzenia), a w bazie zostaje stary wpis. Dla terminarza, zmian planu lekcji i zadań domowych zniknięcie starego wpisu jest wykrywane i zgłaszane (następny punkt). Dla pozostałych typów (oceny, wiadomości, ogłoszenia, frekwencja, zrealizowane lekcje) zniknięcie z listy jest ignorowane.

### Zmiany terminu i usunięcia

Po sprawdzeniu kont, przed wysyłką powiadomień, aplikacja porównuje, co zniknęło z list terminarza, planu lekcji i zadań domowych.

- **Zniknięcie wpisu.** Wpis zniknął, gdy żadne z kont, na których był widoczny, nie wymienia go w swoim ostatnim pobraniu listy (`LastSeenUtc` starsze niż najstarsze z tych pobrań). Wniosek o zniknięciu wymaga więc, by każde takie konto pobrało listę po ostatnim zobaczeniu wpisu: konto, które nie loguje się lub nie pobiera listy, wstrzymuje wniosek, a wpis widoczny tylko na jednym koncie nie jest „usuwany” przez listę drugiego.
- **Ograniczenie do dziś i przyszłości.** Pod uwagę brane są wyłącznie wpisy z dzisiejszą lub późniejszą datą (zadanie domowe: termin wykonania), żeby przesuwające się okna list (terminarz: bieżący i następny miesiąc, plan lekcji: bieżący tydzień) nie wywoływały fałszywych alarmów. Zniknięcia wpisów z przeszłości są oznaczane po cichu. Lista zadań domowych sięga tylko 7 dni wstecz, więc zadania są oceniane, tylko jeśli udostępniono je w ostatnich 7 dniach.
- **Zmiana terminu.** Jedno zniknięcie i jeden nowy, jeszcze niewysłany wpis tego samego typu o tej samej treści, ale innej dacie, to zmiana terminu. Treść to: terminarz – przedmiot, tytuł i opis (lekcja, godzina i nauczyciel mogą się zmienić razem z datą); zadanie domowe – wszystko poza terminem wykonania. Nowy wpis dostaje wtedy powiadomienie „zmiana terminu” zamiast „nowy”, a zniknięty nie dostaje żadnego. Zmiana opisu razem z datą nie jest rozpoznawana: przychodzą wtedy dwa maile, „nowy” i „usunięty”. Zmiany planu lekcji oraz zastępstwa i odwołania z numerem lekcji w treści oraz wpisy szkoły na cały dzień nie są łączone w pary.
- **Usunięcie.** Zniknięcie bez pary daje powiadomienie „usunięto” (dotyczy wszystkich trzech typów: terminarz, w tym zastępstwa, odwołania i dni wolne, plan lekcji i zadania domowe). Gdy istnieje dokładnie jeden inny, już zgłoszony wpis o tej samej treści i innej dacie, mail wspomina o możliwym przeniesieniu na niego. Gdy wpis zniknął, zanim wysłano o nim powiadomienie „nowy”, nic nie jest wysyłane.
- **Adresaci.** Jak przy nowych wpisach: rodzice zawsze, uczeń, jeśli wpis był widoczny na jego koncie. Każde zniknięcie jest zgłaszane najwyżej raz (także gdy wpis wróci i zniknie ponownie).
- **Zabezpieczenie.** Gdy w jednym cyklu znika więcej niż 20 wpisów, aplikacja uznaje to za błąd listy: nie wysyła nic, zapisuje ostrzeżenie w logu, a wpisy, które wrócą na listę, są traktowane jak zwykle.

## 7. Powiadomienia e-mail

- **Adresaci:** rodzice dostają sumę danych z obu kont; uczeń tylko elementy widoczne na jego koncie.
- **Format:** osobny mail na każdy nowy element, z tematem opisującym element, np. nową ocenę z matematyki. Osobne maile o zmianie terminu (`[Librus] Zmiana terminu: Sprawdzian - Język niemiecki, z 2026-11-17 … na 2026-11-20 …`) i o usunięciu wpisu (`[Librus] Usunięto z terminarza: …`, `… zmianę w planie lekcji: …`, `… zadanie domowe: …`) mają tę samą budowę (tabela pól elementu) z dodatkowymi polami „Zmiana terminu” albo „Status” i „Możliwe przeniesienie” na początku.
- **Treść:** mail jest w HTML-u (multipart/alternative z wersją tekstową jako zapasową). Pola elementu (`etykieta: wartość`) są tabelą HTML, szczegóły (np. treść wiadomości) osobną tabelą; wartości z Librusa są kodowane, nigdy nie trafiają do HTML-a jako znaczniki. Maile do administratorów (podsumowanie dnia, błędy) pozostają czystym tekstem.
- **Kolory ocen:** w mailu o nowej ocenie (i w raporcie poniżej) pole z oceną ma pastelowe tło i ciemny tekst (czytelne w jasnym i ciemnym motywie klienta poczty): 6 – intensywny zielony, 5 – zielony, 4 – słaby zielony, 3 – żółty, 2 – pomarańczowy, 1 – czerwony; sam „+” – miętowy odcień zieleni (obok piątki), sam „-” – łososiowy, pomiędzy pomarańczowym a czerwonym. Ocena z plusem lub minusem przy cyfrze (np. „4+”, „3-”) ma kolor swojej cyfry. Inne oznaczenia (np. „nb”, „np”, „zw”) i oceny opisowe mają szare tło (jak „Inne” w zestawieniach dziennych); puste pole nie ma koloru. Waga (etykieta i wartość w tabeli maila, kolumna w raporcie) ma szarą czcionkę i nie jest pogrubiana, żeby nie myliła się z oceną. Wersja tekstowa maila nie ma kolorów.
- **Raport ocen z przedmiotu:** pod treścią maila o nowej ocenie jest lista ocen z tego przedmiotu z tego samego semestru (data, ocena, kategoria, waga; nagłówek raportu podaje numer semestru), od najnowszej do najstarszej: według daty oceny, a przy tej samej dacie od ostatnio wykrytej. Ocena z powiadomienia jest pogrubiona i oznaczona „(nowa)”, ocena niewliczana do średniej ma przy wadze „(poza średnią)”. Wersja tekstowa zawiera ten sam raport jako listę. Raport powstaje z bazy, bez żądań do Librusa, i nie miesza semestrów ani lat szkolnych (patrz „Semestr i rok szkolny”). Pomijane są oceny, które zniknęły z listy w Librusie (np. po poprawieniu oceny; taka sama zasada jak przy wykrywaniu zniknięć: każde konto, na którym ocena była widoczna, musi pobrać listę po jej ostatnim zobaczeniu), a uczeń widzi w raporcie tylko oceny ze swojego konta. Mail o ocenie opisowej nie ma raportu.
- **Daty względem dzisiaj:** każda data `rrrr-mm-dd` (z godziną lub bez) w temacie i treści powiadomienia dostaje w nawiasie dzień tygodnia i odległość od dnia wysyłki, np. `2026-11-13 (piątek, jutro)`: `dziś`, `jutro`, `pojutrze`, `za N dni`, a dla przeszłości `wczoraj`, `przedwczoraj`, `N dni temu`. W wersji HTML data wraz z opisem jest pogrubiona, gdy dotyczy dziś, jutra albo – gdy dziś jest dniem wolnym (weekend, święto, ferie) – pierwszego dnia szkolnego po wolnym; tekst i temat nie mają pogrubień. Bez opisu zostają daty techniczne: „Wykryto” i pole „Data dodania”; w HTML-u „Data dodania” jest dodatkowo szara, żeby nie mylić jej z datą, której dotyczy powiadomienie. Pole „Data” w powiadomieniu o nowej wiadomości (kiedy ją nadano) też jest techniczne: dostaje tylko dzień tygodnia, np. `2026-11-12 18:56:07 (czwartek)`, bez „dziś/jutro” i bez pogrubienia; daty w treści wiadomości są opisywane jak zwykle.
- **Stopka:** każdy automatyczny mail (powiadomienie i zestawienie) kończy się linią „Wiadomość wysłana automatycznie przez LibrusNotifier X.Y.Z.” z numerem wersji aplikacji.
- **Limit:** w jednym cyklu wysyłanych jest najwyżej 20 maili (łącznie dla wszystkich grup odbiorców i wszystkich rodzajów powiadomień), by nie przekroczyć limitów Gmaila. Pozostałe elementy zostają niewysłane i trafiają w kolejnych pingach, od najstarszych.
- **Błędy wysyłki:** znacznik „wysłano” jest ustawiany dopiero po udanej wysyłce. Niewysłane elementy są ponawiane w każdym kolejnym pingu, także gdy liczniki są zerowe. Administratorzy (`[odbiorcy_log]`) są informowani o błędzie, o ile mail da się wysłać.

## 8. Zestawienia dzienne

Dwa razy dziennie aplikacja wysyła mail z chronologiczną listą nadchodzących wydarzeń. Zestawienia dostają rodzice oraz uczeń (o ile `[odbiorcy_uczen]` nie jest pusta); uczeń widzi tylko wydarzenia z elementów widocznych na jego koncie.

- **Dane tylko z bazy.** Zestawienie powstaje z elementów już zapisanych przez pingi (typy `terminarz`, `zmiana_planu`, `zadanie_domowe`). Nie wykonuje żadnych żądań do Librusa i nie wymusza pinga; aktualność danych to czas ostatniego pobrania listy, podany na dole maila („Stan danych z Librusa”).
- **Terminy** (stałe, czas Europe/Warsaw, nie podlegają konfiguracji):

| Zestawienie | Kiedy | Pierwsza sekcja | Warunek wysyłki |
| --- | --- | --- | --- |
| Poranne („Odprawa poranna”) | 6:30, tylko w dni nauki (pn–pt poza świętami, feriami i wakacjami) | „Dzisiaj” – wydarzenia na dziś | zawsze, także gdy na dziś nic nie ma |
| Popołudniowe | 15:00, codziennie | „Jutro” – wydarzenia na jutro | w ciągu roku zawsze; w wakacje i ferie tylko gdy jest jakiekolwiek wydarzenie po dzisiejszym dniu |

- **Jutro wolne (popołudniowe).** Gdy jutro nie ma nauki (weekend, święto, ferie, wakacje), zamiast „Jutro” pierwszą, wyróżnioną sekcją jest pierwszy dzień nauki po wolnym: „Poniedziałek” po zwykłym weekendzie, w pozostałych przypadkach z datą, np. „Wtorek, 2.11.2027”. Nad sekcjami jest baner z powodem wolnego. Dopiero po niej idzie lista kolejnych wydarzeń.
- **Kolejne wydarzenia.** Wszystkie wydarzenia po dzisiejszym dniu (poza dniem sekcji głównej), chronologicznie i pogrupowane po dniach; zakres wyznacza to, co jest w bazie (terminarz: bieżący i następny miesiąc). Sekcje bez wydarzeń zostają w mailu z krótkim komunikatem.
- **Rodzaje i kolory.** Pole z typem wydarzenia ma pastelowe tło i ciemny tekst (czytelne w jasnym i ciemnym motywie klienta poczty); pod listą jest legenda użytych kolorów.

| Rodzaj | Rozpoznanie | Kolor |
| --- | --- | --- |
| Sprawdzian | terminarz: sprawdzian, praca klasowa, klasówka, egzamin, test | czerwony, mocniejszy (`#f28b82`, tekst pogrubiony) |
| Kartkówka | terminarz: kartkówka | czerwony, lżejszy (`#fad0cc`) |
| Zadanie domowe | zadania domowe (termin wykonania) i wpisy terminarza „zadanie domowe” | fioletowy (`#d9c4f2`) |
| Zastępstwo | terminarz „Zastępstwo z …” i zmiana planu „zastępstwo” | zielony (`#b9e4c4`) |
| Odwołane zajęcia | terminarz „Odwołane zajęcia” i zmiana planu „odwołane” | pomarańczowy (`#ffd7a8`) |
| Dzień wolny | zmiana planu „dzień wolny szkoły”, nazwa z „dzień wolny”, wpis szkoły na cały dzień w dniu bez nauki | niebieski (`#bcd9f5`) |
| Wydarzenie | wycieczka, zebranie, apel, konkurs itp.; wpis szkoły na cały dzień w dniu nauki | żółty (`#fff0a8`) |
| Inne | pozostałe wpisy terminarza i zmiany planu | szary (`#e2e2e2`) |

- **Tabela ocen (tylko w popołudniowym).** Na końcu zestawienia popołudniowego (po legendzie kolorów, przed stopką) jest tabela ocen z bieżącego semestru: wiersze to przedmioty (alfabetycznie, w polskiej kolejności liter), kolumny to kolejne oceny przedmiotu (tylko wartość, np. „4+”) od najstarszej do najnowszej: według daty oceny, a przy tej samej dacie od najwcześniej wykrytej. Każda ocena ma kolor wg skali jak w mailu o nowej ocenie (sekcja 7); nagłówek „Oceny” obejmuje najdłuższy wiersz, a krótsze wiersze po prostu się kończą. Wersja tekstowa ma jedną linię na przedmiot (`Matematyka: 5, 4+, 3-`). Dane z bazy, bez żądań do Librusa, a dobór ocen jak w raporcie pod powiadomieniem o nowej ocenie (sekcja 6, „Semestr i rok szkolny”): ten sam semestr (kolumna „Semestr” z Librusa) i rok szkolny, bez ocen, które zniknęły z listy w Librusie, uczeń tylko ze swojego konta. Oceny opisowe nie wchodzą do tabeli. **Bieżący semestr** wynika z daty: semestr 1 to wrzesień–styczeń, semestr 2 to luty–czerwiec (rok szkolny zaczyna się 1 września); lipiec i sierpień nie mają semestru, więc zestawienie wtedy nie ma tabeli. Granica jest przybliżona (szkoła ustala koniec semestru sama, najpóźniej w styczniu), więc tabela przełącza się na drugi semestr 1 lutego. Semestr bez ocen: tabela zastąpiona komunikatem „Brak ocen w bieżącym semestrze.”. Poranne zestawienie („Odprawa poranna”) tabeli ocen nie ma.
- **Duplikaty.** To samo zastępstwo, odwołanie, zadanie domowe lub dzień wolny bywa jednocześnie w terminarzu i na innej liście; w zestawieniu jest jednym wierszem (z planu lekcji: godziny i dane zmiany; z zadań domowych: temat; dzień wolny z terminarza ma nazwę).
- **Edycje i usunięcia.** Zestawienie pomija elementy terminarza i planu lekcji, które zniknęły z ostatnio pobranej listy (sekcja 6), więc sprawdzian przeniesiony na inny dzień nie występuje dwa razy; o samej zmianie informuje osobny mail (sekcja 6, „Zmiany terminu i usunięcia”). Zestawienie jest przy tym ostrzejsze niż powiadomienie: ukrywa wpis już wtedy, gdy zniknął z listy jednego z kont, nie czekając, aż pobiorą ją oba. Zadania domowe tej reguły nie dotyczy: lista obejmuje tylko ostatnie 7 dni, a termin może być dalszy.
- **Temat maila.** Poranne nazywa się „Odprawa poranna” (tak samo brzmi nagłówek w treści maila) i w temacie mówi tylko o dniu dzisiejszym, bez liczby kolejnych wydarzeń: `[Librus] Odprawa poranna, czwartek 2026-11-12: 2 (sprawdzian, zastępstwo)`, a gdy na dziś nic nie ma: `[Librus] Odprawa poranna, czwartek 2026-11-12: brak`. Popołudniowe: `[Librus] Zdarzenia na jutro, piątek 2026-11-13: 2 (sprawdzian, zastępstwo); kolejne: 4`, analogicznie „na poniedziałek 2026-11-16 …” (rodzaje wydarzeń pierwszej sekcji od najważniejszego). Treść porannego, poza tematem, nadal zawiera sekcję „Kolejne wydarzenia”.
- **Wysyłka.** Terminy są sprawdzane co najwyżej co minutę w pętli pingów, więc zestawienie wychodzi punktualnie. Wysłane zestawienie jest zapisywane w `DigestLogs` (dzień, rodzaj, grupa), więc restart aplikacji nie powoduje ponownej wysyłki. Zestawienia nie wchodzą w limit maili na cykl.
- **Kolejność: najpierw ping, potem zestawienie.** Zestawienie nigdy nie wyprzedza pinga, dzięki czemu opiera się na świeżych danych.
  - Trwający ping (oba konta, rodzica i ucznia, razem z wysyłką powiadomień) jest dokańczany; zestawienie, którego termin wypadł w jego trakcie, wychodzi zaraz po nim.
  - Gdy ping i zestawienie są należne w tej samej chwili, najpierw jest ping.
  - **Start aplikacji po terminie.** Zestawienie, którego termin już minął, a które dziś jeszcze nie zostało wysłane (według `DigestLogs`; w nowo założonej bazie żadne nie zostało), wychodzi po pierwszym pingu po starcie, bez limitu opóźnienia. Ping uznaje się za wykonany także wtedy, gdy się nie udał (zestawienie korzysta z danych w bazie). Gdy dziś nie będzie już żadnego pinga (start po ostatnim oknie odpytywania), należne zestawienia wychodzą od razu.
  - **Oba zestawienia zaległe.** Poranne „na dziś” jest wysyłane tylko do 15:00. Gdy aplikacja wystartuje po 15:00, a żadne zestawienie nie zostało dziś wysłane, wychodzi wyłącznie popołudniowe, a pominięcie porannego jest odnotowane w logu.
  - **Nowa baza.** Reguły działają tak samo w nowo założonej bazie (pierwszy ping zapisuje wtedy początkowy stan list bez powiadomień, a zestawienie powstaje z tych danych). Zestawienie czeka jednak, dopóki z konta danej grupy (rodzic dla rodziców, uczeń dla ucznia) nie pobrano terminarza choć raz, np. gdy pierwszy ping się nie udał; bez danych komunikat „brak zdarzeń” byłby nieprawdziwy. Wpis w logu pojawia się raz.
- **Błędy wysyłki.** Po nieudanej wysyłce aplikacja ponawia próbę co 5 minut (poranne najdłużej do 15:00, popołudniowe do końca dnia), a administratorzy dostają mail o błędzie.
- **`--run-once`.** Po cyklu wysyła należne zestawienia wg tych samych reguł (termin minął, dziś niewysłane wg `DigestLogs`, poranne tylko do 15:00), więc uruchamiany z crona po 15:00 wysyła popołudniowe; nieudanej wysyłki nie ponawia (zrobi to kolejne uruchomienie).
- **Poza zakresem.** Dni wolne ustalone przez szkołę (np. dni dyrektorskie) są dla kalendarza dniami nauki, więc poranne zestawienie wyjdzie też w takim dniu (patrz sekcja 9).

## 9. Harmonogram odpytywania

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

## 10. Cykl dobowy

- Przy starcie aplikacja zapamiętuje w pamięci procesu (nie w bazie) bieżący dzień kalendarzowy i obsługuje zadania tylko do końca tego dnia.
- Przed każdym żądaniem do Librusa i po jego zakończeniu sprawdza, czy dzień się zmienił; jeśli tak, kończy działanie.
- Jeśli start wypada w oknie harmonogramu, pierwszy ping następuje od razu, kolejne według interwału. Poza oknem aplikacja czeka do początku następnego okna tego dnia.
- Gdy do końca dnia nie ma już żadnego okna, aplikacja wysyła podsumowanie dnia i kończy działanie od razu (nie czeka do północy).
- `--run-once` ignoruje okna harmonogramu i kończy działanie po jednym cyklu.

## 11. Logowanie

- **Błędy Librusa** (logowanie, sieć, odpowiedź serwera): bieżące sprawdzenie danego konta jest przerywane bez ponowień w tym samym cyklu, a kolejna próba następuje w następnym pingu. Konta rodzica i ucznia są niezależne: błąd jednego nie wstrzymuje drugiego.
- **Żądania do Librusa:** każde żądanie HTTP jest zapisywane w logu lokalnym (czas, konto rodzica/ucznia, metoda, ścieżka bez ciasteczek, tokenów i danych logowania, kod odpowiedzi, czas trwania), żeby można było wykryć nadmierne odpytywanie serwisu. Podsumowanie dnia dla administratorów zawiera liczbę żądań, łącznie i na konto.
- **Log lokalny:** wypis na konsolę i plik dzienny w `data/logs` (poziomy info i błąd, bez haseł i tokenów); pliki starsze niż 30 dni są usuwane.
- **Log do administratorów** (`[odbiorcy_log]`): podsumowanie dnia (m.in. liczba wysłanych zestawień dziennych oraz wykrytych zmian terminu i usunięć) wysyłane przy zakończeniu działania (zmiana dnia lub `--run-once`; w dni roku podzielne przez 10 jego ostatnia sekcja opisuje vacuum bazy, patrz sekcja 6) oraz natychmiastowy mail przy błędzie krytycznym (np. błąd logowania, brak dostępu do Librusa, nieudana wysyłka). Ten sam błąd w ciągu dnia jest zgłaszany natychmiast tylko raz.

## 12. Do ustalenia przy implementacji

- Pola kluczowe i dane JSON dla poszczególnych typów elementów.
- Szablony i tematy maili dla poszczególnych typów.
