# Wdrożenie na 400 robotów

Instalacja na jednym robocie jest łatwa. Instalacja na 400 to inny problem —
i to on decyduje o powodzeniu projektu.

## 1. Zasady

| # | Zasada | Konsekwencja |
|---|---|---|
| D1 | Zero ręcznej pracy przy robocie | Wszystko skryptowalne i zdalne |
| D2 | Każda operacja idempotentna | Ponowne uruchomienie nie psuje stanu |
| D3 | Rollback zawsze dostępny | Deinstalacja tak samo zautomatyzowana jak instalacja |
| D4 | Etapowo, z bramkami | 10 → 50 → 150 → 400 |
| D5 | Konfiguracja centralna | Robot nie ma unikatowej konfiguracji poza tożsamością |
| D6 | Deployment nigdy w trakcie produkcji | Okna serwisowe albo postoje |

## 2. Wybór narzędzia

Do ustalenia w fazie 0. Opcje w kolejności preferencji:

| Narzędzie | Zalety | Wady |
|---|---|---|
| **Ansible** (WinRM) | Deklaratywne, idempotentne, dobry raport, bez agenta | Wymaga WinRM na kontrolerach |
| **SCCM / Intune** | Prawdopodobnie już jest w VW | Ciężkie, wolne, mniejsza kontrola |
| **PowerShell DSC** | Natywne dla Windows | Bardziej złożone niż Ansible |
| **Skrypt + SMB + `sc.exe`** | Działa wszędzie, zero infrastruktury | Ręczna obsługa błędów i raportowania |

Jeśli WinRM jest dostępny — **Ansible**. Jeśli nie i SCCM już obsługuje kontrolery —
SCCM. Skrypt SMB to ostateczność, ale przy 400 maszynach jest wykonalny.

## 3. Kroki instalacji na jednym robocie

Sekwencja, którą narzędzie deploymentu wykonuje na każdym kontrolerze:

```
 1. Sprawdź warunki wstępne
    ├── Windows Embedded Standard 7 lub nowszy
    ├── .NET Framework ≥ 4.8  (rejestr: NDP\v4\Full → Release ≥ 528040)
    ├── TypeLib {307230E0-B48F-11D4-B053-00A0D21AFA30} zarejestrowany
    ├── ≥ 200 MB wolnego RAM
    └── ≥ 500 MB wolnego dysku
       ✗ dowolny brak → PRZERWIJ, zaraportuj, nie instaluj

 2. Odczytaj $KR_SERIALNO
    └── przez KrcMonitor.Probe --read $KR_SERIALNO
       ✗ brak odczytu → PRZERWIJ (CrossComm nie działa)

 3. Sprawdź, czy certyfikat dla tego serialu istnieje w PKI
    ✗ brak → wystaw (patrz §4) albo PRZERWIJ

 4. Utwórz C:\KrcMonitor\ i skopiuj:
    ├── binarki (podpisane)
    ├── config.json (profil + wstrzyknięta tożsamość)
    └── certs\ (robot.pfx, ca.crt)

 5. Ustaw ACL: konto serwisu = odczyt; brak zapisu

 6. Utwórz konto serwisu i nadaj "Log on as a service"

 7. Ustaw zmienną środowiskową serwisu KRCMON_CERT_PW

 8. Zarejestruj serwis
    sc create KrcMonitor binPath= "C:\KrcMonitor\KrcMonitor.Agent.exe" start= auto
    sc failure KrcMonitor reset= 86400 actions= restart/60000/restart/60000/restart/60000

 9. Zarejestruj źródło Event Log

10. Uruchom serwis, odczekaj 60 s

11. Weryfikacja
    ├── serwis w stanie RUNNING
    ├── kolektor odnotował 'hello' z tym $KR_SERIALNO
    ├── kolektor odnotował ≥ 1 'sample'
    └── brak błędów w Event Log
       ✗ dowolny brak → ROLLBACK i zaraportuj
```

Krok 11 jest obowiązkowy. Deployment, który nie weryfikuje skutku, przy 400
maszynach zostawia dziesiątki cicho niedziałających instalacji.

## 4. PKI i provisioning certyfikatów

### Wymagania

- Wewnętrzne CA (AD CS, HashiCorp Vault, step-ca — do wyboru w fazie 0)
- Certyfikat per robot, `CN = <$KR_SERIALNO>`
- Ważność 2 lata, procedura odnawiania **ustalona przed rolloutem**
- Klucz prywatny generowany na kontrolerze albo dostarczany w `.pfx` z hasłem

### Krytyczna kwestia: odnawianie

400 certyfikatów z tą samą datą ważności wygaśnie **tego samego dnia**, dwa lata po
rolloucie. To jest gwarantowana awaria całej floty, wpisana w harmonogram od dziś.

Mitygacja — jedno z dwóch:
1. **Rozłóż daty ważności** — losowy offset ±90 dni przy wystawianiu
2. **Zautomatyzuj odnawianie** — zadanie sprawdzające i odnawiające na 30 dni przed

Rekomendacja: **oba**. Rozłożenie zamienia awarię floty w strumień pojedynczych
przypadków; automatyzacja sprawia, że i tych nie ma.

Kolektor musi alarmować o certyfikatach wygasających w ciągu 60 dni.

## 5. Etapowy rollout

| Etap | Roboty | Obserwacja | Bramka do następnego etapu |
|---|---|---|---|
| R1 | 10, jedna linia | 1 tydz. | Zero incydentów, 100% raportuje |
| R2 | 50, dwie linie | 1 tydz. | Kolektor < 50% budżetu zasobów |
| R3 | 150 | 2 tyg. | Baza i retencja wydolne, brak degradacji linii |
| R4 | 400 | 2 tyg. | Pełna flota stabilna |

Po każdym etapie formalna decyzja go/no-go. Kryterium nadrzędne przez cały czas:

> Czy linie z agentem stoją częściej niż linie bez agenta?

## 6. Kill switch — procedura operacyjna

### Poziom 1: pojedynczy robot (≤ 1 min)

```cmd
echo. > \\ROBOT-042\C$\KrcMonitor\DISABLED
```

Agent wykrywa flagę w ≤ 30 s, wyrejestrowuje subskrypcje, przechodzi w idle.
Wznowienie: usuń plik.

### Poziom 2: grupa lub flota (≤ 10 min)

```
ansible-playbook stop-agents.yml --limit line_bodyshop3
ansible-playbook stop-agents.yml            # cała flota
```

### Poziom 3: deinstalacja (≤ 30 min dla floty)

```
ansible-playbook uninstall-agents.yml
```

Usuwa serwis, katalog `C:\KrcMonitor\`, konto serwisu, źródło Event Log.
Kontroler wraca do stanu sprzed instalacji.

**Utrzymanie musi znać poziom 1 przed rozpoczęciem pilota.** To pierwsza reakcja
na jakiekolwiek podejrzenie, że agent wpływa na robota.

## 7. Aktualizacje agenta

Bez self-update. Aktualizacja to ten sam mechanizm co instalacja:

```
1. Zatrzymaj serwis
2. Podmień binarki
3. Uruchom serwis
4. Zweryfikuj 'hello' z nową wersją w kolektorze
5. Przy niepowodzeniu: przywróć poprzednią wersję
```

Aktualizacje etapowo, jak rollout — nigdy na 400 maszyn naraz.

Uzasadnienie odrzucenia self-update: to nietestowalna ścieżka kodu, która przy
awarii pozostawia 400 maszyn w nieznanym stanie, bez możliwości zdalnej naprawy.
Deployment przez narzędzie jest wolniejszy, ale audytowalny i odwracalny.

## 8. Monitoring samego wdrożenia

Kolektor prowadzi rejestr floty. Dashboard „zdrowie wdrożenia" musi pokazywać:

| Metryka | Alarm |
|---|---|
| Roboty raportujące / zainstalowane | < 98% |
| Roboty milczące > 5 min | dowolny |
| Roboty z `breaker_open = true` | dowolny |
| Roboty z rosnącym `dropped_total` | trend rosnący |
| Roboty z `mem_mb` rosnącym w czasie | trend rosnący (wyciek) |
| Roboty ze starą wersją agenta | po zakończeniu aktualizacji |
| Roboty ze starym `config.hash` | po zakończeniu dystrybucji |
| Certyfikaty wygasające < 60 dni | dowolny |

Bez tego dashboardu nie wiadomo, że 40 robotów cicho przestało raportować.
**Musi powstać przed etapem R2.**

## 9. Dokumentacja operacyjna do przekazania

Przed zakończeniem projektu utrzymanie dostaje:

- [ ] Procedura kill switch, wszystkie trzy poziomy
- [ ] Procedura instalacji na nowym robocie (wymiana kontrolera)
- [ ] Procedura deinstalacji
- [ ] Procedura odnawiania certyfikatu
- [ ] Interpretacja wpisów Event Log
- [ ] Kontakt eskalacyjny i zakres odpowiedzialności
- [ ] Diagram: co robić, gdy podejrzewasz, że agent wpływa na robota

Ostatni punkt jest najważniejszy. Musi być jednostronicowy i zaczynać się od
kill switcha poziomu 1.
