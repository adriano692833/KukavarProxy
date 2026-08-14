# KrcMonitor.Probe

Read-only diagnostic tool for the KUKA CrossComm (`WBC_KrcLib`) COM API.

**Nie jest to część agenta produkcyjnego.** To narzędzie fazy 1, którego jedynym
zadaniem jest odpowiedzieć na pytania z
[`docs/02-crosscomm-api.md` §12](../../docs/02-crosscomm-api.md), żeby agent
powstawał na faktach, a nie na wnioskach z kodu KukavarProxy.

## Status

⚠ **Kod nie został jeszcze skompilowany.** Powstał w środowisku Linux bez
toolchainu .NET; `net48` wymaga Windows. Pierwsza kompilacja w Visual Studio
niemal na pewno ujawni drobne błędy do poprawienia. Nie traktuj tego jako
zweryfikowanej binarki.

## Budowanie

```cmd
cd krc-monitor
dotnet build KrcMonitor.sln -c Release
```

albo otwórz `KrcMonitor.sln` w Visual Studio 2019+.

**Wymagania:** .NET Framework 4.8 Developer Pack (Targeting Pack).

**Platforma jest wymuszona na x86** i nie wolno tego zmieniać. `WBC_KrcLib` to
32-bitowy serwer COM in-process — proces 64-bitowy dostanie
`REGDB_E_CLASSNOTREG` mimo poprawnej rejestracji, bo 64-bitowy widok rejestru
nie zawiera wpisu.

## Bezpieczeństwo

Narzędzie **nie zapisuje żadnej zmiennej**, nie wybiera, nie startuje, nie
zatrzymuje ani nie anuluje programu, i nie resetuje magistrali I/O.

Pobranie wskaźnika na interfejs (`GetService`) jest operacją bezstanową —
to `QueryInterface`, nie dotyka stanu robota — więc `list-services` sprawdza
także `SyncSelect` i `SyncIo`, żeby zaraportować ich obecność. **Wywołanie
czegokolwiek na tych interfejsach nie jest zaimplementowane i nie wolno tego
dodawać.** Punkt egzekucyjny: `ServiceIds.MutatingMembers`.

## Komendy

### `env` — uruchom to jako pierwsze

```cmd
KrcMonitor.Probe.exe env
```

Raportuje host, wersję .NET, rejestrację CrossComm — i przede wszystkim
**czy pomiary czasowe z tej maszyny są reprezentatywne**.

To ostatnie jest istotne, bo faza 1 startuje na OfficeLite/VirtualKRC:

| Przenosi się na fizyczny kontroler | Nie przenosi się |
|---|---|
| Kształt API, sygnatury, IID-y | Koszt CPU/RAM |
| Limit `SetInfo` | Jitter callbacków |
| Formaty wartości `ShowVar` | Minimalny sensowny interwał |
| Czy callbacki STA w ogóle działają | Konkurencja ze smartHMI |

Uwaga o danych: na OfficeLite `$KR_SERIALNO` bywa pusty albo zawiera wartość
domyślną, a to on ma być tożsamością robota w certyfikacie mTLS
([`docs/06-protocol.md`](../../docs/06-protocol.md)). **Potwierdź na sprzęcie,
zanim zbudujesz na tym PKI.**

### `dump-typelib` — najważniejsza komenda

```cmd
KrcMonitor.Probe.exe dump-typelib
KrcMonitor.Probe.exe dump-typelib --filter SyncVar
KrcMonitor.Probe.exe dump-typelib --typelib "C:\Windows\SysWOW64\Cross3Krc.CIE"
```

Wypisuje rzeczywiste interfejsy, metody, IID-y i offsety vtable prosto
z biblioteki typów, przez `ITypeLib`/`ITypeInfo`. **Czyta wyłącznie metadane** —
nie tworzy żadnego obiektu CrossComm, więc nie może wpłynąć na kontroler.

Rozstrzyga dwie rzeczy, których nie da się ustalić z kodu KukavarProxy:

1. **Czy `ICKSyncVar` ma `ShowMultiVar` i jaka jest jej sygnatura.**
   `cCrossComm.cls` nigdy jej nie woła, ale `ICKCallbackVar` deklaruje
   `OnShowMultiVar` (`cCrossComm.cls:1025`) — callback nie istniałby bez metody.
   Odpowiedź decyduje, czy odczyt 24 zmiennych to 1 czy 24 roundtripy COM.

2. **IID-y `ICKCallbackVar` i `ICKConsumeMessage`.** Agent musi zaimplementować
   oba, a ręczna deklaracja `<ComImport>` wymaga dokładnego IID i dokładnej
   kolejności vtable.

### `list-services`

```cmd
KrcMonitor.Probe.exe list-services
```

Które serwisy `WBC_KrcLib` ten kontroler faktycznie udostępnia.

### `read`

```cmd
KrcMonitor.Probe.exe read $KR_SERIALNO $MODEL_NAME[] $POS_ACT $OV_PRO $MODE_OP
```

Pokazuje wartość surową **i** sparsowaną. Wartość surowa jest zawsze wypisywana
— parser degraduje do tekstu, nigdy do błędnej liczby.

### `read-multi`

```cmd
KrcMonitor.Probe.exe read-multi $POS_ACT $AXIS_ACT $OV_PRO
```

Próbuje kolejnych prawdopodobnych form wywołania `ShowMultiVar` i raportuje,
którą kontroler przyjął, plus porównanie z odczytem sekwencyjnym.

## Opcje

| Opcja | Domyślnie | Opis |
|---|---|---|
| `--client <nazwa>` | `KRCPROBE` | Nazwa klienta CrossComm |
| `--timeout <ms>` | `5000` | Timeout pojedynczego wywołania |
| `--typelib <ścieżka>` | (z rejestru) | Wczytaj `Cross3Krc.CIE` z pliku |
| `--filter <tekst>` | — | Zawęź wynik `dump-typelib` |

Nazwa `KUKAVARPROXY` jest **odrzucana** — kolizja z działającym KukavarProxy
zarejestrowałaby oba jako tego samego klienta CrossComm.

## Kody wyjścia

| Kod | Znaczenie |
|---|---|
| 0 | OK |
| 2 | Błąd użycia |
| 3 | CrossComm niedostępny |
| 4 | Wywołanie nie powiodło się |

Nadają się do skryptu inwentaryzacyjnego na flocie.

## Architektura

| Plik | Rola |
|---|---|
| `Interop/StaHost.vb` | **Wątek STA + pompka komunikatów.** Bez tego callbacki COM nigdy nie dotrą — cicho, bez błędu |
| `Interop/NativeMethods.vb` | P/Invoke, typy wyjątków, zwalnianie COM |
| `Interop/TypeLibInspector.vb` | Introspekcja `ITypeLib`/`ITypeInfo` |
| `CrossComm/CrossCommSession.vb` | Sesja late-bound, tylko odczyt |
| `Krl/KrlValueParser.vb` | Parser wartości KRL (invariant culture) |
| `Cli/CommandLine.vb` | Parser argumentów, bez zależności |
| `Diagnostics/EnvironmentInfo.vb` | Rozpoznanie host / VM / OfficeLite |

### Dlaczego late binding, a nie `tlbimp`

`tlbimp` wygeneruje interop assembly dla agenta produkcyjnego i tak zrobimy
w fazie 2. Ale Probe ma inne zadanie: musi działać na kontrolerze, gdzie
żadnego interopu nie wygenerowano, i ma **odkryć** API, a nie je założyć.

Ograniczenie, które trzeba nazwać wprost: **callbacków nie da się dostarczyć
do obiektu late-bound.** Implementacja `ICKCallbackVar` wymaga prawdziwego
interfejsu z właściwym IID i kolejnością vtable — i właśnie po to jest
`dump-typelib`.

To znaczy, że **subskrypcje `SetInfo` nie są jeszcze w Probe.** Są następnym
krokiem, po tym jak `dump-typelib` da IID-y na rzeczywistym kontrolerze.

## Kolejność uruchamiania w fazie 1

```cmd
REM 1. Czy w ogóle jesteśmy na właściwej maszynie
KrcMonitor.Probe.exe env

REM 2. Rzeczywiste API — zapisz wynik, to wejście do fazy 2
KrcMonitor.Probe.exe dump-typelib > typelib-officelite.txt

REM 3. Co kontroler udostępnia
KrcMonitor.Probe.exe list-services

REM 4. Formaty wartości
KrcMonitor.Probe.exe read $KR_SERIALNO $MODEL_NAME[] $POS_ACT $AXIS_ACT $OV_PRO $MODE_OP

REM 5. Czy da się czytać wsadowo
KrcMonitor.Probe.exe read-multi $POS_ACT $AXIS_ACT $OV_PRO
```

Powtórz komplet na maszynie fizycznej i **porównaj oba wyniki** — różnice są
same w sobie wynikiem fazy 1.
