# Konfiguracja agenta

## 1. Lokalizacja

```
C:\KrcMonitor\
├── KrcMonitor.Agent.exe
├── KrcMonitor.Agent.exe.config      .NET config (nie dotykać)
├── config.json                       ← konfiguracja agenta
├── certs\
│   ├── robot.pfx                     certyfikat klienta (CN = $KR_SERIALNO)
│   └── ca.crt                        firmowe CA do weryfikacji kolektora
└── DISABLED                          ← kill switch (obecność pliku = stop)
```

Katalog jest jedynym miejscem na kontrolerze, które agent tworzy. Deinstalacja
usuwa go w całości.

## 2. `config.json`

```json
{
  "version": 1,

  "identity": {
    "robot_id_source": "$KR_SERIALNO",
    "robot_id_override": null,
    "site": "PL-POZ",
    "line": "BODY-SHOP-3",
    "cell": "R042"
  },

  "crosscomm": {
    "client_name": "KRCMONITOR",
    "call_timeout_ms": 2000,
    "breaker_failure_threshold": 3,
    "breaker_cooldown_ms": 30000,
    "silence_timeout_ms": 60000,
    "reconnect_backoff_ms": [1000, 2000, 5000, 10000, 30000]
  },

  "static_reads": [
    "$KR_SERIALNO",
    "$MODEL_NAME[]"
  ],

  "subscriptions": [
    { "name": "$POS_ACT",     "interval_ms": 500,  "kind": "FRAME"  },
    { "name": "$AXIS_ACT",    "interval_ms": 500,  "kind": "E6AXIS" },
    { "name": "$OV_PRO",      "interval_ms": 1000, "kind": "INT"    },
    { "name": "$MODE_OP",     "interval_ms": 1000, "kind": "ENUM"   },
    { "name": "$PRO_STATE1",  "interval_ms": 1000, "kind": "ENUM"   },
    { "name": "$STOPMESS",    "interval_ms": 1000, "kind": "BOOL"   }
  ],

  "messages": {
    "enabled": true,
    "types": ["Info", "State", "Event", "Quitt", "Wait", "Dialog"]
  },

  "collector": {
    "host": "krcmon-collector.example.local",
    "port": 8443,
    "client_cert_path": "certs\\robot.pfx",
    "client_cert_password_env": "KRCMON_CERT_PW",
    "ca_cert_path": "certs\\ca.crt",
    "connect_timeout_ms": 10000,
    "reconnect_backoff_ms": [1000, 2000, 4000, 8000, 30000, 60000, 300000],
    "reconnect_jitter_pct": 20
  },

  "queue": {
    "max_items": 10000,
    "policy": "drop-oldest",
    "batch_max_items": 500,
    "batch_max_age_ms": 1000
  },

  "limits": {
    "max_memory_mb": 80,
    "max_cpu_pct_60s": 3.0,
    "max_samples_per_sec": 50,
    "on_breach": "restart-service"
  },

  "health": {
    "interval_ms": 60000
  },

  "killswitch": {
    "file_path": "C:\\KrcMonitor\\DISABLED",
    "check_interval_ms": 30000
  },

  "logging": {
    "eventlog_source": "KrcMonitor",
    "min_level": "Warning",
    "file_logging": false
  }
}
```

## 3. Walidacja przy starcie

Agent waliduje konfigurację **przed** nawiązaniem kontaktu z CrossComm. Każdy błąd
oznacza **odmowę startu serwisu** z czytelnym wpisem w Event Log — nie ma trybu
„startuj z częściową konfiguracją".

| Reguła | Skutek naruszenia |
|---|---|
| `subscriptions.length ≤ 75` | Odmowa startu — limit API CrossComm |
| `interval_ms ≥ 100` dla każdej subskrypcji | Odmowa startu |
| Brak duplikatów w `subscriptions[].name` | Odmowa startu |
| Nazwy zmiennych pasują do `^\$?[A-Za-z_][A-Za-z0-9_\[\]\.]*$` | Odmowa startu |
| `kind` z listy: `FRAME`, `E6AXIS`, `E6POS`, `INT`, `REAL`, `BOOL`, `ENUM`, `STRING`, `RAW` | Odmowa startu |
| Pliki certyfikatów istnieją i są czytelne | Odmowa startu |
| Certyfikat klienta ważny i nie wygasł | Ostrzeżenie; wygasły → odmowa startu |
| `max_memory_mb` w zakresie 20–200 | Odmowa startu |
| `queue.max_items` w zakresie 100–100 000 | Odmowa startu |
| `client_name` niepusty i różny od `KUKAVARPROXY` | Odmowa startu |

Ostatnia reguła zapobiega konfliktowi, gdyby na kontrolerze pracował jednocześnie
oryginalny KukavarProxy.

## 4. Zmiana konfiguracji

**Nie ma hot reloadu.** Zmiana konfiguracji wymaga restartu serwisu:

```cmd
sc stop KrcMonitor && sc start KrcMonitor
```

Uzasadnienie: przeładowanie w locie oznaczałoby przebudowę subskrypcji CrossComm
w trakcie pracy — dodatkowa ścieżka kodu, dodatkowa klasa błędów, a zysk żaden.
Restart serwisu trwa sekundy i nie ma wpływu na robota.

Konfiguracja jest dystrybuowana centralnie przez narzędzie deploymentu, nie
edytowana ręcznie na kontrolerze. `config.hash` w wiadomości `hello` pozwala
kolektorowi wykryć roboty z nieaktualną konfiguracją.

## 5. Profile konfiguracyjne

Zamiast unikatowej konfiguracji na każdy z 400 robotów — kilka profili:

| Profil | Zastosowanie | Zmienne | Interwał pozycji |
|---|---|---|---|
| `minimal` | Robot o ograniczonych zasobach | 6 | 2000 ms |
| `standard` | Domyślny dla floty | 24 | 500 ms |
| `detailed` | Roboty pod obserwacją / diagnostyka | 60 | 200 ms |
| `disabled` | Tymczasowo wyłączony | 0 | — |

Robot dostaje: profil + wstrzyknięte `identity` (site/line/cell) + własny certyfikat.
Nic więcej nie różni się między instalacjami.

> **Zasada operacyjna.** Profil `detailed` jest przeznaczony do czasowej diagnostyki
> pojedynczych robotów, nie do stałej pracy floty. Wdrożenie `detailed` na dużej
> grupie unieważnia budżety zasobów przyjęte w analizie ryzyka.

## 6. Zmienne środowiskowe

| Zmienna | Przeznaczenie |
|---|---|
| `KRCMON_CERT_PW` | Hasło do `robot.pfx` |
| `KRCMON_CONFIG` | Alternatywna ścieżka do `config.json` (tylko dev) |

Hasło certyfikatu **nie może** trafić do `config.json`. Ustawiane przy instalacji
jako zmienna środowiskowa serwisu.

## 7. Uprawnienia

Serwis pracuje na wydzielonym koncie lokalnym o minimalnych uprawnieniach:

| Uprawnienie | Potrzebne | Powód |
|---|---|---|
| Logowanie jako usługa | tak | uruchomienie serwisu |
| Odczyt `C:\KrcMonitor\` | tak | konfiguracja i certyfikaty |
| Zapis `C:\KrcMonitor\` | **nie** | agent nie zapisuje plików |
| Zapis do Event Log | tak | logowanie |
| Dostęp do COM (CrossComm) | tak | odczyt zmiennych |
| Uprawnienia administratora | **nie** | niepotrzebne w pracy ciągłej |
| Dostęp do sieci (wychodzący) | tak | połączenie z kolektorem |

Uprawnienia administratora są potrzebne wyłącznie **przy instalacji** (rejestracja
serwisu), nie w trakcie pracy.

## 8. Przykład: profil `minimal`

Dla kontrolerów z małym zapasem zasobów, wykrytych w fazie 0:

```json
{
  "version": 1,
  "identity": { "robot_id_source": "$KR_SERIALNO", "site": "PL-POZ", "line": "…", "cell": "…" },
  "crosscomm": { "client_name": "KRCMONITOR", "call_timeout_ms": 2000,
                 "breaker_failure_threshold": 3, "breaker_cooldown_ms": 60000,
                 "silence_timeout_ms": 120000, "reconnect_backoff_ms": [5000, 15000, 60000] },
  "static_reads": ["$KR_SERIALNO", "$MODEL_NAME[]"],
  "subscriptions": [
    { "name": "$MODE_OP",    "interval_ms": 5000, "kind": "ENUM" },
    { "name": "$PRO_STATE1", "interval_ms": 5000, "kind": "ENUM" },
    { "name": "$OV_PRO",     "interval_ms": 5000, "kind": "INT"  },
    { "name": "$STOPMESS",   "interval_ms": 2000, "kind": "BOOL" },
    { "name": "$POS_ACT",    "interval_ms": 2000, "kind": "FRAME" },
    { "name": "$ROB_TIMER",  "interval_ms": 60000, "kind": "REAL" }
  ],
  "messages": { "enabled": true, "types": ["State", "Event", "Quitt"] },
  "collector": { "host": "…", "port": 8443, "client_cert_path": "certs\\robot.pfx",
                 "client_cert_password_env": "KRCMON_CERT_PW", "ca_cert_path": "certs\\ca.crt",
                 "connect_timeout_ms": 15000,
                 "reconnect_backoff_ms": [5000, 15000, 60000, 300000], "reconnect_jitter_pct": 25 },
  "queue":  { "max_items": 2000, "policy": "drop-oldest", "batch_max_items": 200, "batch_max_age_ms": 5000 },
  "limits": { "max_memory_mb": 50, "max_cpu_pct_60s": 1.5, "max_samples_per_sec": 10,
              "on_breach": "restart-service" },
  "health": { "interval_ms": 300000 },
  "killswitch": { "file_path": "C:\\KrcMonitor\\DISABLED", "check_interval_ms": 30000 },
  "logging": { "eventlog_source": "KrcMonitor", "min_level": "Warning", "file_logging": false }
}
```
