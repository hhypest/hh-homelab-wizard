# M0: локальный профиль Home Assistant

Реализованы C#-проверка доставки Monitor Docker и рендер локальной конфигурации из hh-homelab **v1.3.0**, commit `b05047b081f3543520582315002e359ef1d93e59`. Это исследовательский контракт Infrastructure и developer probe. Установки на NAS, GUI, журналирования и продуктового resume пока нет; M0 остаётся открытым.

## Доставка компонента

`MonitorDockerPackage.FetchAsync` получает архив по фиксированному HTTPS URL точного commit `78abdc40d96871be44bd965202de59fe2e5b10d3`. Перенаправления запрещены, сертификат проверяется штатным HTTP transport. Архив ограничен 4 MiB, отдельный распакованный файл — 512 KiB, число записей — 200.

[Lock-файл](../../profiles/hh-homelab/1.3.0/components/monitor-docker.lock.json) содержит raw-byte SHA-256 всех 38 файлов компонента и upstream `LICENSE.md`. При подготовке lock содержимое сверено с Git blob SHA-1 из дерева указанного commit. Скачивание проверяет SHA-256 каждого файла до формирования payload; Git API при каждом запуске не требуется. Не принимаются недостающие/изменённые файлы, дополнительные файлы внутри компонента, дубликаты без учёта регистра, другой корень архива, traversal, Windows-пути, symlinks и специальные файлы. Посторонние материалы репозитория не устанавливаются. ZIP не извлекается напрямую на диск.

Manifest проверяется дополнительно: `monitor_docker`, версия **1.20b3** (исходный tag называется **1.20**), зависимости **aiodocker 0.25.0** и **python-dateutil 2.9.0.post0**. Код компонента не патчится и не хранится в wizard; лицензия Apache-2.0 передаётся как `custom_components/monitor_docker/LICENSE.upstream.md`. Это не зависимость от HACS и не запуск установочного скрипта.

## Рендер конфигурации

`HomeAssistantLocalProfile.RenderAsync` использует встроенный доверенный каталог fingerprints, а не предоставленные вызывающим кодом хеши. Проверяются все 19 исходных артефактов профиля. Для каждого читаемого файла конфигурации повторно проверяется хеш конкретного содержимого перед преобразованием. Исходный checkout не изменяется.

Payload включает 50 файлов:

- `configuration.yaml`, пустые UI include-файлы `automations.yaml`, `scripts.yaml`, `scenes.yaml`;
- пакеты Docker, monitoring, media_tv и новый local_notifications;
- `bin/docker_state.py` и `bin/http_check.py`;
- `secrets.yaml` с шестью необходимыми локальному YAML значениями;
- 39 проверенных файлов Monitor Docker вместе с лицензией.

Пакеты Alice и Pachca не включаются. Все 11 вызовов `script.pachca_notify` в Docker/monitoring заменены на `script.hh_homelab_notify`, который вызывает `persistent_notification.create` с исходными заголовком и текстом. Параметр `level` принимается для совместимости вызовов; отдельного оформления уровней в HA persistent notifications нет. Внешние webhook, отчёты Pachca и облачные привязки исключены вместе с их секретами. Функция внешних уведомлений в этом renderer ещё не реализована.

Исходные сценарии ТВ, условия, действия и времена ожидания сохранены. Адрес HA и WOL broadcast задаются явно; принимаются только канонические IPv4. MAC, координаты, user ID и формат Jellyfin token проверяются до чтения источника. Значения строк сериализуются как YAML-совместимые JSON scalars. Renderer не выбирает случайный плеер и не создаёт сущности: остаются конкретные роли LG/GX1/Cast/Jellyfin и датчики DS725, которые должны быть подключены и сопоставлены отдельными контрактами.

В `http_check.py` удалено отключение проверки TLS. HTTPS использует системное доверие; для самоподписанного сертификата нужна корректная доверенная CA, а не bypass. Остальные проверки адресов и портов Media сохраняют контракт исходного host-network профиля. Настраиваемые порты и другой сетевой режим этим renderer не поддержаны.

Payload содержит секреты в памяти и предоставляет только список путей и запись TAR в поток. `ToString` настроек/payload не печатает поля. `secrets.yaml` получает режим 0600, остальные файлы — 0644, все TAR entries — обычные файлы с известными относительными именами. В probe TAR передаётся через stdin в свежий собственный Docker volume. Секреты не пишутся в локальный temporary file, Git или аргументы процессов. Это не гарантия защищённого хранения памяти или готовое хранилище секретов M3.

## Воспроизведение пробы

Нужны .NET SDK из global.json, локальный Linux Docker Engine, доступ к GitHub codeload и источникам Python requirements HA. Отдельная установка Python на Windows/Linux клиента не нужна: тестовый Python-код выполняется внутри закреплённого HA image. Checkout hh-homelab должен соответствовать указанному commit; его содержимое проверяется renderer.

```text
docker pull ghcr.io/home-assistant/home-assistant:2026.10.0
dotnet restore HhHomelab.Wizard.slnx --locked-mode
dotnet build HhHomelab.Wizard.slnx --no-restore --configuration Release
dotnet run --project tools/HhHomelab.Wizard.HaProfileProbe --no-build --configuration Release -- /path/to/hh-homelab-v1.3.0
```

Probe создаёт собственные именованные volume, сеть и два временных контейнера с session label. Bind mounts, Docker socket, NAS и устройства не используются. TAR принимается только в пустую конфигурацию; владение проверяется перед записью и удалением. Ошибки HA/конфигурация захватываются без вывода сырого текста. Отмена завершает локальный процесс Docker и запускает независимый ограниченный cleanup собственных ресурсов; успешный исход требует подтверждённого cleanup. После аварийного завершения процесса долговечный recovery ещё отсутствует.

Внутри образа выполняются:

1. Настоящий HA YAML loader с include/packages и разрешением secrets; проверка состава четырёх пакетов и шести ключей secrets.
2. `async_check_ha_config_file` HA 2026.10.0; успех требует отсутствия ошибок **и предупреждений**.
3. Импорт Monitor Docker/helpers и проверка установленных точных версий обеих Python-зависимостей.
4. Исполнение исходных Jinja-шаблонов `TV watching`/`TV idle` через HA Template и StateMachine на десяти синтетических наборах состояний. Проверяются Jellyfin playing/buffering/paused, YouTube playing/paused, отсутствующий достоверный источник, LG unavailable/unknown/off. Синтетические состояния существуют только внутри checker; фиктивные сущности в установленную конфигурацию не добавляются.
5. Наличие исходного запрета ночной автоматики при `tv_no_auto_off` в обеих ночных автоматизациях. В этом checker это структурная проверка конфигурации; динамические действия отдельно проверяет HaRuntimeProbe.

11 октября 2026 года локальная проба успешно выполнена на свежей конфигурации; окончательные повторные результаты и CI публикуются в PR и [протоколе](PROBE_REPORT.md). Добавлен отдельный `ha-profile-runtime` Ubuntu 24.04 job с checkout точного upstream commit и двумя свежими прогонами.

## Пределы доказательства и следующий шаг

Успешный checker подтверждает валидность полного локального YAML, импорты/зависимости компонента и перечисленные вычисления шаблонов на HA 2026.10.0. Он не запускает Home Assistant как сервер и не подтверждает создание Monitor Docker sensors, их метрики, подключения DSM/TV, HTTP readiness Media, Jellyfin Resume, аппаратное управление, фактические задержки/перезапуск автоматизаций или доставку уведомлений.

Полный dashboard ещё не рендерится. Синтетические Jellyfin user/token не являются рабочей авторизацией и не выдают зелёную готовность интеграций. Отдельный [HaRuntimeProbe](HA_RUNTIME.md) уже проверяет полный HA-сервер с настоящим proxy, синтетическими Docker-метриками, уведомлением, restart и 18 динамическими action cases с ускоренными задержками. Эти результаты не расширяют объём checker. Дальше нужны остальные config flows, disabled entities, роли устройств и согласованная панель. Проверка на DS725+/DSM остаётся аппаратным этапом.

Первичные источники: [Monitor Docker commit](https://github.com/ualex73/monitor_docker/tree/78abdc40d96871be44bd965202de59fe2e5b10d3), [HA check_config 2026.10.0](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/helpers/check_config.py), [HA Template](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/helpers/template/__init__.py), [исходная конфигурация hh-homelab](https://github.com/hhypest/hh-homelab/tree/b05047b081f3543520582315002e359ef1d93e59/homeassistant/config).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
