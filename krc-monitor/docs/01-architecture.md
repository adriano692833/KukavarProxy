# Architektura

## 1. Założenia projektowe

| # | Założenie | Uzasadnienie |
|---|---|---|
| A1 | Tylko odczyt | Zakres to monitoring. Zapis podnosi klasę ryzyka o rząd wielkości i nie wnosi nic do celu. |
| A2 | Agent nie nasłuchuje | Port nasłuchowy na 400 robotach to 400 punktów wejścia. Połączenie wychodzące to zero. |
| A3 | Push zamiast pollingu | `SetInfo` daje subskrypcje za darmo. Polling obciąża CrossComm bez potrzeby. |
| A4 | Allowlist zmiennych | Agent bez „czytaj cokolwiek" jest bezużyteczny dla atakującego, który przejmie kolektor. |
| A5 | Twarde limity zasobów | Kontroler KR C4 ma mały zapas CPU/RAM. Agent musi mieć sufit, nie „zwykle zużywa mało". |
| A6 | Degradacja zamiast awarii | Utrata kolektora nie może wpłynąć na kontroler. Agent w izolacji po prostu wyrzuca dane. |
| A7 | Jeden język w zespole | VB.NET po stronie agenta, .NET po stronie kolektora. Zespół utrzymuje jeden stos. |

## 2. Komponenty

```
krc-monitor/
├── src/
│   ├── KrcMonitor.Interop/      Wrapper COM (VB.NET, x86)
│   ├── KrcMonitor.Core/         Model danych, config, kolejka (VB.NET)
│   ├── KrcMonitor.Agent/        Windows Service (VB.NET, x86)
│   ├── KrcMonitor.Collector/    Serwer zbierający (.NET 8)
│   └── KrcMonitor.Probe/        Narzędzie diagnostyczne CLI (VB.NET, x86)
├── deploy/                      Skrypty instalacyjne i PKI
└── docs/
```

### 2.1 `KrcMonitor.Interop`

Jedyny projekt, który wie o istnieniu COM. Reszta agenta widzi tylko czysty interfejs .NET.

**Odpowiedzialność:**
- Zarządzanie wątkiem STA + pompką komunikatów
- `GetService` dla `SyncVar`, `AsyncVar`, `AdviseMessage` — **i tylko dla nich**
- Implementacja `ICKCallbackVar` i `ICKConsumeMessage`
- Tłumaczenie callbacków COM na zdarzenia .NET
- Timeout per wywołanie, mapowanie błędów COM na wyjątki .NET
- `Marshal.ReleaseComObject` przy zamykaniu

**Publiczna powierzchnia (szkic):**

```vb
Public Interface ICrossCommSession
    Function ConnectAsync(clientName As String) As Task
    Function ReadOnceAsync(varName As String) As Task(Of String)
    Sub Subscribe(varName As String, intervalMs As Integer)
    Sub UnsubscribeAll()
    Event VariableChanged As EventHandler(Of VariableSample)
    Event RobotMessage As EventHandler(Of RobotMessage)
    Event ConnectionLost As EventHandler
End Interface
```

**Czego tu nie ma i nie będzie:** `SetVar`, `Select`, `Cancel` (programu), `IoControl`.
Interfejsy `ICKSyncSelect` i `ICKSyncIo` nie są nawet importowane z interop assembly.

### 2.2 `KrcMonitor.Core`

Logika niezależna od COM i od transportu — **w pełni testowalna jednostkowo bez robota**.

- Model danych (`VariableSample`, `RobotMessage`, `AgentHealth`)
- Parsery formatów KRL: `FRAME`, `E6AXIS`, `E6POS`, `INT`, `REAL`, `BOOL`, `ENUM`
- Wczytywanie i walidacja konfiguracji (w tym limit 75 subskrypcji)
- `BoundedQueue(Of T)` — kolejka z polityką drop-oldest
- Circuit breaker
- Serializacja do formatu wymiany

### 2.3 `KrcMonitor.Agent`

Windows Service spinający całość.

```
┌─────────────────── KrcMonitor.Agent ────────────────────┐
│                                                          │
│  ┌────────────────┐                                      │
│  │ Wątek STA      │  ← jedyny wątek dotykający COM       │
│  │  message pump  │                                      │
│  │  callbacki ────┼──► BoundedQueue ◄── enqueue          │
│  └────────────────┘         │                            │
│                             │ dequeue                    │
│  ┌──────────────────────────▼─────────────────────────┐  │
│  │ Wątek wysyłki                                      │  │
│  │  batching → serializacja → mTLS → kolektor         │  │
│  └────────────────────────────────────────────────────┘  │
│                                                          │
│  ┌────────────────────────────────────────────────────┐  │
│  │ Watchdog                                           │  │
│  │  • cisza z CrossComm > N s → re-subskrypcja        │  │
│  │  • RAM > limit → kontrolowany restart serwisu      │  │
│  │  • N timeoutów COM → circuit breaker, pauza        │  │
│  │  • sprawdzenie kill switch                         │  │
│  └────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────┘
```

### 2.4 `KrcMonitor.Collector`

Proces po stronie centralnej. Nie działa na kontrolerze.

- Terminacja TLS z weryfikacją certyfikatu klienta
- Mapowanie CN certyfikatu → tożsamość robota, odrzucenie niezgodności
- Walidacja i rate limiting per robot
- Zapis do TimescaleDB
- Endpoint `/health` i metryki dla Prometheusa
- Rejestr stanu floty: który robot milczy i od kiedy

### 2.5 `KrcMonitor.Probe`

Narzędzie CLI uruchamiane ręcznie na kontrolerze przy diagnostyce i podczas fazy 1.
Nie instaluje się jako serwis, nic nie wysyła po sieci — wypisuje na konsolę.

```
KrcMonitor.Probe.exe --list-services
KrcMonitor.Probe.exe --read $POS_ACT
KrcMonitor.Probe.exe --subscribe $POS_ACT --interval 200 --duration 30
KrcMonitor.Probe.exe --messages --duration 60
KrcMonitor.Probe.exe --benchmark --vars 75
```

To jest **pierwszy artefakt do zbudowania** — odpowiada na otwarte pytania z
[`02-crosscomm-api.md` §12](02-crosscomm-api.md).

## 3. Przepływ danych

```
KRL runtime
   │  (CrossComm, in-process COM)
   ▼
ICKAsyncVar.SetInfo ──► OnSetInfo(nID, wartość)
ICKAdviseMessage    ──► OnAddMessage(TKMessage)
   │
   │  wątek STA — tylko enqueue, zero I/O
   ▼
BoundedQueue (domyślnie 10 000 pozycji, drop-oldest)
   │
   │  wątek wysyłki — batch co 1 s lub 500 pozycji
   ▼
Serializacja (JSON Lines lub Protobuf — patrz 06-protocol.md)
   │
   ▼
Strumień mTLS ──────────────► Collector
   │                              │
   │ przy braku łączności:        ▼
   │ kolejka rośnie do limitu,  TimescaleDB
   │ potem drop-oldest            │
   │ (NIGDY nie na dysk           ▼
   │  kontrolera)              Grafana
```

**Kluczowa decyzja:** przy utracie łączności agent **nie buforuje na dysk kontrolera**.
Dysk KR C4 to często karta CF z ograniczoną liczbą cykli zapisu, a zapełnienie dysku
kontrolera to realny sposób na zatrzymanie robota. Tracimy dane — akceptujemy to.
Monitoring nie jest systemem krytycznym.

## 4. Model bezpieczeństwa

| Warstwa | Mechanizm |
|---|---|
| Sieć | Brak portów nasłuchowych na robocie. Wyłącznie wychodzące TCP do jednego hosta:portu. |
| Transport | TLS 1.2+, weryfikacja obustronna (mTLS) |
| Tożsamość | Certyfikat per robot, CN = `$KR_SERIALNO`, wystawiany przez wewnętrzne CA |
| Autoryzacja | Kolektor przyjmuje wyłącznie znane CN. Nieznany certyfikat → rozłączenie i alert |
| Zakres danych | Allowlist zmiennych w configu agenta. Kolektor nie może zażądać innych |
| Kierunek sterowania | **Brak.** Kolektor nie ma kanału do wydawania poleceń agentowi |
| Kill switch | Plik-flaga na kontrolerze + wpis w rejestrze; sprawdzany co 30 s |
| Audyt | Windows Event Log lokalnie (z rotacją), pełne logi po sieci do kolektora |

### Analiza „co jeśli kolektor zostanie przejęty"

Najgorszy scenariusz jest celowo nudny:
- napastnik widzi telemetrię z allowlisty — dane produkcyjne, nie sterujące
- **nie może** odczytać innych zmiennych — agent zna tylko swoją listę
- **nie może** nic zapisać — nie istnieje ścieżka kodu
- **nie może** wysłać polecenia — protokół jest jednokierunkowy
- **nie może** dosięgnąć robota po sieci — robot nie nasłuchuje

To jest zasadnicza różnica względem KukavarProxy, gdzie przejęcie dowolnego hosta
w segmencie daje pełny odczyt **i zapis** zmiennych na wszystkich robotach.

## 5. Budżet zasobów na kontrolerze

Limity twarde, egzekwowane przez agenta. Przekroczenie → kontrolowany restart serwisu
z wpisem do Event Log.

| Zasób | Limit | Egzekwowanie |
|---|---|---|
| RAM (working set) | 80 MB | Watchdog co 10 s, restart po przekroczeniu |
| CPU (średnia 60 s) | 3% | Watchdog, throttling batchowania |
| Kolejka | 10 000 pozycji | `BoundedQueue`, drop-oldest |
| Zapis na dysk kontrolera | **0 B/s w normalnej pracy** | Brak logowania plikowego; tylko Event Log |
| Połączenia wychodzące | 1 | Jeden strumień do jednego hosta |
| Subskrypcje CrossComm | 75 (limit API) | Walidacja configu przy starcie |

Wartości do zweryfikowania i skorygowania po testach obciążeniowych w fazie 4.

## 6. Tryby degradacji

| Sytuacja | Zachowanie agenta | Wpływ na robota |
|---|---|---|
| Kolektor nieosiągalny | Kolejkuje do limitu, potem drop-oldest. Retry z backoff. | brak |
| CrossComm nie odpowiada | Circuit breaker: pauza 30 s, potem próba ponownego połączenia | brak |
| Restart programu KRL | Watchdog wykrywa ciszę, re-subskrybuje | brak |
| Przekroczony limit RAM | Kontrolowany restart serwisu | brak |
| Kill switch aktywny | Wyrejestrowuje subskrypcje, zwalnia COM, przechodzi w idle | brak |
| Uszkodzony config | Serwis **nie startuje**, wpis do Event Log | brak |
| Certyfikat wygasł | Retry z backoff, alarm w Event Log | brak |

Reguła nadrzędna: **żadna awaria agenta nie może wymagać interwencji przy robocie.**
Wszystko rozwiązywalne zdalnie albo samoczynnie.

## 7. Decyzje odrzucone i dlaczego

| Rozważane | Odrzucone, bo |
|---|---|
| Agent jako serwer TCP (jak KukavarProxy) | 400 otwartych portów w sieci sterowania |
| Polling `ShowVar` w pętli | `SetInfo` daje to samo taniej dla kontrolera |
| Buforowanie na dysk kontrolera | Ryzyko zapełnienia dysku i zużycia karty CF |
| Zapis zmiennych „na wszelki wypadek" | Zmienia klasę ryzyka; niepotrzebne do monitoringu |
| Self-update agenta | Nietestowalna ścieżka na 400 maszynach produkcyjnych; deployment przez narzędzie |
| Jeden interwał dla wszystkich zmiennych | Marnuje budżet CrossComm na zmienne statyczne |
| C# | Zespół utrzymuje VB6; VB.NET to mniejszy skok kompetencyjny |
| .NET 8 na agencie | Nie ma gwarancji na Windows Embedded Standard 7; `.NET Framework 4.8` jest pewny |

## 8. Otwarte kwestie architektoniczne

Do rozstrzygnięcia przed zamrożeniem projektu:

1. **Format serializacji** — JSON Lines (czytelny, debugowalny) czy Protobuf (kompaktowy,
   schematyzowany)? Decyzja po pomiarze wolumenu w fazie 3.
2. **Jeden kolektor czy per zakład?** Zależy od topologii sieci — wejście z fazy 0.
3. **Czy kolektor ma wystawiać API dla MES**, czy MES czyta z bazy? Wejście od zespołu MES.
4. **Retencja w TimescaleDB** — 400 robotów × ~5 próbek/s to ~170 mln rekordów/dobę.
   Potrzebna polityka agregacji i retencji przed uruchomieniem produkcyjnym.
5. **Czy `$KR_SERIALNO` jest unikalny i stabilny na całej flocie?** Jeśli nie —
   potrzebna alternatywna tożsamość. Weryfikacja w fazie 0.
