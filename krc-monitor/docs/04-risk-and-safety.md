# Analiza ryzyka i bezpieczeństwa

Dokument przeznaczony do formalnego zatwierdzenia przed instalacją na robotach
produkcyjnych. Wypełniany w trakcie projektu — pola oznaczone `[do zmierzenia]`
uzupełnia się wynikami z faz 1, 4 i 5.

**Status:** wersja robocza, przed pomiarami.

---

## 1. Zakres zmiany

| Element | Opis |
|---|---|
| Co instalujemy | Windows Service `KrcMonitor.Agent`, .NET Framework 4.8, x86 |
| Gdzie | Kontroler KUKA KR C4, Windows Embedded Standard 7 |
| Ile instancji | Docelowo 400 |
| Co robi | Odczytuje zmienne KRL przez CrossComm, wysyła wychodzącym TLS |
| Czego nie robi | Nie zapisuje zmiennych, nie steruje programem, nie nasłuchuje na porcie |
| Odwracalność | Pełna — deinstalacja skryptem, brak trwałych zmian w KSS |

---

## 2. Co jest fizycznie niemożliwe

Te gwarancje wynikają z architektury systemu, nie z dyscypliny programisty.

| Zagrożenie | Dlaczego niemożliwe |
|---|---|
| Zmiana toru ruchu robota | Sterowanie ruchem to osobny proces czasu rzeczywistego. Agent w userspace Windows nie ma do niego dostępu. |
| Wpływ na układ bezpieczeństwa | Safety (E-Stop, kurtyny, SafeOperation) jest sprzętowo odseparowany. Software w Windows nie może go zmienić. |
| Zapis zmiennej KRL | `ICKSyncVar.SetVar` nie jest wywoływane, a interfejs jest opakowany tak, że metoda nie jest wystawiona. |
| Start / stop / wybór programu | `ICKSyncSelect` **nie jest importowany** do żadnego projektu agenta. |
| Reset magistrali I/O | `ICKSyncIo` **nie jest importowany**. |
| Modyfikacja programów KRL | `ICKSyncFile` i `ICKSyncEdit` **nie są importowane**. |
| Połączenie przychodzące do robota | Agent nie otwiera gniazda nasłuchującego. |
| Odczyt zmiennej spoza listy | Agent zna wyłącznie zmienne z lokalnej konfiguracji. Protokół nie ma polecenia „odczytaj X". |

> **Weryfikowalność.** Nieobecność `ICKSyncSelect` i `ICKSyncIo` w zbudowanym
> assembly jest sprawdzalna narzędziowo (ILSpy, `ildasm`). To jest kontrola,
> którą audytor może wykonać samodzielnie na binarce.

---

## 3. Rejestr ryzyk

### R-01 — Konkurencja o zasoby CrossComm

| | |
|---|---|
| **Opis** | CrossComm obsługuje też smartHMI i inne narzędzia KUKA. Agent zajmuje kolejkę i cykle CPU procesu KRC. |
| **Skutek** | Wolniejsze odświeżanie HMI, opóźnienia innych aplikacji CrossComm. |
| **Wpływ na ruch robota** | Brak — sterowanie ruchem nie przechodzi przez CrossComm. |
| **Prawdopodobieństwo** | Średnie przy agresywnych interwałach, niskie przy zalecanych. |
| **Mitygacja** | Interwały ≥ 200 ms dla pozycji, ≥ 1000 ms dla stanów. Zmienne statyczne czytane raz. `ShowMultiVar` zamiast N wywołań `ShowVar`. |
| **Weryfikacja** | E5/E7 w fazie 1, soak test w fazie 5. |
| **Pomiar** | `[do zmierzenia]` |

### R-02 — Zużycie CPU i RAM kontrolera

| | |
|---|---|
| **Opis** | KR C4 ma niewielki zapas zasobów. Agent .NET to dodatkowy proces. |
| **Skutek** | Przy braku zapasu: wolniejsze przełączanie programów, timeouty w komunikacji z PLC, w skrajnym przypadku restart Windows przez watchdog kontrolera — **co zatrzymuje robota**. |
| **Prawdopodobieństwo** | Niskie przy egzekwowanych limitach. |
| **Mitygacja** | Twarde limity (80 MB RAM, 3% CPU) z watchdogiem i kontrolowanym restartem. Pomiar wolnych zasobów w fazie 0 — roboty bez zapasu wyłączone z rolloutu. |
| **Weryfikacja** | Faza 0 (baseline), faza 1 (benchmark), faza 5 (soak). |
| **Pomiar** | `[do zmierzenia]` |

### R-03 — Zawieszenie wywołania CrossComm

| | |
|---|---|
| **Opis** | W określonych stanach KSS (Power-On, restart programu, aktywny `$STOPMESS`) wywołanie CrossComm może nie wrócić przez dłuższy czas. |
| **Skutek** | Zablokowany wątek agenta; przy złej implementacji — narastające obciążenie dokładnie w momencie, gdy operator próbuje przywrócić produkcję. |
| **Prawdopodobieństwo** | Wysokie, że wystąpi kiedykolwiek na 400 robotach. |
| **Mitygacja** | Timeout per wywołanie (2 s). Circuit breaker: po 3 timeoutach pauza 30 s. Zero retry w pętli ciasnej. |
| **Weryfikacja** | Faza 1 (E3, E4), chaos test w fazie 4. |

### R-04 — Obciążenie sieci i kolizja z PROFINET

| | |
|---|---|
| **Opis** | 400 robotów wysyłających telemetrię może nasycić łącze lub zakłócić QoS PROFINET, jeśli ruch dzieli infrastrukturę z siecią sterowania. |
| **Skutek** | **Zakłócenie komunikacji robot ↔ PLC, czyli zatrzymanie produkcji.** To najpoważniejszy realny wektor w całym dokumencie. |
| **Prawdopodobieństwo** | Niskie przy separacji, **wysokie przy płaskiej sieci**. |
| **Mitygacja** | **Wymóg twardy: ruch monitoringu na osobnym VLAN, odseparowany od PROFINET sterowania.** Rate limit po stronie agenta. Pomiar wolumenu w fazie 3 przed rolloutem. |
| **Weryfikacja** | Przegląd topologii w fazie 0, testy obciążeniowe w fazie 4. |
| **Status** | `[wymaga potwierdzenia od sieciowców]` |

### R-05 — Wyciek zasobów / degradacja w czasie

| | |
|---|---|
| **Opis** | Niezwolnione referencje COM albo wyciek pamięci narastający przez tygodnie pracy. |
| **Skutek** | Powolna degradacja kontrolera, objawiająca się po tygodniach — trudna do powiązania z przyczyną. |
| **Prawdopodobieństwo** | Średnie — to klasyczna klasa błędu w COM interop. |
| **Mitygacja** | Jawny `Marshal.ReleaseComObject`. Watchdog na working set. Soak test 2 tygodnie. Telemetria RAM agenta wysyłana do kolektora — degradacja widoczna na dashboardzie floty. |
| **Weryfikacja** | Faza 2 (24 h), faza 5 (2 tygodnie). |

### R-06 — Zapełnienie dysku kontrolera

| | |
|---|---|
| **Opis** | Logi lub bufor offline zapełniają dysk kontrolera. |
| **Skutek** | Zatrzymanie robota — KSS wymaga miejsca na dysku do pracy. |
| **Prawdopodobieństwo** | Niskie przy przyjętej decyzji projektowej. |
| **Mitygacja** | **Agent nie zapisuje na dysk kontrolera.** Bufor wyłącznie w RAM, ograniczony, drop-oldest. Logi tylko do Event Log z rotacją. |
| **Weryfikacja** | Kryterium wyjścia z fazy 2: zero zapisów plikowych. |

### R-07 — Konflikt z antywirusem / polityką IT

| | |
|---|---|
| **Opis** | Nowy niepodpisany plik wykonywalny może być blokowany lub skanowany w trakcie pracy. |
| **Skutek** | Agent nie startuje, albo skanowanie obciąża kontroler. |
| **Mitygacja** | Podpisanie binarki certyfikatem firmowym. Uzgodnienie wyjątku z IT przed rolloutem. |
| **Status** | `[wymaga uzgodnienia z IT]` |

### R-08 — Utrata wsparcia KUKA / naruszenie polityki VW

| | |
|---|---|
| **Opis** | Instalacja software'u firm trzecich na kontrolerze może naruszać warunki wsparcia lub standard VASS. |
| **Skutek** | **Krytyczny dla projektu** — nie techniczny, lecz formalny. |
| **Prawdopodobieństwo** | Średnie. Środowisko VW jest pod tym względem rygorystyczne. |
| **Mitygacja** | **Wyjaśnić w fazie 0, przed pisaniem kodu.** Uzyskać pisemne stanowisko. |
| **Status** | `[niewyjaśnione — zadanie blokujące]` |

### R-09 — Kompromitacja kolektora

| | |
|---|---|
| **Opis** | Napastnik przejmuje serwer kolektora. |
| **Skutek** | Dostęp do telemetrii produkcyjnej z allowlisty. **Brak dostępu do robotów** — protokół jest jednokierunkowy, roboty nie nasłuchują, agent nie przyjmuje poleceń. |
| **Mitygacja** | mTLS, hardening kolektora, segmentacja sieci, monitoring dostępu. |
| **Ocena** | Skutek istotnie mniejszy niż w KukavarProxy, gdzie przejęcie hosta w segmencie daje zapis zmiennych na całej flocie. |

### R-10 — Błąd w agencie destabilizujący kontroler

| | |
|---|---|
| **Opis** | Nieprzewidziany błąd (nieskończona pętla, zagłodzenie wątku, awaria w callbacku COM). |
| **Skutek** | Od restartu serwisu po degradację kontrolera. |
| **Mitygacja** | Watchdog z kontrolowanym restartem. Kill switch. Etapowy rollout z bramkami. Przetestowana procedura rollbacku. |
| **Weryfikacja** | Chaos test w fazie 4, pilot w fazie 5. |

---

## 4. Wymogi twarde przed instalacją produkcyjną

Lista kontrolna. **Każdy punkt musi być spełniony.**

- [ ] Ruch monitoringu odseparowany od PROFINET sterowania (osobny VLAN) — **R-04**
- [ ] Pisemne stanowisko w sprawie polityki VW/KUKA — **R-08**
- [ ] Uzgodniony wyjątek antywirusowy, binarka podpisana — **R-07**
- [ ] Zmierzony zapas CPU/RAM na docelowych kontrolerach — **R-02**
- [ ] Ukończony 2-tygodniowy soak test bez degradacji — **R-05**
- [ ] Ukończony 4-tygodniowy pilot bez incydentu — **R-10**
- [ ] Przetestowana procedura rollbacku
- [ ] Działający kill switch
- [ ] Utrzymanie przeszkolone, dokumentacja przekazana

---

## 5. Kill switch

Trzy niezależne mechanizmy zatrzymania, w kolejności rosnącej stanowczości:

| Poziom | Mechanizm | Czas reakcji | Zasięg |
|---|---|---|---|
| 1 | Plik-flaga `C:\KrcMonitor\DISABLED` | ≤ 60 s | pojedynczy robot |
| 2 | Zdalne zatrzymanie serwisu (`sc stop`) narzędziem deploymentu | minuty | grupa lub cała flota |
| 3 | Deinstalacja skryptem | ~30 min dla floty | cała flota |

Agent sprawdza flagę poziomu 1 co 30 s. Po wykryciu: wyrejestrowuje subskrypcje,
zwalnia referencje COM, przechodzi w stan idle bez kontaktu z CrossComm. Nie kończy
procesu — dzięki temu wznowienie nie wymaga interwencji przy robocie.

**Procedura użycia musi być znana utrzymaniu przed rozpoczęciem pilota.**

---

## 6. Plan rollbacku

| Sytuacja | Działanie | Czas |
|---|---|---|
| Podejrzenie wpływu na 1 robota | Kill switch poziom 1 | < 1 min |
| Podejrzenie wpływu na linię | Kill switch poziom 2 dla linii | < 10 min |
| Incydent na poziomie floty | Kill switch poziom 2 dla wszystkich, potem poziom 3 | < 30 min |
| Wycofanie projektu | Deinstalacja + usunięcie certyfikatów + zamknięcie reguł firewall | 1 dzień |

Agent nie modyfikuje konfiguracji KSS, nie zmienia rejestru poza własnym kluczem
i nie instaluje sterowników. Deinstalacja przywraca kontroler do stanu sprzed
instalacji — nie ma stanu, który trzeba by cofać ręcznie.

---

## 7. Odniesienie: porównanie z KukavarProxy

Punkt odniesienia, bo KukavarProxy jest rozwiązaniem, które ten projekt zastępuje.

| Wektor | KukavarProxy | KrcMonitor |
|---|---|---|
| Otwarty port na robocie | TCP 7000 + UDP 6999, `0.0.0.0` | brak |
| Uwierzytelnianie | brak | mTLS, certyfikat per robot |
| Zapis zmiennych przez sieć | **tak, bez uwierzytelnienia** | niemożliwy |
| Zakres odczytu | dowolna zmienna na żądanie | wyłącznie allowlista |
| Wykrywalność w sieci | rozgłasza się na UDP broadcast | niewidoczny |
| Limity zasobów | brak | twarde, z watchdogiem |
| Obsługa błędów | `On Error Resume Next` | jawna, z circuit breakerem |
| Kill switch | brak | trzy poziomy |
| Audyt | lista w oknie, 100 pozycji | Event Log + strumień do kolektora |

Wniosek do zatwierdzenia: KrcMonitor ma **istotnie niższy profil ryzyka** niż
rozwiązanie, które w tej roli bywa stosowane, przy zachowaniu funkcjonalności
monitoringu.

---

## 8. Zatwierdzenia

| Rola | Osoba | Data | Podpis |
|---|---|---|---|
| Właściciel projektu | | | |
| Utrzymanie robotów | | | |
| IT Security | | | |
| Sieci | | | |
| Jakość / BHP | | | |
| Przedstawiciel KUKA (jeśli wymagany) | | | |
