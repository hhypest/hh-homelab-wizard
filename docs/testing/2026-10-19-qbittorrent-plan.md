# Протокол: конкретный read-only план qBittorrent

Дата: 19.10.2026. База: `5219987c9dfd89fb0cd5249a090d724d7f2b918d` (`main`, merge PR #120). Частичный #50, родитель #20, зависимости #47/#49; подготовка #101. Решение: [ADR 0021](../adr/0021-qbittorrent-installation-plan.md). Точный head и CI фиксируются в PR.

## Среда и команды

Ubuntu 24.04 x64, .NET SDK 10.0.401 / runtime 10.0.12. Версии пакетов, lock-файлы, source/image pins не менялись. Чтения NAS представлены fixtures; библиотека инфраструктуры использует встроенный versioned template. Credentials и настоящие адреса NAS не требуются.

```text
dotnet restore HhHomelab.Wizard.slnx --locked-mode -m:1 -p:BuildInParallel=false
dotnet build HhHomelab.Wizard.slnx --no-restore -c Release -m:1 -p:BuildInParallel=false
dotnet test HhHomelab.Wizard.slnx --no-build -c Release -m:1 -p:BuildInParallel=false
dotnet run --project tools/HhHomelab.Wizard.JournalProbe --no-build -c Release
dotnet run --project tools/HhHomelab.Wizard.ProfileCheck --no-build -c Release -- profiles/hh-homelab/1.3.0/research-profile.json
```

Locked restore и Release build прошли, 0 warnings / 0 errors. Полный набор: **1091 unit/fixture** (45 новых plan cases), **18 Desktop tests**, без пропусков. JournalProbe: `journal.probe.restart-and-exclusive-lock.passed`, `journal.probe.plan-recovery.passed`, `journal.probe.passed`. ProfileCheck: 9 services / 19 artifacts / 118 steps, research inventory only. Последовательный MSBuild — параметр ограниченной локальной среды, CI не менялся.

## Сценарии

| Проверка | Результат |
| --- | --- |
| Явные пути, порты и budgets | Конкретные каталоги/файлы/контейнер/сеть; медиатека PreserveOrCreate без присвоения owner |
| BIND_ADDR на все интерфейсы / конкретный NAS / loopback | Раздельные PC, HA host network и Compose адреса; host WebUI port отличается от внутреннего 9080; loopback блокирует PC без proxy |
| Данные и конфигурация на одном/разных томах | Бюджеты складываются только на одном томе, каждый нужный том проверяется |
| TCP listener / непустой Docker / нехватка места / redirected path / чужой marker / данные без marker | Blocking остаётся в preview |
| Перепроверка listener/path/space-below-budget/owner | Оба порта вызываются заново, прежний fingerprint инвалидируется |
| Свежие timestamps и свободное место выше бюджета | Новый snapshot, прежний fingerprint; нет ложного drift |
| Host/path key, profile, principal, layout mismatch | Conflict/IdentityMismatch, плана нет; при host mismatch пути не читаются |
| Неподтверждённый endpoint | Отказ до обоих remote reads |
| Старый host / path раньше host / path из будущего | Conflict/ObservationChanged |
| Ошибка перепроверки | Старый план не выдаётся как свежий, Unchanged=false |
| Auth / deadline / invalid response | Типизированные безопасные коды, без исходного exception text |
| Caller cancellation / второй запуск | Cancelled / OperationInProgress, лишнее чтение путей не начинается; следующий запуск разрешён |
| Изменённый шаблон при том же research profile | Fingerprint меняется; мутация исходного массива mods не меняет сохранённый профиль |
| Неверные schema/source commit/compose hash/unknown field | Встроенный template parser отклоняет вход |
| Неверный адрес/bind/ports/network/root UID/UMASK/TZ/overflow бюджета | Отказ до чтений |

Проверены Markdown-relative links и `git diff --check` перед публикацией. Результат Windows/Linux CI относится к точному head в PR и не подменяет этот локальный запуск.

## Ограничения

- Нет runtime вызова compiler через живой SSH/DSM, Docker daemon локально отсутствует. SSH/Docker/HA/native GUI из существующей CI-матрицы оцениваются отдельно в PR.
- UDP, DSM ACL/PUID/PGID, совместимость версий, конфигурационные дочерние пути/базы, Docker storage, сеть, TZ и достижимость адресов остаются явно непроверенными. Совпавший fingerprint не разрешает Apply.
- Нет UI wiring, Compose renderer/delivery или конвертации preview в исполняемый граф. Полный состав v1 и #50/#101/M2 не приняты. NAS не подключался и не изменялся.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
