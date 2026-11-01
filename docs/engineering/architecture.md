# Архитектура и границы

Тип: действующая справка; устройство кода, зависимости слоёв и вход к решениям и доказательствам реализованных механизмов.

[Документация](../README.md) · [Разработка](../development.md) · [Реестр ADR](../adr/README.md) · [Стиль кода](code-style.md) · [Текущая готовность](../../ROADMAP.md)

Сверено 01.11.2026 по `main` d5ca91d. Карта описывает существующую реализацию; требования будущих этапов — в [плане](../plans/development-plan.md#4-архитектура-и-будущая-структура), причины решений — в [ADR](../adr/README.md). Наличие механизма в коде не означает приёмку на настоящем NAS: уровень доказательства указан в протоколе каждого инкремента.

## Слои и проекты

| Проект | Ответственность | Зависит от |
| --- | --- | --- |
| `src/HhHomelab.Wizard.Domain` | Модели и чистые правила: план, операции, preflight, пути, владение, профиль | — |
| `src/HhHomelab.Wizard.Application` | Сценарии и порты (`ISshConnectionProbe`, `INasPreflightProbe`, `INasPathProbe`, `INasDockerNetworkProbe`, порты конфигурационных операций) | Domain |
| `src/HhHomelab.Wizard.Infrastructure` | Адаптеры: SSH.NET, фиксированные NAS-скрипты `nas-*.sh`, HTTP-клиенты контрактов Media/HA, файловый журнал, закреплённый профиль | Application, Domain, SSH.NET |
| `src/HhHomelab.Wizard.Desktop` | Avalonia Views/ViewModels, тексты UI, отображение ErrorCode→действие, composition root | Application, Infrastructure, Avalonia, CommunityToolkit.Mvvm |
| `tests/HhHomelab.Wizard.UnitTests` | Unit/fixture/contract-тесты Domain, Application и Infrastructure | Infrastructure |
| `tests/HhHomelab.Wizard.DesktopTests` | Headless-проверки Desktop | Desktop, Avalonia.Headless |
| `tools/` | [Инструменты разработчика](developer-probes.md), ProfileCheck и DocsCheck; не входят в пользовательский пакет | по инструменту |
| `profiles/hh-homelab/1.3.0` | Машинный исследовательский профиль, план qBittorrent и lock компонента Monitor Docker | — |

Domain/Application не зависят от Avalonia, SSH/HTTP реализации. Pins, API contracts, роли и transforms берутся из версионированного профиля ([ADR 0009](../adr/0009-pinned-runtime-profile.md)); изменение профиля не подхватывается посреди сессии. Обязательные правила слоёв, MVVM, внешних операций и секретов — в [AGENTS](../../AGENTS.md#архитектура-и-качество); оформление кода — в [стиле кода](code-style.md).

## Движок операций

Операции разделяют Observe / Apply / Verify ([ADR 0010](../adr/0010-operation-boundaries.md)). Durable intent принадлежит журналу, session auth state — короткоживущей сессии. Timeout, обрыв или отмена после отправки означают неизвестный исход: следующий шаг — Observe, а не повтор Apply ([ADR 0002](../adr/0002-journal-and-secrets.md)). Конфигурационные операции объединяются в неизменяемый граф с зависимостями и бюджетами, закреплённый в журнале ([ADR 0020](../adr/0020-configuration-plan-recovery.md)).

## Реализованные механизмы и доказательства

Каждая строка ведёт к решению и датированному протоколу; ограничения в конце строки сохраняют границу доказательства.

### Подключение, preflight и план (M2)

- [Выбор SSH.NET и read-only порт подключения](../adr/0012-ssh-connection-probe.md), [fixture/TCP протокол](../verification/reports/2026-10-18-ssh-probe.md) и [одноразовый OpenSSH-стенд](../verification/reports/2026-10-19-ssh-runtime.md). Аппаратная DSM проверяется отдельно.
- [Read-only preflight inventory](../adr/0013-read-only-nas-preflight.md) и [его протокол](../verification/reports/2026-10-19-nas-preflight.md): snapshot фактов не заменяет проверку совместимости, путей и ownership.
- [Настоящий Docker/Compose через SSH в CI](../verification/reports/2026-10-19-docker-preflight.md), [граница изоляции стенда](../adr/0014-docker-preflight-runtime.md). Новый privileged режим предназначен только для одноразовой GitHub-hosted VM.
- [Модель NAS, слушающие порты и место томов](../adr/0015-preflight-host-resources.md) в preflight и [протокол](../verification/reports/2026-10-19-preflight-resources.md): Passed портов/места возможен только относительно требований плана.
- [Read-only проверка путей](../adr/0016-remote-path-inspection.md) и [протокол](../verification/reports/2026-10-19-remote-paths.md): symlink, границы тома и hardlink-устройство; DSM ACL не заявляются.
- [Числовая identity SSH и PUID/PGID контейнера](../adr/0025-container-unix-identity.md), [протокол](../verification/reports/2026-10-19-container-unix-identity.md): несовпадающие ID блокируют план до создания графа; равенство не подтверждает DSM ACL.
- [Read-only проверка выбранного имени Docker-сети](../adr/0026-docker-network-preflight.md), [протокол](../verification/reports/2026-10-19-docker-network-preflight.md): конфликт или недоступное наблюдение блокируют новый план до графа; сеть по имени не присваивается.
- [Конкретный read-only план qBittorrent](../adr/0021-qbittorrent-installation-plan.md), [fixture-протокол](../verification/reports/2026-10-19-qbittorrent-plan.md): ресурсы, контексты адресов и свежая перепроверка; первый preview, без установки и подключения UI.

### Исполнение, журнал и владение (M3)

- [Локальный файловый журнал](../adr/0011-local-file-journal.md) и [проба перезапуска процессов](../verification/reports/2026-10-18-file-journal.md); remote lock/ownership остаются отдельной работой.
- [Маркер владения установкой](../adr/0017-installation-ownership-marker.md) и [протокол](../verification/reports/2026-10-19-ownership-marker.md): своя установка определяется только точным совпадением маркера.
- [Создание маркера владения](../adr/0018-ownership-marker-creation.md) и [протокол](../verification/reports/2026-10-19-ownership-marker-creation.md): первая мутация NAS через журнал намерений, пока только на стенде.
- [Граф конфигурационных операций и recovery](../adr/0020-configuration-plan-recovery.md), [протокол](../verification/reports/2026-10-19-plan-recovery.md): зависимости, неизменяемый план в журнале schema 2 и повторное наблюдение после перезапуска; прежние журналы schema 1 автоматически не мигрируются.
- [Создание конфигурационных каталогов](../adr/0024-configuration-directories.md) и [протокол](../verification/reports/2026-10-19-configuration-directories.md): каталоги монтирования под собственным маркером, существующие не меняются.
- [Удалённая блокировка установки](../adr/0019-remote-installation-lock.md) и [протокол](../verification/reports/2026-10-19-remote-lock.md): второй мастер не меняет ту же установку; чужая блокировка не перехватывается.
- [Compose/.env qBittorrent и исполняемый граф](../adr/0023-qbittorrent-configuration-graph.md) и [протокол](../verification/reports/2026-10-19-qbittorrent-configuration-graph.md): маркер → каталоги монтирования и файлы из плана, возобновление новым процессом; контейнер ещё не запускается.
- [Создание конфигурационного файла](../adr/0022-configuration-file-delivery.md) и [протокол](../verification/reports/2026-10-19-configuration-file-delivery.md): только создание под собственным маркером; ручные изменения не перезаписываются, замена и компенсация ещё не реализованы.

## Контракты сервисов M0

Клиенты Media/HA, профиль HA и device flows исследованы в M0 на disposable стендах: [контракты адаптеров](../catalog.md#контракты-адаптеров-и-профиля), [сводка M0](../verification/m0.md). Демонстрационный каркас Desktop — [ADR 0006](../adr/0006-desktop-demo.md), интерфейс «Фокус» — [ADR 0005](../adr/0005-focus-ui.md), клиентские платформы — [ADR 0007](../adr/0007-client-platforms.md).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
