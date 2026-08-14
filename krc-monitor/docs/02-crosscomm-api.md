# Referencja API CrossComm (`WBC_KrcLib`)

Odtworzona z klasy `cCrossComm.cls` projektu KukavarProxy (autor oryginału: Guengoer,
05.11.2001). Ta klasa jest działającym wrapperem VB6 wokół biblioteki COM firmy KUKA,
więc stanowi wiarygodne źródło sygnatur — każde wywołanie tutaj udokumentowane
działa w produkcji od ponad dwóch dekad.

> **Zastrzeżenie.** To nie jest oficjalna dokumentacja KUKA. Sygnatury odtworzono
> z użycia w kodzie VB6. Typy zwracane i dokładne nazwy parametrów należy potwierdzić
> przez `tlbimp` / Object Browser na rzeczywistej bibliotece (faza 1 planu).

---

## 1. Identyfikacja biblioteki

| Właściwość | Wartość |
|---|---|
| Nazwa | KUKA Cross KRC Library |
| Namespace COM | `WBC_KrcLib` |
| Plik | `Cross3Krc.CIE` |
| TypeLib GUID | `{307230E0-B48F-11D4-B053-00A0D21AFA30}` |
| Wersja | 1.5 |
| Bitowość | **x86** (32-bit) |

Źródło: `KukavarProxy.vbp:3`

```
Reference=*\G{307230E0-B48F-11D4-B053-00A0D21AFA30}#1.5#0#..\Cross3Krc.CIE#KUKA Cross KRC Library
```

### Generowanie interop assembly

```cmd
tlbimp Cross3Krc.CIE /out:Interop.WBC_KrcLib.dll /namespace:WBC_KrcLib
```

Projekt VB.NET **musi** być zbudowany jako `x86`, nie `AnyCPU`.

---

## 2. Punkt wejścia — fabryka serwisów

```vb
' Coclass
Dim factory As IKServiceFactory = New KrcServiceFactory()

' Pobranie serwisu
Dim svc As Object = factory.GetService(progId, clientName)
```

| Parametr | Typ | Opis |
|---|---|---|
| `progId` | `String` | Identyfikator serwisu, np. `"WBC_KrcLib.SyncVar"` |
| `clientName` | `String` | **Identyfikator klienta** — musi być unikalny w systemie |

`clientName` w KukavarProxy to `"KUKAVARPROXY"` (`basMain.bas:56`). KrcMonitor używa
`"KRCMONITOR"`, żeby móc współistnieć z ewentualnym KukavarProxy na tym samym
kontrolerze.

Źródło: `cCrossComm.cls:124`, `cCrossComm.cls:154`

---

## 3. Katalog serwisów

Wszystkie pobierane przez `GetService`. Kolumna „KrcMonitor" mówi, czy serwis jest
w ogóle referencowany w naszym agencie.

| ProgID | Interfejs | Przeznaczenie | KrcMonitor |
|---|---|---|---|
| `WBC_KrcLib.SyncVar` | `ICKSyncVar` | Synchroniczny odczyt/zapis zmiennych | ✅ tylko odczyt |
| `WBC_KrcLib.AsyncVar` | `ICKAsyncVar` | Subskrypcje zmiennych (push) | ✅ |
| `WBC_KrcLib.AdviseMessage` | `ICKAdviseMessage` | Strumień komunikatów systemowych | ✅ |
| `WBC_KrcLib.SyncFile` | `ICKSyncFile` | Operacje na plikach robota | ❌ niereferencowany |
| `WBC_KrcLib.SyncEdit` | `ICKSyncEdit` | Edycja modułów KRL | ❌ niereferencowany |
| `WBC_KrcLib.SyncSelect` | `ICKSyncSelect` | **Start/stop/wybór programu** | ❌ **zakazany** |
| `WBC_KrcLib.SyncIo` | `ICKSyncIo` | **Reset magistrali I/O** | ❌ **zakazany** |

Źródło: `cCrossComm.cls:154-174`

> **Zasada projektu.** `SyncSelect` i `SyncIo` nie są importowane do żadnego projektu
> agenta. To jedyna technicznie wiarygodna gwarancja, że agent nie może wpłynąć na
> pracę robota — nie wystarczy „nie wywołujemy tych metod", one nie mogą istnieć
> w powierzchni API, do której agent ma dostęp.

---

## 4. `ICKSyncVar` — odczyt synchroniczny

### `ShowVar`

```vb
Function ShowVar(bstrVarName As String) As String
```

Zwraca **surową wartość** zmiennej jako string. Nie zawiera nazwy zmiennej.

```vb
Dim v As String = syncVar.ShowVar("$POS_ACT")
' v = "{X 1234.5, Y 234.1, Z 987.0, A 0.0, B 90.0, C 0.0, S 2, T 10}"
```

> **Uwaga na pułapkę z KukavarProxy.** Wrapper VB6 sztucznie doklejał nazwę:
> `strVarValue = strVarName + "=" + itfSyncvar.ShowVar(strVarName)` (`cCrossComm.cls:253`),
> a następnie `ExtractVariableValue` w `basMain.bas:374` obcinał to z powrotem po
> znaku `=`. Czysta strata. **KrcMonitor używa wartości zwracanej bezpośrednio.**
>
> Efekt uboczny tamtego bugu: jeśli wartość zmiennej sama zawierała `=`, parser
> `InStr(VarString, "=")` obcinał ją w złym miejscu. KrcMonitor jest na to odporny.

### `SetVar`

```vb
Sub SetVar(bstrVarName As String, bstrNewValue As String)
```

**Nieużywane w KrcMonitor.** Udokumentowane wyłącznie dla kompletności.

### `ShowMultiVar` / `SetMultiVar`

Nie wywoływane bezpośrednio w `cCrossComm.cls`, ale ich istnienie jest **potwierdzone
przez interfejs callbacku** — `ICKCallbackVar` deklaruje `OnShowMultiVar` i
`OnSetMultiVar` (`cCrossComm.cls:1017`, `cCrossComm.cls:1025`). Callback nie istniałby
bez metody, która go wywołuje.

Przypuszczalna sygnatura (**do potwierdzenia w fazie 1**):

```vb
Function ShowMultiVar(varNames As Variant) As Variant   ' tablica nazw → tablica wartości
```

**To jest istotna optymalizacja.** Odczyt 30 zmiennych:
- przez `ShowVar` — 30 wywołań COM, 30 roundtripów do runtime KRL
- przez `ShowMultiVar` — 1 wywołanie

Weryfikacja tej sygnatury to **zadanie o najwyższym priorytecie w fazie 1**.

---

## 5. `ICKAsyncVar` — subskrypcje (mechanizm kluczowy)

### `SetInfo`

```vb
Sub SetInfo(pCallback As ICKCallbackVar, nID As Long, bstrVarName As String, nInterval As Long)
```

| Parametr | Opis |
|---|---|
| `pCallback` | Obiekt implementujący `ICKCallbackVar` |
| `nID` | Unikalny identyfikator subskrypcji, nadawany przez klienta |
| `bstrVarName` | Nazwa zmiennej KRL |
| `nInterval` | **Interwał odświeżania w milisekundach** |

Wywołanie z KukavarProxy (`cCrossComm.cls:286`):

```vb
itfAsyncVar.SetInfo objCallbackVar, m_nID, sVariableName, 200
```

Po rejestracji CrossComm **sam wywołuje** `ICKCallbackVar.OnSetInfo` — klient nie
pollinguje. To jest funkcjonalny odpowiednik subskrypcji OPC UA, dostępny bez licencji.

**Limit:** komentarz w oryginale (`cCrossComm.cls:266`) mówi *„SetInfo count max. 75"*.
Agent musi walidować liczbę zmiennych w konfiguracji przy starcie i odmówić
uruchomienia powyżej limitu — przekroczenie objawia się cichą awarią, nie wyjątkiem.

### `Cancel`

```vb
Sub Cancel(nID As Long)
```

Wyrejestrowuje subskrypcję o podanym `nID`. Agent musi wywołać `Cancel` dla wszystkich
aktywnych subskrypcji przy zatrzymaniu serwisu — inaczej CrossComm zostaje z martwymi
referencjami do callbacku.

Źródło: `cCrossComm.cls:341`

---

## 6. `ICKCallbackVar` — interfejs callbacku (implementuje klient)

```vb
Sub OnSetInfo(nID As Long, bstrVal As String)
Sub OnShowVar(nID As Long, bstrVal As String)
Sub OnSetVar(nID As Long)
Sub OnShowMultiVar(nID As Long, varVals As Variant)
Sub OnSetMultiVar(nID As Long)
```

`OnSetInfo` dostaje **surową wartość**, bez nazwy zmiennej. Mapowanie `nID` → nazwa
utrzymuje klient. KukavarProxy trzyma to w `Collection` (`cCrossComm.cls:1003-1008`);
KrcMonitor użyje `Dictionary(Of Integer, String)`.

Źródło: `cCrossComm.cls:996-1031`

### ⚠ Wymóg wątkowania — najczęstsza przyczyna porażki

Callbacki COM **nie dotrą**, jeśli wątek rejestrujący nie jest **STA i nie pompuje
komunikatów Windows**. Windows Service domyślnie nie ma pompki komunikatów.

Agent musi:
1. Utworzyć dedykowany wątek z `Thread.SetApartmentState(ApartmentState.STA)`
2. Na tym wątku wykonać **całą** interakcję z CrossComm — `GetService`, `SetInfo`, `Advise`
3. Uruchomić na nim pętlę komunikatów (`Application.Run` z ukrytym `ApplicationContext`)
4. Przekazywać dane z callbacków do reszty agenta przez **kolejkę thread-safe**, nigdy
   nie wykonywać pracy I/O bezpośrednio w callbacku

To jest powód, dla którego KukavarProxy jest aplikacją okienkową VB6 (`Init(frmMain)`
przyjmuje formularz jako rodzica, `cCrossComm.cls:118`) — formularz zapewnia pompkę
komunikatów. Nasz serwis musi to odtworzyć jawnie.

---

## 7. `ICKAdviseMessage` + `ICKConsumeMessage` — strumień komunikatów

### Rejestracja

```vb
Sub Advise(pConsumer As ICKConsumeMessage, nMessageMask As Long)
```

Maska typów budowana z enuma (`cCrossComm.cls:179-181`):

```vb
nMessage = CLng(eMessageTypeInfo)  Or CLng(eMessageTypeState) Or _
           CLng(eMessageTypeEvent) Or CLng(eMessageTypeQuitt) Or _
           CLng(eMessageTypeWait)  Or CLng(eMessageTypeDialog)
itfAdviseMessage.Advise itfConsumeMessage, nMessage
```

W KukavarProxy `AdviseMessage` jest pobierany **tylko** gdy tryb połączenia `nC_Mode = -1`
(`cCrossComm.cls:172`), a `Connect(0)` w `basMain.bas` zawsze przekazuje `0` — więc
**oryginał nigdy nie odbiera komunikatów robota.** Cała ta funkcjonalność leży odłogiem.

### Interfejs konsumenta

```vb
Sub OnAddMessage(pMessage As TKMessage)
Sub OnAddDialog(pDialogData As TKDialog)
Sub OnSubMessage(nMessageHandle As Long)
```

Źródło: `cCrossComm.cls:1046-1081`

### Struktura `TKMessage`

Pola odczytane z użycia w `cCrossComm.cls:909-926` i `cCrossComm.cls:1051-1068`:

| Pole | Typ | Opis |
|---|---|---|
| `nMessageHandle` | `Long` | Uchwyt komunikatu (do potwierdzania) |
| `nMessage` | `Long` | Numer komunikatu |
| `eMessageType` | enum | Typ: Info / State / Event / Quitt / Wait / Dialog |
| `bstrDBMessage` | `String` | Tekst komunikatu z bazy |
| `bstrDBModule` | `String` | Moduł źródłowy |
| `bstrCause` | `String` | Przyczyna |
| `bDBCause` | `Boolean` | Czy przyczyna pochodzi z bazy |
| `nHighTimeStamp` | `Long` | Znacznik czasu — słowo wysokie |
| `nLowTimeStamp` | `Long` | Znacznik czasu — słowo niskie |
| `nBiasTimeStamp` | `Long` | Przesunięcie strefy czasowej |
| `varParams()` | `Variant()` | Parametry komunikatu |
| `varDBParams()` | `Variant()` | Parametry z bazy |

`nHighTimeStamp` / `nLowTimeStamp` to niemal na pewno `FILETIME` rozbity na dwa 32-bitowe
słowa. Konwersja w VB.NET:

```vb
Dim ft As Long = (CLng(high) << 32) Or (CLng(low) And &HFFFFFFFFL)
Dim utc As DateTime = DateTime.FromFileTimeUtc(ft)
```

**Do potwierdzenia w fazie 1** przez porównanie z zegarem kontrolera.

### Dlaczego to jest cenne

Ten strumień to gotowy feed **alarmów, stójek i zmian stanu** — dla wyliczania OEE
i analizy przyczyn przestojów wart więcej niż pozycje osi. Dostajesz go w modelu push,
z timestampem po stronie kontrolera, bez pollingu.

---

## 8. Obsługa błędów

Interfejsy serwisowe implementują `IKSyncError` lub `IKAsyncError`:

```vb
Function GetLastError() As TKMessage    ' IKSyncError
```

Wzorzec z oryginału (`cCrossComm.cls:888-936`): po złapaniu wyjątku COM rzutuj obiekt
serwisu na `IKSyncError` i pobierz szczegóły.

```vb
If TypeOf itf Is IKSyncError Then
    Dim errData As TKMessage = CType(itf, IKSyncError).GetLastError()
End If
```

`IKAsyncError` udostępnia dodatkowo:

```vb
Sub Confirm(pCallback As Object, nID As Long, nMsgHandle As Long)   ' potwierdzanie komunikatów
```

**Nieużywane w KrcMonitor** — potwierdzanie komunikatów to modyfikacja stanu kontrolera,
poza zakresem read-only.

---

## 9. Sekwencja połączenia

Odtworzona z `basMain.bas:40-78` i `cCrossComm.cls:118-195`:

```
1. New KrcServiceFactory()                          ' odpowiednik Init()
2. GetService("WBC_KrcLib.SyncVar",       "KRCMONITOR")
3. GetService("WBC_KrcLib.AsyncVar",      "KRCMONITOR")
4. GetService("WBC_KrcLib.AdviseMessage", "KRCMONITOR")
5. AdviseMessage.Advise(consumer, maska)
6. dla każdej zmiennej z allowlisty:
      AsyncVar.SetInfo(callback, id, nazwa, interwał)
7. --- praca: callbacki napływają ---
8. dla każdej subskrypcji: AsyncVar.Cancel(id)
9. zwolnienie wszystkich referencji COM (Marshal.ReleaseComObject)
```

Krok 9 odpowiada `ServerOff()` (`cCrossComm.cls:219-229`). W .NET zwalnianie musi być
jawne — `Marshal.ReleaseComObject` w odwrotnej kolejności pozyskania, na tym samym
wątku STA.

---

## 10. Zmienne KUKA przydatne w monitoringu

Nazwy potwierdzone w kodzie KukavarProxy (`basMain.bas:57-58`) lub standardowe w KRL.
**Dostępność należy zweryfikować na docelowym KSS w fazie 0.**

| Zmienna | Zawartość | Zmienność |
|---|---|---|
| `$POS_ACT` | Pozycja kartezjańska TCP (FRAME) | wysoka |
| `$AXIS_ACT` | Pozycja osiowa A1..A6 (E6AXIS) | wysoka |
| `$OV_PRO` | Override programu [%] | niska |
| `$MODE_OP` | Tryb pracy (T1/T2/AUT/EX) | niska |
| `$PRO_STATE1` | Stan interpretera robota | średnia |
| `$STOPMESS` | Aktywny komunikat stopu | zdarzeniowa |
| `$ALARM_STOP` | Stan zatrzymania awaryjnego | zdarzeniowa |
| `$IN[n]` / `$OUT[n]` | Wejścia/wyjścia cyfrowe | średnia |
| `$PRO_I_O[]` | Nazwa programu SUBMIT | niska |
| `$KR_SERIALNO` | **Numer seryjny — identyfikacja robota** | statyczna |
| `$MODEL_NAME[]` | Model robota | statyczna |
| `$ROB_TIMER` | Licznik czasu pracy | wolna |
| `$DATE` | Data/czas kontrolera | wolna |

`$KR_SERIALNO` i `$MODEL_NAME[]` są odczytywane przez KukavarProxy przy starcie
(`basMain.bas:57-58`) i przy odpowiedzi na broadcast (`frmMain.frm:286-287`) —
to dowód, że działają. **`$KR_SERIALNO` będzie tożsamością robota w certyfikacie mTLS.**

### Dobór interwałów

Nie ustawiaj jednego interwału dla wszystkiego. Sugerowany punkt startowy:

| Klasa | Interwał `SetInfo` | Przykłady |
|---|---|---|
| Pozycje | 200–500 ms | `$POS_ACT`, `$AXIS_ACT` |
| Stan | 1000 ms | `$OV_PRO`, `$MODE_OP`, `$PRO_STATE1` |
| Statyczne | odczyt raz przy starcie przez `ShowVar` | `$KR_SERIALNO`, `$MODEL_NAME[]` |
| Zdarzenia | przez `AdviseMessage`, nie przez `SetInfo` | alarmy, stopy |

Nie subskrybuj zmiennych statycznych — marnują sloty z limitu 75.

---

## 11. Znane zachowania i pułapki

| # | Zachowanie | Konsekwencja dla agenta |
|---|---|---|
| 1 | `SetInfo` limit ~75 zmiennych | Walidacja configu przy starcie, twardy błąd powyżej |
| 2 | Callbacki wymagają STA + message pump | Dedykowany wątek STA, patrz §6 |
| 3 | CrossComm może zawiesić wywołującego podczas Power-On / restartu programu | Timeout per wywołanie + circuit breaker |
| 4 | `ShowVar` zwraca surowy string, format zależny od typu zmiennej | Parser per typ (FRAME / E6AXIS / INT / REAL / BOOL / ENUM) |
| 5 | Biblioteka jest x86 | Build `x86`, nie `AnyCPU` |
| 6 | `clientName` musi być unikalny | `"KRCMONITOR"`, nie `"KUKAVARPROXY"` |
| 7 | Referencje COM nie zwalniają się same | Jawny `Marshal.ReleaseComObject` przy stopie |
| 8 | Brak potwierdzenia, czy `SetInfo` przeżywa restart KRL | Watchdog: wykryj ciszę > N s i re-subskrybuj |

Pozycja 8 jest **otwartym pytaniem projektowym** — musi zostać rozstrzygnięta
eksperymentalnie w fazie 1. Jeśli subskrypcje giną po restarcie programu robota,
agent potrzebuje pełnej logiki re-rejestracji.

---

## 12. Pytania do rozstrzygnięcia w fazie 1

Lista rzeczy, których **nie da się ustalić z kodu** i które trzeba zweryfikować
na rzeczywistym kontrolerze:

1. Dokładna sygnatura `ShowMultiVar` — jaki typ tablicy przyjmuje i zwraca?
2. Czy `SetInfo` wysyła callback przy **każdym** interwale, czy tylko przy **zmianie** wartości?
3. Czy subskrypcje przeżywają restart programu KRL / przełączenie trybu pracy?
4. Rzeczywisty limit `SetInfo` — czy 75 to twarda granica, czy ostrożne oszacowanie autora?
5. Czy `nHighTimeStamp`/`nLowTimeStamp` to faktycznie `FILETIME`?
6. Minimalny sensowny `nInterval` — czy 200 ms to dolna granica, czy da się mniej bez obciążenia?
7. Zachowanie przy równoczesnym kliencie (KukavarProxy lub smartHMI) — czy CrossComm
   obsługuje wielu klientów bez degradacji?
8. Co dokładnie zwraca `ShowVar` dla nieistniejącej zmiennej — pusty string czy wyjątek?

Odpowiedzi na te pytania trafiają z powrotem do tego dokumentu.
