# M0: подключение Jellyfin к Home Assistant

Контракт ограничен HA Container **2026.10.0** и Jellyfin **12.2.0**, образ `lscr.io/linuxserver/jellyfin:12.2ubu2604-ls53` исходного профиля. Это адаптер Infrastructure и developer probe; пользовательского мастера и журнала установки пока нет.

## Операции

`StartJellyfinSetupAsync` принимает URL, уже проверенный server ID Jellyfin и username. До POST проверяет текущую HA-сессию и отсутствие существующих Jellyfin entries, затем фиксирует намерение в памяти. Создаётся только штатный USER flow. Поддерживается форма `user` с полями url/username/password; другие шаги и схема блокируются.

`SubmitJellyfinCredentialsAsync` получает пароль отдельным аргументом. Неверный пароль и недоступность сервера возвращают безопасные статусы. Новый ввод после полученного ответа с ошибкой — явное действие вызывающей стороны, а не автоматический повтор. Неизвестный исход записи останавливает дальнейшую отправку credentials.

Успешный ответ создания предоставляет entry ID. `ObserveJellyfinSetupAsync` проверяет эту запись, disabled state, состояние запуска и связь с устройством Jellyfin через `config/device_registry/list`: entry ID, идентификатор сервера и версия 12.2.0. `setup_in_progress`/`setup_retry` означают ожидание; `loaded` подтверждается только вместе с устройством правильного сервера. Название интеграции не является идентичностью. Отсутствие активного клиента/плеера не означает сбой подключения сервера.

`ObserveJellyfinEntryAsync` — отдельная операция чтения по ранее полученным entry ID и server ID, в том числе после restart/нового login. Она не присваивает запись клиенту и не разрешает изменение существующей интеграции. Публичная metadata HA не раскрывает credentials/unique_id записи: этот readback подтверждает сервер и работоспособность загрузки интеграции, но не независимую проверку имени аккаунта.

Пароль, токены и raw config-flow ответы не возвращаются в модели прогресса и не выводятся. При ошибках серверный текст и suggested/default значения формы не копируются в диагностику. HTTP redirects должны быть отключены; вызывающая сторона проверяет владение HA и сериализует операции.

## Неопределённый результат и восстановление

- Если потерян ответ start, неизвестен flow ID. API не предоставляет список USER flows: GET коллекции не поддерживается, WS progress исключает такие flows. Клиент сохраняет неизвестное намерение и не начинает второй flow.
- Если потерян ответ submit, сохранённый flow ID можно прочитать. GET заново вызывает шаг с `None`, поэтому предыдущие ошибки credentials не сохраняются. Пустая форма не доказывает, что предыдущий запрос завершился безопасно: клиент сохраняет `OutcomeUnknown`.
- После успешного завершения flow GET возвращает 404. Без entry ID из собственного ответа клиент не присваивает появившуюся запись по title и не повторяет submit.
- Существующий entry блокирует start у нового клиента. Параллельные клиенты/перезапуск процесса без подтверждённого журнала не являются поддержанным resume. Полный контракт переноса намерения и ручного разрешения неизвестного исхода относится к M3.
- Автоматическое удаление flow/entry, reauth/reconfigure и изменение `.storage` не реализованы. Откат подключения не объявляется транзакционным. Незавершённые flows остаются на стороне HA; disposable probe удаляет только весь собственный тестовый контейнер/volume после проверки владения.

## Готовность Jellyfin и формат JSON

Публичный `System/Info/Public` может отвечать 200 ещё из временного SetupServer, с camelCase JSON, до запуска основных API. Docker harness ждёт `Startup/User`: временный сервер возвращает 503; основной API — 200 на свежей конфигурации или 401/403 после настройки. Это проверка готовности endpoint, не успешной авторизации. Затем адаптер отдельно проверяет версию и идентичность, явно запрашивая JSON-профиль PascalCase. Readiness GET допускает ограниченное ожидание транспорта; credentials и записи не повторяются автоматически. Источники: [SetupServer v12.2](https://github.com/jellyfin/jellyfin/blob/v12.2/Jellyfin.Server/ServerSetupApp/SetupServer.cs), [PascalCase formatter](https://github.com/jellyfin/jellyfin/blob/v12.2/Jellyfin.Api/Formatters/PascalCaseJsonProfileFormatter.cs).

## Проверки и границы

Fixtures проверяют полученный отказ credentials, явную коррекцию, потерянные start/submit, отмену start, запрет повторных POST, чужой entry/сервер, disabled/setup_error/setup_retry, неизвестный handler и безопасную модель результата. Точный фактический результат живых проб публикуется в [протоколе](PROBE_REPORT.md).

HaDockerProbe использует отдельные HA и Jellyfin в собственной bridge-сети. Адрес Jellyfin задаётся внутри этой сети, поэтому авторизация проверяется из HA, а не только с компьютера разработчика. Проба создаёт синтетического администратора Jellyfin; это не решение о правах будущей служебной учётной записи. Проверка loaded не подтверждает воспроизведение, media_player на реальном клиенте, DSM, NAS ACL, автоматизации или полный dashboard.

Первичные источники точной версии: [Jellyfin config flow](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/jellyfin/config_flow.py), [REST/WS config entries](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/config/config_entries.py), [flow manager](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/data_entry_flow.py), [Jellyfin setup/device registry](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/jellyfin/__init__.py), [coordinator](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/jellyfin/coordinator.py).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
