# Plan wdrożenia

## Podsumowanie harmonogramu

| Faza | Zakres | Czas | Zależność |
|---|---|---|---|
| 0 | Rozpoznanie i inwentaryzacja | 1 tydz. | — |
| 1 | Probe — weryfikacja API CrossComm | 1 tydz. | faza 0 |
| 2 | Agent MVP | 2 tyg. | faza 1 |
| 3 | Transport + kolektor | 1,5 tyg. | faza 2 |
| 4 | Hardening i testy obciążeniowe | 1,5 tyg. | faza 3 |
| 5 | Soak test + pilot produkcyjny | 6 tyg. | faza 4 |
| 6 | Rollout na 400 robotów | 6–8 tyg. | faza 5 |

**Software gotowy:** ~7 tygodni roboczych.
**Pełny rollout produkcyjny:** ~4–5 miesięcy.

Faza 5 wygląda na długą i taka ma być. Skracanie jej to jedyne miejsce w tym planie,
gdzie oszczędność czasu kupuje się ryzykiem zatrzymania linii.

---

## Faza 0 — Rozpoznanie (1 tydzień)

**Cel:** ustalić, czy projekt w ogóle ma podstawy, zanim powstanie linijka kodu.

### 0.1 Inwentaryzacja floty

Na reprezentatywnej próbce (min. 10 robotów z różnych linii i roczników):

| Do ustalenia | Metoda |
|---|---|
| Model kontrolera (KR C2 / C4 / C5) | Tabliczka + `Help → Info` na smartHMI |
| Wersja KSS | `Help → Info` |
| Wersja Windows | `winver` na kontrolerze |
| Zainstalowany .NET Framework | Rejestr: `HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full` → `Release` |
| Obecność `Cross3Krc.CIE` | Szukaj w `C:\Windows\System32` i `SysWOW64` |
| Rejestracja TypeLib | Rejestr: `HKCR\TypeLib\{307230E0-B48F-11D4-B053-00A0D21AFA30}` |
| Wolny RAM / obciążenie CPU | Task Manager przy pracy produkcyjnej |
| Wolne miejsce na dysku | `dir C:\` |
| `$KR_SERIALNO` — unikalny? | Odczyt na kilkunastu robotach, porównanie |

**Wynik:** arkusz inwentaryzacyjny + odpowiedź „na ilu % floty to zadziała".

### 0.2 Weryfikacja CrossComm

Na jednym robocie, najlepiej offline:

```cmd
reg query HKCR\TypeLib\{307230E0-B48F-11D4-B053-00A0D21AFA30}
```

Jeśli klucz istnieje — biblioteka jest zarejestrowana. Jeśli nie, sprawdź czy plik
`Cross3Krc.CIE` w ogóle jest na dysku i czy `regsvr32` przechodzi.

**Test ostateczny:** uruchom oryginalny `KukavarProxy.exe` z tego repo na robocie
testowym i sprawdź, czy odczytuje `$KR_SERIALNO`. Jeśli tak — CrossComm działa
i cały projekt ma podstawy. **To jest bramka do fazy 1.**

### 0.3 Ustalenia organizacyjne

| Kwestia | Kto odpowiada |
|---|---|
| Dostęp do robota testowego / lab cell | utrzymanie robotów |
| Polityka VW/VASS wobec software'u firm trzecich na kontrolerze | IT / dział jakości |
| Czy instalacja narusza warunki wsparcia KUKA | account manager KUKA |
| Topologia sieci: trasa robot → kolektor | sieciowcy |
| Wewnętrzne CA do wystawiania certyfikatów | IT security |
| Narzędzie deploymentu na 400 maszyn (Ansible / SCCM / WinRM / SMB) | IT |
| Co PLC już widzi z robotów | automatycy PLC |

Punkt ostatni jest istotny nawet teraz: jeśli okaże się, że PLC ma 90% potrzebnych
danych, tańszą drogą może być kolektor po `snap7` zamiast agenta na 400 robotach.
**Warto to sprawdzić przed fazą 1.**

### Kryteria wyjścia z fazy 0

- [ ] KukavarProxy odczytuje `$KR_SERIALNO` na robocie testowym
- [ ] Znana wersja .NET Framework na flocie
- [ ] Potwierdzona trasa sieciowa robot → planowany kolektor
- [ ] Uzyskana zgoda organizacyjna na instalację software'u na kontrolerze
- [ ] Dostępny robot testowy do wyłącznej dyspozycji projektu

**Jeśli którykolwiek punkt jest niespełniony, faza 1 się nie zaczyna.**

---

## Faza 1 — Probe (1 tydzień)

**Cel:** odpowiedzieć na 8 otwartych pytań z [`02-crosscomm-api.md` §12](02-crosscomm-api.md).
To faza badawcza, nie produkcyjna.

### 1.1 Interop assembly

```cmd
tlbimp Cross3Krc.CIE /out:Interop.WBC_KrcLib.dll /namespace:WBC_KrcLib
```

Otwórz wynik w ILSpy / Object Browser i **spisz rzeczywiste sygnatury** wszystkich
interfejsów. Zaktualizuj `02-crosscomm-api.md` faktami zamiast domysłów.
Szczególnie: `ShowMultiVar`.

### 1.2 `KrcMonitor.Probe`

Aplikacja konsolowa VB.NET, x86, .NET Framework 4.8. Minimalna, jednoplikowa,
bez architektury — to narzędzie badawcze.

Funkcje:

| Komenda | Odpowiada na pytanie |
|---|---|
| `--list-services` | Które serwisy `GetService` faktycznie zwraca? |
| `--read <var>` | Co dokładnie zwraca `ShowVar`? Jak wygląda format `FRAME`? |
| `--read-multi <v1,v2,...>` | Czy `ShowMultiVar` istnieje i jaka jest jego sygnatura? |
| `--subscribe <var> --interval <ms> --duration <s>` | Czy callbacki działają? Przy każdym tiku czy tylko przy zmianie? |
| `--messages --duration <s>` | Jak wygląda strumień `TKMessage`? Czy timestamp to FILETIME? |
| `--max-subs` | Gdzie faktycznie leży limit `SetInfo`? |
| `--benchmark` | Jaki narzut CPU/RAM przy N zmiennych i różnych interwałach? |

### 1.3 Eksperymenty na robocie testowym

| # | Eksperyment | Mierzone |
|---|---|---|
| E1 | Subskrypcja `$POS_ACT` @ 200 ms przez 30 min | Stabilność, CPU, czy callbacki nie giną |
| E2 | Zwiększanie liczby subskrypcji do awarii | Rzeczywisty limit `SetInfo` |
| E3 | Restart programu KRL przy aktywnych subskrypcjach | **Czy subskrypcje przeżywają?** |
| E4 | Przełączenie T1 → AUT przy aktywnych subskrypcjach | Zachowanie przy zmianie trybu |
| E5 | Probe + smartHMI jednocześnie | Czy CrossComm obsługuje wielu klientów |
| E6 | Probe + oryginalny KukavarProxy jednocześnie | Konflikt `clientName`? |
| E7 | Interwał 50 / 100 / 200 / 500 / 1000 ms | Gdzie leży sensowna dolna granica |
| E8 | Odczyt nieistniejącej zmiennej | Wyjątek czy pusty string |
| E9 | Odłączenie kabla sieciowego kontrolera | Czy CrossComm reaguje (nie powinien) |

### Kryteria wyjścia z fazy 1

- [ ] Wszystkie 8 pytań z §12 ma udokumentowaną odpowiedź
- [ ] `02-crosscomm-api.md` zaktualizowany o rzeczywiste sygnatury
- [ ] E3 rozstrzygnięty — wiadomo, czy potrzebna logika re-subskrypcji
- [ ] Zmierzony narzut CPU/RAM przy docelowej liczbie zmiennych
- [ ] Potwierdzone, że callbacki STA działają w aplikacji bez okna

> **Bramka decyzyjna.** Jeśli faza 1 wykaże, że `SetInfo` jest niestabilny albo
> generuje istotne obciążenie, wracamy do projektu: polling `ShowMultiVar` z niską
> częstotliwością zamiast subskrypcji. **To jest dopuszczalny wynik fazy 1**, nie porażka.

---

## Faza 2 — Agent MVP (2 tygodnie)

**Cel:** działający Windows Service zbierający dane, jeszcze bez sieci.

### Zakres

| Projekt | Zawartość |
|---|---|
| `KrcMonitor.Interop` | Sesja CrossComm z wątkiem STA, timeouty, zwalnianie COM |
| `KrcMonitor.Core` | Model danych, parsery KRL, config + walidacja, `BoundedQueue`, circuit breaker |
| `KrcMonitor.Agent` | Serwis Windows, watchdog, Event Log, kill switch |

### Zadania

1. Wątek STA z pompką komunikatów — **odizolowany i przetestowany osobno**
2. `ICrossCommSession` z pełnym cyklem życia (connect → subscribe → work → release)
3. Timeout per wywołanie COM + circuit breaker
4. Parsery: `FRAME`, `E6AXIS`, `E6POS`, `INT`, `REAL`, `BOOL`, `ENUM` + testy jednostkowe na próbkach z fazy 1
5. Wczytywanie konfiguracji + walidacja (limit subskrypcji, poprawność nazw, interwały)
6. `BoundedQueue` z drop-oldest i licznikiem odrzuceń
7. Watchdog: RAM, CPU, cisza z CrossComm, kill switch
8. Instalacja/deinstalacja serwisu
9. Wyjście tymczasowe: plik JSON Lines w katalogu tymczasowym **(tylko na czas MVP)**

### Kryteria wyjścia z fazy 2

- [ ] Serwis startuje, działa 24 h bez wycieku pamięci
- [ ] Zatrzymanie serwisu zwalnia wszystkie referencje COM (weryfikacja w Process Explorer)
- [ ] Watchdog restartuje serwis po sztucznym przekroczeniu limitu RAM
- [ ] Kill switch zatrzymuje zbieranie w < 60 s
- [ ] Uszkodzony config → serwis nie startuje, czytelny wpis w Event Log
- [ ] Pokrycie testami jednostkowymi parserów i `BoundedQueue` > 80%
- [ ] Zero zapisów na dysk kontrolera poza Event Log

---

## Faza 3 — Transport i kolektor (1,5 tygodnia)

### Zakres

1. Zamrożenie protokołu ([`06-protocol.md`](06-protocol.md))
2. Klient mTLS w agencie: reconnect z backoff, batching, keepalive
3. `KrcMonitor.Collector`: terminacja TLS, weryfikacja CN, walidacja, rate limit
4. Schemat TimescaleDB + hypertables + polityka retencji
5. Dashboardy Grafana: pozycje, stany, alarmy, zdrowie floty
6. Usunięcie tymczasowego wyjścia plikowego z fazy 2

### Kryteria wyjścia z fazy 3

- [ ] Agent → kolektor → baza → Grafana działa end-to-end na 1 robocie
- [ ] Certyfikat z niewłaściwym CN jest odrzucany
- [ ] Zabicie kolektora nie wpływa na agenta; po restarcie strumień wznawia się sam
- [ ] Zmierzony wolumen danych na robota → ekstrapolacja na 400
- [ ] Polityka retencji ustalona i wdrożona

---

## Faza 4 — Hardening (1,5 tygodnia)

### Zakres

1. **PKI i provisioning:** skrypt generujący certyfikat per robot, CN = `$KR_SERIALNO`
2. **Telemetria agenta:** heartbeat, liczniki odrzuceń, błędy COM, CPU/RAM — jako
   osobny strumień do kolektora
3. **Rejestr floty w kolektorze:** który robot milczy i od kiedy, alerting
4. **Kill switch zdalny:** mechanizm i procedura użycia
5. **Testy obciążeniowe:** symulacja 400 agentów przeciw jednemu kolektorowi
6. **Test chaos:** losowe zabijanie agenta/kolektora, zrywanie sieci, zapełnianie dysku
7. **Pakiet instalacyjny:** cichy installer + skrypt deinstalacji
8. **Procedura rollback:** jak w 30 minut usunąć agenta z całej floty

### Kryteria wyjścia z fazy 4

- [ ] Kolektor obsługuje 400 symulowanych agentów w budżecie zasobów
- [ ] Chaos test przechodzi bez utraty stabilności agenta
- [ ] Instalacja i deinstalacja w pełni skryptowalna, bez interakcji
- [ ] Rollback przetestowany na grupie testowej
- [ ] Dokument [`04-risk-and-safety.md`](04-risk-and-safety.md) uzupełniony wynikami pomiarów

---

## Faza 5 — Soak test i pilot (6 tygodni)

**To jest najważniejsza faza projektu.** Nie skracać.

### 5.1 Soak test w labie (2 tygodnie)

Jeden robot offline, agent w konfiguracji docelowej, praca ciągła.

Monitorowane: RAM agenta w czasie, CPU kontrolera, responsywność smartHMI,
liczba błędów COM, ciągłość strumienia, zachowanie po restarcie kontrolera.

**Kryterium:** zero degradacji w stosunku do pomiaru bazowego sprzed instalacji.

### 5.2 Risk assessment i zatwierdzenia (równolegle)

Formalne zamknięcie [`04-risk-and-safety.md`](04-risk-and-safety.md) i uzyskanie zgód:
utrzymanie, IT security, jakość, w razie potrzeby KUKA.

### 5.3 Pilot produkcyjny (4 tygodnie)

5–10 robotów na **mniej krytycznej linii**. Metryka nadrzędna:

> Czy linia z agentem stoi częściej niż linia bez agenta?

Porównanie z grupą kontrolną. Jeśli różnica jest istotna statystycznie — **stop**
i analiza przyczyn.

### Kryteria wyjścia z fazy 5

- [ ] 2 tygodnie soak testu bez degradacji
- [ ] 4 tygodnie pilota bez incydentu przypisanego agentowi
- [ ] Brak istotnej różnicy w dostępności linii pilotażowej vs kontrolna
- [ ] Wszystkie zgody formalne uzyskane
- [ ] Procedura rollback przetestowana na produkcji

---

## Faza 6 — Rollout (6–8 tygodni)

Etapami, z bramką decyzyjną po każdym.

| Etap | Liczba robotów | Czas obserwacji | Bramka |
|---|---|---|---|
| R1 | 10 | 1 tydz. | Zero incydentów |
| R2 | 50 | 1 tydz. | Kolektor w budżecie zasobów |
| R3 | 150 | 2 tyg. | Baza i retencja wydolne |
| R4 | 400 | 2 tyg. | Pełna flota stabilna |

Po każdym etapie: przegląd metryk, decyzja go/no-go, gotowość do rollbacku.

### Kryteria zakończenia projektu

- [ ] 400 agentów zainstalowanych i raportujących
- [ ] Dashboardy Grafana w użyciu produkcyjnym
- [ ] Alerting na milczące roboty działa
- [ ] Dokumentacja operacyjna przekazana utrzymaniu
- [ ] Procedura rollback udokumentowana i przećwiczona
- [ ] Zespół utrzymania przeszkolony

---

## Ryzyka projektowe

| # | Ryzyko | Prawd. | Skutek | Reakcja |
|---|---|---|---|---|
| R1 | CrossComm nieobecny na części floty | średnie | wysoki | Faza 0 wykrywa wcześnie; fallback na odczyt z PLC |
| R2 | `SetInfo` niestabilny | średnie | średni | Fallback: polling `ShowMultiVar` |
| R3 | Callbacki STA nie działają w serwisie | niskie | wysoki | Prototyp w fazie 1; fallback: agent jako aplikacja z ukrytym oknem |
| R4 | VW/KUKA nie zgadza się na software na kontrolerze | **średnie** | **krytyczny** | **Wyjaśnić w fazie 0, przed pisaniem kodu** |
| R5 | Agent degraduje wydajność kontrolera | niskie | wysoki | Limity zasobów, soak test, pilot |
| R6 | Wolumen danych przerasta bazę | średnie | średni | Pomiar w fazie 3, agregacja i retencja |
| R7 | Brak zasobów deploymentowych na 400 maszyn | średnie | średni | Ustalić narzędzie w fazie 0 |
| R8 | Zmiana wersji KSS psuje interop | niskie | średni | Test na każdej wersji obecnej we flocie |

**R4 jest ryzykiem o najgorszym stosunku prawdopodobieństwa do skutku.** Wyjaśnienie
polityki VW wobec software'u firm trzecich na kontrolerze robota to **pierwsze
zadanie fazy 0**, przed jakąkolwiek pracą techniczną. Odpowiedź „nie" unieważnia
cały plan i oszczędza dwa miesiące pracy.

---

## Zapotrzebowanie na zasoby

| Rola | Zaangażowanie | Fazy |
|---|---|---|
| Programista .NET | pełny etat | 1–6 |
| Inżynier robotyki (KUKA) | 30% | 0, 1, 5 |
| IT security (PKI) | 20% | 0, 4 |
| Sieciowiec | 10% | 0, 3 |
| Administrator (deployment) | 30% | 4, 6 |
| Utrzymanie robotów | 20% | 5, 6 |

**Sprzęt:**
- 1 robot testowy offline **do wyłącznej dyspozycji projektu** (bramka fazy 0)
- 1 serwer kolektora (dev) + 1 produkcyjny
- Maszyna deweloperska z Visual Studio i dostępem do `Cross3Krc.CIE`

---

## Co robić w pierwszej kolejności

1. **Zapytaj o politykę VW/KUKA wobec software'u na kontrolerze** (ryzyko R4)
2. **Zapytaj automatyków PLC, ile danych już mają w S7** — może to unieważnić cały projekt na Waszą korzyść
3. **Załatw robota testowego** — bez niego faza 1 nie ruszy
4. Uruchom `KukavarProxy.exe` na tym robocie i sprawdź, czy widzi `$KR_SERIALNO`

Punkty 1 i 2 mogą zakończyć projekt, zanim zainwestujesz w niego tydzień pracy.
Dlatego są pierwsze.
