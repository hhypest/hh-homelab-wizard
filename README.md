# hh-homelab-wizard

Визуальный помощник для новой установки Media и Home Assistant на Synology NAS по SSH. Стек: **C# / .NET / Avalonia / MVVM**. Клиентская матрица: **Windows 11 x64, Ubuntu 24.04 LTS x64 / X11 и Wayland**. Архитектурные границы — [карта архитектуры](docs/engineering/architecture.md).

**Статус:** разработка первого выпуска, 1.0.0 не выпущен. Демонстрационный мастер «Фокус» M1 принят владельцем на клиентских ОС, но не подключается к NAS. Read-only preflight, план qBittorrent, журнал, маркер владения и первые журналируемые операции проверены на fixtures и одноразовых стендах; установка на NAS не реализована. [Текущая готовность и очередь](ROADMAP.md) — единый источник состояния; [план разработки](docs/plans/development-plan.md) описывает требования первого выпуска и 2.0.0, а не обещание уже работающих функций.

Маршруты чтения: [как попробовать демонстрацию, разрабатывать, проверять и находить решения](docs/README.md).

## Назначение и границы

Мастер должен проверить подготовленный NAS, показать конкретный план, развернуть контейнеры, настроить аккаунты и связи, сопоставить устройства и проверить результат. Обязательный состав v1: qBittorrent, Prowlarr, Radarr, Sonarr, FlareSolverr, Jellyfin, Seerr и Home Assistant Container с локальными интеграциями.

Поддерживаются новая установка и продолжение собственной прерванной сессии. Существующая медиатека сохраняется; чужие контейнеры, конфигурации и базы не присваиваются. Настройка сети, VPN, внешней публикации, облаков и исправление существующего стека в первый выпуск не входят.

Пользователь готовит DSM/Container Manager/SSH, предоставляет локальные доступы, выбирает индексаторы и выполняет подтверждения/PIN на устройствах. [Требования и последовательность экранов](docs/plans/development-plan.md#3-пользовательский-сценарий) приведены в плане. База первого профиля — [hh-homelab v1.3.0](https://github.com/hhypest/hh-homelab/tree/v1.3.0), commit `b05047b081f3543520582315002e359ef1d93e59`.

Для 2.0.0 запланирован выбор компонентов, включая optional Arr/Bazarr/HA и обработчик индексаторов; объём и условия будущего архивирования hh-homelab — в [разделе 12](docs/plans/development-plan.md#12-версия-200-настраиваемый-стек-и-архив-hh-homelab).

## Начать работу

**Попробовать демонстрацию:** скачайте пробный пакет из артефактов CI и следуйте [инструкции пакета](docs/guide/demo-package.md). Пароли и доступ к NAS не нужны.

**Разработчику:** нужен .NET SDK **10.0.401**, закреплённый в `global.json`. Из корня репозитория:

```text
dotnet restore HhHomelab.Wizard.slnx --locked-mode
dotnet build HhHomelab.Wizard.slnx --no-restore --configuration Release
dotnet test HhHomelab.Wizard.slnx --no-build --configuration Release
dotnet run --project src/HhHomelab.Wizard.Desktop --no-build --configuration Release
```

Проверка документации и профиля, self-contained публикация и зависимости Linux — в [разработке](docs/development.md); изолированные пробы — в [инструментах разработчика](docs/engineering/developer-probes.md); методика и уровни доказательств — в [TESTING](TESTING.md).

## Как ведётся работа

Работа ведётся в [Issues/Milestones](https://github.com/hhypest/hh-homelab-wizard/issues) и [Project](https://github.com/users/hhypest/projects/3). После независимого аудита приоритет — транспорт, модель операций и один вертикальный сценарий qBittorrent с журналом/resume; [ADR 0008](docs/adr/0008-audit-development-sequence.md) отделяет доступную разработку от полной приёмки. Порядок изменений — [CONTRIBUTING](CONTRIBUTING.md) и [правила для агентов](AGENTS.md); история — [CHANGELOG](CHANGELOG.md).

## Лицензии

Код, тесты, конфигурация сборки и машинные профили — [MIT](LICENSE). Авторские тексты документации — [CC BY 4.0](LICENSES/CC-BY-4.0.txt), © 2026 hhypest. Импортированные материалы и зависимости сохраняют происхождение, авторство, лицензии и необходимые уведомления; исходный внешний аудит не перелицензируется.
