# Protokół agent ↔ kolektor

**Status:** projekt, do zamrożenia w fazie 3.

## 1. Zasady

| # | Zasada | Uzasadnienie |
|---|---|---|
| P1 | **Jednokierunkowy.** Agent wysyła, kolektor nie wydaje poleceń. | Brak kanału sterującego = brak wektora ataku na robota |
| P2 | Połączenie inicjuje agent | Zero portów nasłuchowych na robocie |
| P3 | Wersjonowany od pierwszego dnia | 400 robotów nie zaktualizuje się jednocześnie |
| P4 | Samoopisujący się | Kolektor nie musi znać konfiguracji agenta |
| P5 | Odporny na utratę | Utrata próbek jest akceptowalna; blokowanie agenta nie |

> P1 jest zasadą bezpieczeństwa, nie wygody. Każda przyszła propozycja „a gdyby
> kolektor mógł poprosić agenta o..." otwiera dokładnie ten wektor, który ten
> projekt eliminuje. Odpowiedź brzmi: zmiana konfiguracji i redeploy.

## 2. Warstwa transportowa

| Właściwość | Wartość |
|---|---|
| Protokół | TCP + TLS 1.2 minimum, preferowane 1.3 |
| Port | 8443 (konfigurowalny) |
| Uwierzytelnianie | Obustronne (mTLS) |
| Certyfikat klienta | Per robot, `CN = <$KR_SERIALNO>` |
| Weryfikacja serwera | Pełna, przypięte firmowe CA |
| Keepalive | TCP keepalive 30 s + heartbeat aplikacyjny 60 s |
| Reconnect | Wykładniczy backoff: 1 s → 2 → 4 → … → 300 s, z jitterem |

**Jitter jest obowiązkowy.** Bez niego 400 agentów po restarcie kolektora uderzy
w niego jednocześnie.

## 3. Format ramki

```
┌────────────┬────────────┬──────────────────────┐
│ magic  4 B │ length 4 B │ payload (length B)   │
│ "KRCM"     │ uint32 BE  │ UTF-8 JSON Lines     │
└────────────┴────────────┴──────────────────────┘
```

Maksymalny `length`: 1 MiB. Ramka większa → kolektor zrywa połączenie i loguje incydent.

> **Decyzja otwarta.** JSON Lines wybrano na start ze względu na debugowalność.
> Jeśli pomiar w fazie 3 pokaże, że wolumen z 400 robotów jest problemem, przejście
> na Protobuf jest ścieżką awaryjną — dlatego pole `v` w każdej wiadomości.

## 4. Typy wiadomości

Każda wiadomość to jeden obiekt JSON w linii, z polami wspólnymi:

| Pole | Typ | Opis |
|---|---|---|
| `v` | int | Wersja protokołu (obecnie `1`) |
| `t` | string | Typ wiadomości |
| `ts` | string | Znacznik czasu ISO 8601 UTC z milisekundami |
| `rid` | string | Identyfikator robota (`$KR_SERIALNO`) |

### 4.1 `hello` — pierwsza wiadomość po nawiązaniu połączenia

```json
{
  "v": 1,
  "t": "hello",
  "ts": "2026-08-14T09:12:33.482Z",
  "rid": "1234567",
  "agent": { "version": "1.0.0", "build": "2026-08-01" },
  "robot": {
    "model": "KR 210 R2700 extra",
    "kss": "8.3.29",
    "controller": "KRC4",
    "host": "ROBOT-042"
  },
  "config": { "hash": "sha256:a1b2c3…", "variables": 24, "interval_min_ms": 200 }
}
```

Kolektor weryfikuje, że `rid` zgadza się z CN certyfikatu. **Niezgodność →
natychmiastowe rozłączenie i alert.** To jest zabezpieczenie przed podszywaniem się
robota pod inny robot.

### 4.2 `sample` — próbka zmiennej

```json
{
  "v": 1,
  "t": "sample",
  "ts": "2026-08-14T09:12:34.102Z",
  "rid": "1234567",
  "name": "$POS_ACT",
  "raw": "{X 1234.5, Y 234.1, Z 987.0, A 0.0, B 90.0, C 0.0, S 2, T 10}",
  "parsed": { "X": 1234.5, "Y": 234.1, "Z": 987.0, "A": 0.0, "B": 90.0, "C": 0.0, "S": 2, "T": 10 },
  "kind": "FRAME"
}
```

`raw` jest zawsze wysyłane. `parsed` tylko gdy parser rozpoznał typ — przy nieznanym
formacie kolektor dostaje surową wartość i nic nie ginie.

**Batching:** wiele `sample` w jednej ramce, oddzielonych `\n`. Agent grupuje co 1 s
albo co 500 pozycji, zależnie co nastąpi pierwsze.

### 4.3 `message` — komunikat robota z `AdviseMessage`

```json
{
  "v": 1,
  "t": "message",
  "ts": "2026-08-14T09:12:35.771Z",
  "rid": "1234567",
  "ctrl_ts": "2026-08-14T09:12:35.750Z",
  "msg_type": "Quitt",
  "number": 1422,
  "handle": 88231,
  "module": "R1",
  "text": "Napęd nie gotowy",
  "cause": "…",
  "params": ["A3", "12"]
}
```

`ctrl_ts` to znacznik czasu z kontrolera (z `nHighTimeStamp`/`nLowTimeStamp`), `ts` to
czas agenta. Rozbieżność między nimi jest sama w sobie użyteczną metryką —
ujawnia rozjazd zegarów na flocie.

### 4.4 `health` — telemetria agenta, co 60 s

```json
{
  "v": 1,
  "t": "health",
  "ts": "2026-08-14T09:13:00.000Z",
  "rid": "1234567",
  "uptime_s": 84321,
  "mem_mb": 42.1,
  "cpu_pct": 0.8,
  "queue_depth": 12,
  "dropped_total": 0,
  "com_errors_total": 3,
  "com_timeouts_total": 1,
  "breaker_open": false,
  "subscriptions_active": 24,
  "resubscribes_total": 1
}
```

To jest strumień, który wykrywa cichą degradację floty. `dropped_total` rosnący
u jednego robota oznacza problem z siecią; rosnący u wszystkich — problem z kolektorem.

### 4.5 `bye` — kontrolowane zamknięcie

```json
{ "v": 1, "t": "bye", "ts": "…", "rid": "1234567", "reason": "service_stop" }
```

Dopuszczalne `reason`: `service_stop`, `kill_switch`, `config_reload`, `watchdog_restart`.

Brak `bye` przed rozłączeniem oznacza awarię — kolektor odnotowuje to jako zdarzenie.

## 5. Zachowanie kolektora

| Sytuacja | Reakcja |
|---|---|
| CN certyfikatu nieznany | Rozłączenie, alert bezpieczeństwa |
| `rid` ≠ CN | Rozłączenie, alert bezpieczeństwa |
| Nieznana wersja `v` | Akceptuj znane pola, zaloguj ostrzeżenie |
| Nieznany typ `t` | Zignoruj wiadomość, zaloguj, **nie rozłączaj** |
| Ramka > 1 MiB | Rozłączenie, log |
| Przekroczony rate limit | Throttling, log; przy uporczywym — rozłączenie |
| Brak `health` przez 5 min | Oznacz robota jako milczącego, alert |

Reguła: **kolektor nigdy nie odsyła nic, co agent traktowałby jako polecenie.**
Jedyna informacja zwrotna to zamknięcie połączenia TCP.

## 6. Rate limity

| Limit | Wartość | Egzekwuje |
|---|---|---|
| Próbki na robota | 50/s | agent i kolektor |
| Ramki na robota | 5/s | agent |
| Bajty na robota | 100 KiB/s | kolektor |
| Połączenia z jednego CN | 1 | kolektor |

Limity po obu stronach celowo. Agent ogranicza się sam, żeby nie zalać sieci;
kolektor egzekwuje, żeby uszkodzony agent nie zaszkodził reszcie floty.

## 7. Schemat bazy (szkic)

```sql
CREATE TABLE samples (
    ts          TIMESTAMPTZ      NOT NULL,
    robot_id    TEXT             NOT NULL,
    var_name    TEXT             NOT NULL,
    raw         TEXT,
    parsed      JSONB,
    kind        TEXT
);
SELECT create_hypertable('samples', 'ts');
CREATE INDEX ON samples (robot_id, var_name, ts DESC);

CREATE TABLE robot_messages (
    ts          TIMESTAMPTZ      NOT NULL,
    ctrl_ts     TIMESTAMPTZ,
    robot_id    TEXT             NOT NULL,
    msg_type    TEXT,
    number      INTEGER,
    module      TEXT,
    text        TEXT,
    cause       TEXT,
    params      JSONB
);
SELECT create_hypertable('robot_messages', 'ts');

CREATE TABLE agent_health (
    ts          TIMESTAMPTZ      NOT NULL,
    robot_id    TEXT             NOT NULL,
    uptime_s    BIGINT,
    mem_mb      REAL,
    cpu_pct     REAL,
    queue_depth INTEGER,
    dropped_total BIGINT,
    com_errors_total BIGINT,
    breaker_open BOOLEAN
);
SELECT create_hypertable('agent_health', 'ts');
```

### Szacunek wolumenu

400 robotów × 24 zmienne × 5 próbek/s ≈ **48 000 próbek/s**, czyli ~4,1 mld rekordów
na dobę. To **za dużo** dla naiwnego zapisu.

Konieczne przed produkcją:
- agregacja ciągła (`continuous aggregates`) do 1 s / 10 s / 1 min
- retencja surowych próbek: 7 dni, agregatów: 2 lata
- kompresja TimescaleDB na partycjach starszych niż 1 dzień
- **rewizja interwałów** — czy `$POS_ACT` co 200 ms jest naprawdę potrzebne, czy
  wystarczy 1 s?

To jest ryzyko **R-06** z planu wdrożenia i wymaga rozstrzygnięcia w fazie 3,
przed rolloutem.

## 8. Wersjonowanie

Pole `v` rośnie tylko przy zmianach niekompatybilnych. Dodanie nowego pola do
istniejącej wiadomości albo nowego typu `t` **nie** podbija wersji — kolektor
ignoruje nieznane pola i typy.

Kolektor musi obsługiwać dwie kolejne wersje protokołu jednocześnie, bo podczas
rolloutu na 400 robotach flota zawsze jest niejednorodna.
