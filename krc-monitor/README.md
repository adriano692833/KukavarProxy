# KrcMonitor

Read-only agent telemetryczny dla kontrolerów KUKA KR C2/KR C4, oparty o natywne API
**CrossComm (`WBC_KrcLib`)**. Zbiera zmienne robota i komunikaty systemowe, wypycha je
wychodzącym połączeniem TLS do centralnego kolektora.

**Status:** faza projektowa — dokumentacja i plan. Kod jeszcze nie powstał.

---

## Po co to powstało

Monitoring floty ~400 robotów KUKA bez płacenia za opcje licencyjne:

| Rozwiązanie | Koszt dla 400 robotów | Werdykt |
|---|---|---|
| KUKA.OPC UA Server | ~1 600 000 PLN (~4000 PLN/robot) | odrzucone — koszt |
| KUKA.EthernetKRL | licencja niedostępna na flocie | odrzucone — brak licencji |
| KukavarProxy (VB6) | 0 PLN | działa, ale brak auth, otwarty port 7000, VB6 |
| **KrcMonitor** | **0 PLN** | ten projekt |

CrossComm jest częścią bazowego KSS i nie wymaga dodatkowej licencji — to ten sam
mechanizm, którego używa smartHMI i KukavarProxy.

## Czym różni się od KukavarProxy

KukavarProxy używa ~5% możliwości API CrossComm. KrcMonitor wykorzystuje trzy
mechanizmy, których tamten w ogóle nie dotyka:

| Mechanizm | KukavarProxy | KrcMonitor |
|---|---|---|
| Odczyt zmiennych | `ShowVar` w pętli, 1 wywołanie COM na zmienną | `ShowMultiVar` — N zmiennych jednym wywołaniem |
| Model odświeżania | polling przez klienta po TCP | `SetInfo` — **subskrypcja push** z callbackiem |
| Alarmy / komunikaty robota | ignorowane | `AdviseMessage` — pełny strumień `TKMessage` |
| Model sieciowy | serwer nasłuchujący na 0.0.0.0:7000 | **klient wychodzący**, zero otwartych portów |
| Uwierzytelnianie | brak | mTLS, certyfikat per robot |
| Zapis zmiennych | tak (`SetVar`) | **niemożliwy — brak w kodzie** |
| Język | VB6 | VB.NET (.NET Framework 4.8) |

Szczegóły API: [`docs/02-crosscomm-api.md`](docs/02-crosscomm-api.md)

## Architektura w jednym obrazku

```
┌──────────────────────────────────────────┐
│ Kontroler KR C4 (Windows Embedded Std 7) │
│                                          │
│  KrcMonitor.Agent  (Windows Service)     │
│   ├── wątek STA + message pump           │
│   │     ├── ICKAsyncVar.SetInfo   ───────┼──► CrossComm ──► KRL runtime
│   │     └── ICKAdviseMessage.Advise ─────┼──► CrossComm ──► komunikaty
│   ├── bounded queue (drop-oldest)        │
│   └── klient mTLS (wychodzący) ──────────┼──┐
└──────────────────────────────────────────┘  │
                                              │ TLS 1.2, port 8443
                    ┌─────────────────────────▼──────────┐
                    │ KrcMonitor.Collector               │
                    │  weryfikacja cert → walidacja →    │
                    │  zapis do TimescaleDB              │
                    └─────────────────┬──────────────────┘
                                      │
                                 ┌────▼────┐
                                 │ Grafana │
                                 └─────────┘
```

Pełny opis: [`docs/01-architecture.md`](docs/01-architecture.md)

## Dokumentacja

| Dokument | Zawartość |
|---|---|
| [01-architecture.md](docs/01-architecture.md) | Komponenty, przepływ danych, decyzje projektowe |
| [02-crosscomm-api.md](docs/02-crosscomm-api.md) | **Referencja API `WBC_KrcLib`** — odtworzona z `cCrossComm.cls` |
| [03-implementation-plan.md](docs/03-implementation-plan.md) | Plan fazowy, kamienie milowe, kryteria akceptacji |
| [04-risk-and-safety.md](docs/04-risk-and-safety.md) | Analiza ryzyka dla produkcji, wymagana do zatwierdzenia |
| [05-deployment-400.md](docs/05-deployment-400.md) | Rollout na 400 robotów, PKI, kill switch |
| [06-protocol.md](docs/06-protocol.md) | Protokół agent ↔ kolektor |
| [07-configuration.md](docs/07-configuration.md) | Schemat konfiguracji agenta |

## Zasady projektu (nienegocjowalne)

1. **Read-only.** Interfejsy `ICKSyncSelect` i `ICKSyncIo` nie są referencowane w kodzie
   agenta. Nie da się przez agenta zatrzymać programu, wystartować go ani zresetować
   magistrali — nie ma takiej ścieżki kodu do włączenia flagą.
2. **Zero portów nasłuchowych.** Agent tylko łączy się na zewnątrz.
3. **Allowlist zmiennych.** Agent czyta wyłącznie zmienne z konfiguracji. Nie istnieje
   tryb „czytaj dowolną zmienną na żądanie".
4. **Ograniczone zasoby.** Twarde limity RAM/CPU/kolejki, egzekwowane przez agenta,
   z self-restartem po przekroczeniu.
5. **Produkcja przede wszystkim.** Przy każdym wyborze projektowym, w którym stoi
   „więcej danych" przeciw „mniejsze ryzyko dla robota", wygrywa mniejsze ryzyko.

## Wymagania

**Kontroler (target):**
- KUKA KR C2 lub KR C4, KSS 5.x / 8.x
- Windows XPe / Windows Embedded Standard 7
- .NET Framework 4.8 (lub 4.6.2 — patrz plan, faza 0)
- Zarejestrowany `Cross3Krc.CIE` (KUKA Cross KRC Library, TypeLib `{307230E0-B48F-11D4-B053-00A0D21AFA30}` v1.5)

**Maszyna deweloperska:**
- Visual Studio 2019+ z obsługą VB.NET
- Windows SDK (`tlbimp.exe`)
- Kopia `Cross3Krc.CIE` do wygenerowania interop assembly

**Kolektor:**
- .NET 8 (Windows Server lub Linux)
- TimescaleDB / PostgreSQL 14+
- Grafana 10+

## Szybki start

Kod jeszcze nie istnieje. Pierwszy krok wykonawczy to **faza 0** z
[planu wdrożenia](docs/03-implementation-plan.md) — inwentaryzacja floty i weryfikacja
CrossComm na jednym robocie testowym. Nie pisz kodu, zanim faza 0 nie potwierdzi, że
CrossComm faktycznie odpowiada na Waszych kontrolerach.

## Licencja

Do ustalenia przez właściciela projektu.

## Pochodzenie

Referencja API CrossComm została odtworzona z klasy `cCrossComm.cls` projektu
[KukavarProxy](https://github.com/lionpeloux/KukavarProxy) (IMTS s.r.l., wydane jako
open source w styczniu 2019, rozszerzone przez Lionela du Peloux). KrcMonitor nie
zawiera kodu KukavarProxy — korzysta wyłącznie z wiedzy o publicznym API COM firmy KUKA.
