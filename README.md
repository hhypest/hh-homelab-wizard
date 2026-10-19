# hh-homelab-wizard

Визуальный помощник для новой установки Media и Home Assistant на Synology NAS по SSH. Стек: **C# / .NET / Avalonia / MVVM**. Клиентская матрица: **Windows 11 x64, Ubuntu 24.04 LTS x64 / X11 и Wayland**.

[Текущая готовность и ограничения](docs/STATUS.md) — единый источник состояния продукта. [План разработки](docs/DEVELOPMENT_PLAN.md) описывает требования первого выпуска и 2.0.0, а не обещание уже работающих функций.

## Назначение и границы

Мастер должен проверить подготовленный NAS, показать конкретный план, развернуть контейнеры, настроить аккаунты и связи, сопоставить устройства и проверить результат. Обязательный состав v1: qBittorrent, Prowlarr, Radarr, Sonarr, FlareSolverr, Jellyfin, Seerr и Home Assistant Container с локальными интеграциями.

Поддерживаются новая установка и продолжение собственной прерванной сессии. Существующая медиатека сохраняется; чужие контейнеры, конфигурации и базы не присваиваются. Настройка сети, VPN, внешней публикации, облаков и исправление существующего стека в первый выпуск не входят.

Пользователь готовит DSM/Container Manager/SSH, предоставляет локальные доступы, выбирает индексаторы и выполняет подтверждения/PIN на устройствах. [Требования и последовательность экранов](docs/DEVELOPMENT_PLAN.md#3-пользовательский-сценарий) приведены в плане. База первого профиля — [hh-homelab v1.3.0](https://github.com/hhypest/hh-homelab/tree/v1.3.0), commit `b05047b081f3543520582315002e359ef1d93e59`.

Для 2.0.0 запланирован выбор компонентов, включая optional Arr/Bazarr/HA и обработчик индексаторов; объём и условия будущего архивирования hh-homelab — в [разделе 12](docs/DEVELOPMENT_PLAN.md#12-версия-200-настраиваемый-стек-и-архив-hh-homelab).

## Сборка и демонстрация

Для разработки нужен .NET SDK **10.0.401**, закреплённый в `global.json`. Из корня репозитория:

```text
dotnet restore HhHomelab.Wizard.slnx --locked-mode
dotnet build HhHomelab.Wizard.slnx --no-restore --configuration Release
dotnet test HhHomelab.Wizard.slnx --no-build --configuration Release
dotnet run --project tools/HhHomelab.Wizard.ProfileCheck --no-build --configuration Release -- profiles/hh-homelab/1.3.0/research-profile.json
dotnet run --project src/HhHomelab.Wizard.Desktop --no-build --configuration Release
```

ProfileCheck проверяет каталог; вторым аргументом можно передать checkout исходного hh-homelab v1.3.0 для сверки файлов. [M1 README](docs/m1/README.md) содержит запуск, self-contained публикацию, Linux-зависимости и протокол клиентской проверки. [M0 README](docs/m0/README.md) описывает developer tools, изолированные runtime-пробы и их ограничения.

## Разработка

Работа ведётся в [Issues/Milestones](https://github.com/hhypest/hh-homelab-wizard/issues) и [Project](https://github.com/users/hhypest/projects/3). После независимого аудита приоритет — транспорт, модель операций и один вертикальный сценарий qBittorrent с журналом/resume; [ADR 0008](docs/adr/0008-audit-development-sequence.md) отделяет доступную разработку от полной приёмки.

- [Карта задач](docs/ISSUE_ROADMAP.md) и [правила Project](docs/PROJECT.md).
- [Сведения об источнике аудита](docs/audit/2026-10-11-independent-review.md) и [решения по каждой находке](docs/audit/2026-10-11-follow-up.md).
- [CONTRIBUTING](CONTRIBUTING.md), [AGENTS](AGENTS.md), [CHANGELOG](CHANGELOG.md).
- [Правила написания кода](docs/CODE_STYLE.md).
- [Архитектура](docs/adr/0001-architecture.md), [журнал и секреты](docs/adr/0002-journal-and-secrets.md), [интерфейс «Фокус»](docs/adr/0005-focus-ui.md), [клиентские платформы](docs/adr/0007-client-platforms.md).
- [Локальный файловый журнал](docs/adr/0011-local-file-journal.md) и [проба перезапуска процессов](docs/testing/2026-10-18-file-journal.md); remote lock/ownership остаются отдельной работой.
- [Выбор SSH.NET и read-only порт подключения](docs/adr/0012-ssh-connection-probe.md), [fixture/TCP протокол](docs/testing/2026-10-18-ssh-probe.md) и [одноразовый OpenSSH-стенд](docs/testing/2026-10-19-ssh-runtime.md). Аппаратная DSM проверяется отдельно.
- [Read-only preflight inventory](docs/adr/0013-read-only-nas-preflight.md) и [его протокол](docs/testing/2026-10-19-nas-preflight.md): snapshot фактов не заменяет проверку совместимости, путей и ownership.
- [Настоящий Docker/Compose через SSH в CI](docs/testing/2026-10-19-docker-preflight.md), [граница изоляции стенда](docs/adr/0014-docker-preflight-runtime.md). Новый privileged режим предназначен только для одноразовой GitHub-hosted VM.
- [Модель NAS, слушающие порты и место томов](docs/adr/0015-preflight-host-resources.md) в preflight и [протокол](docs/testing/2026-10-19-preflight-resources.md): Passed портов/места возможен только относительно требований плана.
- [Read-only проверка путей](docs/adr/0016-remote-path-inspection.md) и [протокол](docs/testing/2026-10-19-remote-paths.md): symlink, границы тома и hardlink-устройство; DSM ACL не заявляются.

## Лицензии

Код, тесты, конфигурация сборки и машинные профили — [MIT](LICENSE). Авторские тексты документации — [CC BY 4.0](LICENSES/CC-BY-4.0.txt), © 2026 hhypest. Импортированные материалы и зависимости сохраняют происхождение, авторство, лицензии и необходимые уведомления; исходный внешний аудит не перелицензируется.
