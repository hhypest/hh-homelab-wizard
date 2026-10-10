# Протокол локальных проб M0

Дата: 2026-10-10. Среда: Windows, .NET SDK 10.0.401, Docker Desktop с Linux Engine **29.8.2**, linux/amd64. Это не NAS, не испытание DSM и не проверка Avalonia.

Создана отдельная bridge-сеть и девять временных контейнеров с меткой `com.hh-homelab-wizard.session=m0-20261010-a1`. Опубликованные порты привязаны к 127.0.0.1 и назначены Docker динамически. Конфигурация временная, медиатека и каталог реального hh-homelab не монтировались. Для Docker Socket Proxy использовался только read-only socket с `POST=0` и ограниченными GET-категориями. Accounts/credentials синтетические; значения не входят в протокол.

## Первый стенд: подтверждённый объём

| Сервис / версия | Что выполнено | Что этим не доказано |
| --- | --- | --- |
| Home Assistant 2026.10.0 | GET onboarding, POST создания владельца, обмен auth code на token, core_config, авторизованный GET config | Продолжение после потери ответа; интеграции с реальными устройствами; загрузка полного YAML проекта |
| HA config flows | Получены начальные form для synology_dsm, webostv, androidtv_remote, jellyfin, qbittorrent, sonarr и radarr | Заполнение/завершение flow, reauth, Google Cast discovery, PIN и LG confirmation |
| Jellyfin 12.2.0 | Public system info; Startup Configuration/User/RemoteAccess/Complete; AuthenticateByName с созданным пользователем | Создание и сканирование библиотек; воспроизведение на GX1; повтор после прерывания |
| Seerr 3.5.0 | Status; первый auth/jellyfin с администратором Jellyfin по контейнерному имени; GET settings/jellyfin подтвердил связь | Radarr/Sonarr settings, библиотеки, permissions, реальные запросы |
| Sonarr 4.0.20.3014 | Status с API key; downloadclient/schema содержит QBittorrent | Создание клиента и root folders; authentication settings, импорт |
| Radarr 6.4.4.10685 | Status с API key; downloadclient/schema содержит QBittorrent | Создание клиента и root folders; authentication settings, импорт |
| Prowlarr 2.6.5.5623 | Status с API key; applications/schema содержит Sonarr | Создание applications, индексаторы и sync в оба Arr |
| FlareSolverr v3.5.2 | HTTP health/root отвечает | Обход конкретного пользовательского индексатора |
| Docker Socket Proxy v0.5.0 | GET /version = 200, POST /version = 403 | DSM socket path, метрики Monitor Docker, разрешения других API-категорий |

Ключи Arr читались из config.xml **только своих временных контейнеров**, оставались в памяти и не выводились. Docker GET /version и POST /version не создают и не удаляют контейнеры; второй запрос проверяет блокировку POST на безвредном endpoint.

## qBittorrent: история отказов и подтверждённый bootstrap

На первом стенде образ профиля и `/app/qbittorrent-nox --version` подтвердили **v5.2.4**, но попытки с временным паролем и с подготовленной PBKDF2-конфигурацией не подтвердили аутентификацию. HTTP-ошибка, таймаут при устаревшем динамическом порте и результат отказа credentials были разными неуспешными исходами. Эта история сохранена; она не является доказательством ошибки самого qBittorrent.

При исследовании [authcontroller.cpp выбранной версии](https://github.com/qbittorrent/qBittorrent/blob/release-5.2.4/src/webui/api/authcontroller.cpp) и [формирования HTTP-ответа](https://github.com/qbittorrent/qBittorrent/blob/release-5.2.4/src/webui/webapplication.cpp) обнаружено отличие от старого описания WebUI API: успешный login без data получает 204, неверные credentials — 401. При существующей сессии login возвращает успех без проверки переданного пароля; localhost/subnet bypass также создаёт сессию до обработки login. Поэтому одного 204 и последующего GET версии недостаточно.

После разбора Codex review к PR #2 клиент сначала требует 403 на защищённом GET версии в отдельном контексте без предварительной авторизации; доступный endpoint блокирует отправку credentials. GET-запросы, как и login, получают Referer для HTTP-проверок сервера. Затем клиент принимает 204 и проверяет авторизованный GET версии. Текст `Ok.` со статусом 200 отклоняется. Login 401, 403 и прочие HTTP-ошибки имеют разные безопасные коды с числовым статусом, без response body. 403 не объявляется однозначно баном: возможен другой запрет доступа.

После PR #3 полный путь проверен на новом стенде: отказ GET версии 403, login с временным паролем 204, GET с cookies 200 и `v5.2.4`. В контролируемом отрицательном запросе с другим `Host` port, без credentials, получен 401. [Host validation в исходниках 5.2.4](https://github.com/qbittorrent/qBittorrent/blob/release-5.2.4/src/webui/webapplication.cpp) сравнивает этот порт с портом сервера. Это воспроизводимое объяснение отказа HTTP до проверки пароля; без старых полных диагностических данных нельзя приписать ему каждый прежний отказ.

Дополнительно локально запущен собранный QbProbe против временного HTTP fixture на loopback: шесть сценариев — доступ до login, login 401/403/404/500 и успешная последовательность с `Set-Cookie`/`Cookie`. Проверены exit code, числовой HTTP-статус, отсутствие POST при открытом доступе и отсутствие синтетических секретов/response body в выводе. Это проверка транспорта и консольной диагностики на искусственном сервере, не работа реального qBittorrent; вспомогательный smoke harness в репозиторий не включён.

QbProbe на C# сохраняется как диагностический инструмент с безопасными error codes и передачей credentials через stdin. Добавлен самостоятельный C# QbDockerProbe без новых NuGet-зависимостей. Два локальных запуска на свежих конфигурациях с последующим перезапуском каждого контейнера подтвердили:

- temporary login, смену username/password через API, отказ старых credentials и вход новых отдельными клиентами;
- сохранение save/temp paths и enabled-флага, отсутствие повторной записи совпадающих preferences;
- категории `movies` и `tv` с путями `/data/torrents/movies` и `/data/torrents/tv`, отсутствие дублей и сохранение конфликтующего пути;
- наблюдение созданной категории после искусственной потери успешного HTTP-ответа в handler, без повторного POST;
- сохранение credentials/preferences/категорий после stop/start и проверку состояния до любых повторных записей;
- cleanup собственных container/config volume/network; проверка списков по метке не обнаружила оставшихся ресурсов стенда.

Сеть этих проб — отдельный обычный bridge, публикация только на loopback. `/data` находится в tmpfs, конфигурация — в собственном named volume. Случайный host port совпадает с `WEBUI_PORT` тестового контейнера. VueTorrent mod и рабочий Compose не использованы, загрузки не добавлялись. Проверены API-настройки, не воспроизведение, hardlinks, ACL DSM или связи Arr. Установка не получила журнал и resumable engine: это ещё M0. Новый CI job на Ubuntu повторяет сценарий дважды; итог конкретного CI run фиксируется в PR.

Во время подготовки стенда обнаружены две ошибки тестового окружения: internal bridge не дал ожидаемой публикации порта Docker Desktop; logging driver `local` со включённым compression отклонил `max-file=1`. Инструмент использует отдельный обычный bridge и явно выбирает `json-file` с ограниченным журналом. Неудачные подготовительные запуски не учитываются как успешные пробы; их ресурсы удалены после проверки владения.

## Arr: настройка аккаунтов, папок и связей

Следующий инкремент M0 выполнен на тех же закреплённых версиях Radarr **6.4.4.10685**, Sonarr **4.0.20.3014**, Prowlarr **2.6.5.5623** и qBittorrent **5.2.4**. Добавлены C# ArrContractClient, ArrDockerProbe и общий ProbeSupport. Локальный повторяемый сценарий на собственных свежих конфигурациях подтвердил:

- переход `none` → `forms`, `authenticationRequired=enabled` у трёх Arr; анонимный UI перенаправляется на login, правильный пароль открывает UI, неверный отклоняется;
- независимость API-key доступа от cookie веб-входа: анонимный status получает 401, исходный ключ даёт точную версию;
- создание доступных root folders `/data/media/movies` и `/data/media/tv`;
- download clients qBittorrent в Radarr/Sonarr с категориями `movies`/`tv`, успешный Test из самих Arr;
- два Prowlarr applications с внутренними адресами Radarr/Sonarr и `fullSync`, успешные Test;
- повтор без create/update, отказ при конфликте host, наблюдение root folder после искусственной потери успешного ответа;
- сохранение исходных ключей, credentials, папок и связей после stop/start всех четырёх контейнеров;
- cleanup собственных контейнеров, config/data volumes и сети.

Сохранённые providers возвращают маскированные password/API key. Повтор использует ID, проверяет несекретные поля, затем вызывает Test сохранённого доступа и отдельно Test предоставленных credentials. Это проверка работоспособности соединения; она не доказывает равенства скрытого секрета исходной строке. HTTP success без наблюдаемой настройки не считается успехом. Unit fixtures покрывают также неизвестный результат provider/authentication write, отмену записи, чужую политику авторизации, недоступные/чужие папки, дубликаты, malformed JSON и безопасные ошибки. Набор содержит **139 тестов**, из них 47 добавлены для Arr.

Общие данные расположены в новом пустом named volume. Порты опубликованы только на 127.0.0.1, сеть — отдельный обычный bridge. Индексаторы, торрент-файлы и загрузки не добавляются. `fullSync` проверен как настройка и соединение; передача конкретных индексаторов не проверена. Импорт, hardlinks, качества/языки, Jellyfin, Seerr и права/ACL DSM остаются за пределами этой пробы. Исследовательский профиль сохраняет `NotImplemented` для операций установщика: журнала и движка ещё нет.

Во время разработки первые два полных запуска дошли до связей, но остановились при readiness после перезапуска Arr: динамический внешний порт изменился, harness обращался к прежнему адресу. Исправлено повторным чтением port binding после каждого start. Эти запуски не считаются успешными испытаниями сохранения; их ресурсы очищены. При подготовке проверок сборка поверх работающего probe на Windows также получила блокировку DLL; итоговая сборка выполняется после завершения процесса. Во время повторной проверки и проверки сохранения transport запрещает конфигурационные записи; разрешены чтение и API Test. Ubuntu job повторяет весь bootstrap дважды на свежих конфигурациях; фактический результат CI фиксируется в PR.

Первичные источники контрактов: [Sonarr authentication](https://github.com/Sonarr/Sonarr/blob/v4.0.20.3014/src/Sonarr.Http/Authentication/AuthenticationController.cs), [ProviderControllerBase](https://github.com/Sonarr/Sonarr/blob/v4.0.20.3014/src/Sonarr.Api.V3/ProviderControllerBase.cs), [Radarr OpenAPI](https://github.com/Radarr/Radarr/blob/v6.4.4.10685/src/Radarr.Api.V3/openapi.json), [Prowlarr OpenAPI](https://github.com/Prowlarr/Prowlarr/blob/v2.6.5.5623/src/Prowlarr.Api.V1/openapi.json). Общая схема взята с живого API закреплённой версии; произвольные JSON payload не предоставляются вызывающему коду.

## Jellyfin и Seerr: библиотеки и сохранённые связи

Инкремент проверен на Jellyfin **12.2.0** (`lscr.io/linuxserver/jellyfin:12.2ubu2604-ls53`) и Seerr **3.5.0** (`ghcr.io/seerr-team/seerr:v3.5.0`) вместе с закреплёнными Radarr/Sonarr. В Windows/Docker Desktop Linux Engine 29.8.2 выполнены три успешных полных запуска MediaDockerProbe на свежих конфигурациях. Последний запуск включает отрицательные проверки паролей; первые два выполнены до их добавления. Все три завершились exit 0 и cleanup; отдельные регрессионные запуски QbDockerProbe и ArrDockerProbe также завершились exit 0. Сборка Release без предупреждений; **187 unit/fixture-тестов**, 48 новых для Jellyfin/Seerr.

Подтверждено runtime:

- создание первого администратора Jellyfin, локаль ru-RU/RU/ru, отключение внешнего доступа приложения и завершение startup; новый вход и проверка administrator policy/user/server ID;
- две библиотеки с типами movies/tvshows, путями `/data/media/movies` и `/data/media/tv`, ru/RU; повтор без создания и отказ при конфликте пути;
- первый владелец Seerr через Jellyfin, затем независимый cookie context и проверка сохранённого Jellyfin API доступа, user/server IDs и endpoint;
- получение реальных library IDs, синхронизация и включение ровно двух ожидаемых movie/show библиотек;
- выбор HD-1080p из live Arr Test, проверка доступных root folders, Test перед сохранением и после него, сохранение двух default connections; HD-1080p и preventSearch=true выбраны только для синтетического сценария;
- initialize только после повторного подтверждения сохранённых библиотек и обеих связей;
- пять потерянных успешных ответов после завершённых записей: Startup/User, создание библиотеки, первый auth/jellyfin, связь Radarr, initialize; наблюдение без повторного создания;
- отказ неверных паролей Jellyfin и Seerr в свежих клиентах;
- сохранение credentials и server/user/library IDs, enabled libraries, links и initialized после перезапуска четырёх контейнеров; transport блокирует конфигурационные записи при повторе и проверке сохранения;
- cleanup только ресурсов с меткой владения; проверки собственных mounts выполняются перед удалением.

Первые две подготовительные полные пробы остановились с HTTP 401: сначала на анонимном Startup/User после уже установленного пароля, затем на Users/Me с legacy X-Emby-Token. Они не учитываются как успешные испытания; ресурсы очищены. Исправлено: сначала проверять login при незавершённом startup и передавать token в современном Authorization MediaBrowser. Legacy-режим не включается. [StartupController v12.2](https://github.com/jellyfin/jellyfin/blob/v12.2/Jellyfin.Api/Controllers/StartupController.cs), [AuthorizationContext v12.2](https://github.com/jellyfin/jellyfin/blob/v12.2/Jellyfin.Server.Implementations/Security/AuthorizationContext.cs) объясняют оба наблюдённых отказа. Подтверждение session/readiness снимается после неудачного повторного login/проверки связи; новые fixture-тесты не позволяют продолжать initialize на устаревшем подтверждении.

Стенд не имеет host bind mounts; Jellyfin получает read-only подкаталог media собственного data volume, `/cache` временный. Сеть обычная bridge, порты на loopback. Внешние сервисы, реальные индексаторы, media files, загрузки и запросы не использованы; scan/playback/transcoding/импорт/hardlinks не проверены. Проверен первый администратор, не обычный пользователь и его policies. Успешный Test подтверждает API-связь и выбранные profile/root, а не обработку реального запроса. Потеря ответа после завершённой записи не доказывает восстановление после падения Seerr между созданием owner, выдачей Jellyfin API key и сохранением настроек. Эти несколько серверных записей не образуют общей транзакции; отдельное восстановление остаётся открытым пунктом. NAS, UI, journal и installer не проверены и не реализованы этим инкрементом.

Первичные источники: [Jellyfin library controller](https://github.com/jellyfin/jellyfin/blob/v12.2/Jellyfin.Api/Controllers/LibraryStructureController.cs), [user controller](https://github.com/jellyfin/jellyfin/blob/v12.2/Jellyfin.Api/Controllers/UserController.cs), [Seerr auth](https://github.com/seerr-team/seerr/blob/v3.5.0/server/routes/auth.ts), [Jellyfin settings и initialize](https://github.com/seerr-team/seerr/blob/v3.5.0/server/routes/settings/index.ts), [Radarr settings/Test](https://github.com/seerr-team/seerr/blob/v3.5.0/server/routes/settings/radarr.ts), [Sonarr settings/Test](https://github.com/seerr-team/seerr/blob/v3.5.0/server/routes/settings/sonarr.ts). Наличие API в source отличено от перечисленных выше runtime-результатов. Новый job `media-runtime` выполняет две свежие пробы на Ubuntu; итог конкретного CI run фиксируется в PR.

## Home Assistant: владелец, HTTP, реестр и панель

Следующий инкремент проверен на **HA Container 2026.10.0** (`ghcr.io/home-assistant/home-assistant:2026.10.0`) профиля hh-homelab v1.3.0. Добавлены HomeAssistantContractClient, ограниченный WebSocket transport и HaDockerProbe без новых NuGet-зависимостей. Три полные локальные пробы на свежих config volumes в Windows/Docker Desktop Linux Engine 29.8.2 завершились exit 0 и cleanup; последняя включает сравнение всего ожидаемого HTTP config, а не только protection-полей. Сборка Release без предупреждений, **240 unit/fixture-тестов**, 53 новых для HA.

Подтверждено runtime:

- версия в WS greeting до передачи credentials; создание первого owner, token exchange, обычный login при повторе, owner/admin/name и отсутствие safe mode;
- завершение четырёх шагов onboarding с проверкой флагов; после restart endpoint onboarding отсутствует, повтор использует обычный login;
- отказ неверного пароля в отдельном клиенте; токены и auth codes не выводятся;
- создание настоящего storage input_boolean через API как fixture, чтение registry identity и переименование только той же записи;
- повтор renamed entity без update, отказ при чужом unique ID;
- отдельный storage dashboard, API create/save/readback с явной ссылкой на зарегистрированный включённый helper, повтор без create/save;
- HTTP IP ban, threshold=5, X-Forwarded-For=false, trusted_proxies=[]: pending → штатный restart → refresh/reconnect → прежний owner, RUNNING и active pending → promote → stable readback;
- шесть потерянных успешных ответов после записи owner, registry update, dashboard create, config save, HTTP configure и promote; дальнейшее наблюдение без повторной записи;
- check_config минимального YAML без ошибок/предупреждений;
- после stop/start того же контейнера: прежние пароль/owner ID, registry identity/renamed ID, dashboard config и stable HTTP protection, с блокировкой конфигурационных записей транспортом;
- cleanup двух собственных контейнеров (seed/HA), config volume и сети, без host mounts/socket/устройств и без правок .storage.

Первый подготовительный запуск подтвердил owner/registry/dashboard, но остановился после HTTP configure: тестовый запуск напрямую через Python обошёл штатный `/init`, HA завершил контейнер с кодом 100, и reconnect истёк. Исправлено: YAML создаётся отдельным собственным seed, основной контейнер запускает обычный `/init` образа. Второй запуск остановился с `not_running`: открытый HTTP/WS не означает завершённого startup. Добавлены наблюдение RUNNING до configure и ожидание RUNNING после reconnect. Оба неуспешных запуска очищены и не учитываются как успешные полные пробы.

Состояния failed/reverted/foreign pending, невозможность promote на прежнем соединении, чужой owner/admin/name/safe mode, коллизии registry ID/unique ID/platform/config entry/device/disabled state, несоответствие WS command ID, malformed JSON и повтор шагов onboarding после потери ответа покрыты fixtures. Сравнивается весь ожидаемый HTTP config без server metadata: другое значение CORS также блокирует promote. Автоматический пятиминутный откат HTTP и достижение лимита IP ban на реальном сервере не испытывались. API success без readback не считается результатом; cancellation не объявляется rollback.

На этой версии source default HTTP config задаёт ip_ban_enabled=true и threshold=-1; один enabled-флаг не подтверждает выбранный threshold=5. Проба явно устанавливает и проверяет оба параметра. Это уточнение контракта закреплённой версии, не изменение исходного hh-homelab и не испытание рабочей DSM.

Это минимальный HA fixture, не полный configuration.yaml/packages/dashboard hh-homelab. Onboarding core самого HA может создавать штатные интеграции и выполнять внешние запросы; сеть обычная bridge, не air gap. Облачные аккаунты не привязывались. Config flows Synology/LG/GX1/Jellyfin, доставка Monitor Docker, полный рендер с исключёнными облачными модулями, disabled-сенсоры, управление устройствами, визуальная панель и прикладные автоматизации не проверены. Сохранение simple entities card не доказывает отображление всего dashboard. Журнал/маркер владения/движок и Avalonia UI ещё не реализованы; профиль сохраняет NotImplemented.

Первичные источники версии 2026.10.0: [onboarding views](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/onboarding/views.py), [onboarding setup](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/onboarding/__init__.py), [login flow](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/auth/login_flow.py), [token/current_user](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/auth/__init__.py), [HTTP WS](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/http/websocket_api.py), [HTTP store/defaults/revert](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/http/config.py), [entity registry](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/config/entity_registry.py), [Lovelace WS](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/lovelace/websocket.py), [input_boolean](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/input_boolean/__init__.py). `ha-runtime` повторяет сценарий дважды в Ubuntu CI; результат конкретного запуска фиксируется в PR.

## Исправления жизненного цикла HA после PR #7

11 октября 2026 года: locked restore, Release build без предупреждений и все **264 unit/fixture-теста** прошли на локальном Windows-стенде с SDK 10.0.401. ProfileCheck подтвердил 9 сервисов, 19 fingerprints и 118 шагов, а также содержимое checkout hh-homelab v1.3.0. Это инвентаризация источника, не готовность установщика.

Расширенный HaDockerProbe прошёл дважды на свежих локальных HA Container 2026.10.0 / Docker Linux Engine 29.8.2 с собственными временными ресурсами. Помимо прежних проверок owner, registry, dashboard, HTTP protection и restart подтверждены:

- после отказа проверки имени владельца уже выданный refresh token отозван; metadata API показывает только активную сессию основного клиента;
- второй авторизованный клиент не принимает и не подтверждает идентичный активный pending HTTP trial; исходный клиент с собственным намерением configure может проверить и подтвердить его после reconnect;
- штатный async cleanup второго клиента не оставляет лишнего refresh token;
- успешный ответ `/auth/revoke` намеренно потерян: сообщается неизвестный результат, затем явный повтор cleanup получает нормальный пустой 200;
- после перезапуска и новой авторизации metadata показывает одну сессию wizard, сохранённые owner/registry/dashboard/HTTP policy подтверждены; cleanup ресурсов прошёл.

24 новых регрессионных проверки включают реальные короткие deadlines на ожидающем fake socket, отличие от отмены caller, отсутствие повторной записи, чужой идентичный trial, повторную авторизацию/reconnect, отзыв при ошибке owner/config/token validation, потерю ответа и отказ cleanup с сохранением приватного токена для явного повтора. API клиента изменён с IDisposable на IAsyncDisposable; все имеющиеся потребители переведены на `await using`.

Источник контракта отзыва и metadata: [auth 2026.10.0](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/auth/__init__.py). Используется современный `/auth/revoke`, а не устаревший `action=revoke` на `/auth/token`. Пустой 200 также возвращается для уже отозванного токена; проба дополнительно проверяет число сессий через API metadata.

Намерение HTTP configure пока хранится в памяти одного клиента, не переносится в новый процесс. Журнал M3, аварийное завершение, потерянный ответ выдачи неизвестного токена, config flows и полный профиль остаются открытыми. Реальные DSM/устройства и Avalonia UI этим инкрементом не проверены. Результаты CI следует проверять по конкретному PR, локальная проба не заменяет Linux CI.

## Исходники и версии

[Image evidence](image-evidence.json) содержит публичные OCI digests и metadata загруженных образов, без container environment и credentials. Digests наблюдены при pull на этом стенде, не являются новой закреплённой политикой deploy и требуют проверки архитектуры NAS до использования.

Основные проверенные источники:

- [HA onboarding](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/onboarding/views.py), [config entries](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/config/config_entries.py), [entity registry](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/config/entity_registry.py).
- [HA HTTP WS](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/http/websocket_api.py): `http/config`, `http/config/configure`, `http/config/promote` и штатный restart/pending trial подтверждены HaDockerProbe в описанном выше объёме. Полный сетевой профиль и автоматический rollback таймера не проверены.
- [Jellyfin Startup v12.2](https://github.com/jellyfin/jellyfin/blob/v12.2/Jellyfin.Api/Controllers/StartupController.cs); runtime OpenAPI доступен по `/api-docs/openapi.json`. Нельзя угадывать версию приложения по упаковочному tag; 12.2.0 получена из runtime API.
- [Seerr auth v3.5.0](https://github.com/seerr-team/seerr/blob/v3.5.0/server/routes/auth.ts), [settings](https://github.com/seerr-team/seerr/blob/v3.5.0/server/routes/settings/index.ts).
- [Sonarr OpenAPI](https://github.com/Sonarr/Sonarr/blob/v4.0.20.3014/src/Sonarr.Api.V3/openapi.json), [Radarr OpenAPI](https://github.com/Radarr/Radarr/blob/v6.4.4.10685/src/Radarr.Api.V3/openapi.json), [Prowlarr applications](https://github.com/Prowlarr/Prowlarr/blob/v2.6.5.5623/src/Prowlarr.Api.V1/Applications/ApplicationController.cs).
- [Monitor Docker manifest в закреплённом commit](https://github.com/ualex73/monitor_docker/blob/78abdc40d96871be44bd965202de59fe2e5b10d3/custom_components/monitor_docker/manifest.json), [лицензия](https://github.com/ualex73/monitor_docker/blob/78abdc40d96871be44bd965202de59fe2e5b10d3/LICENSE.md).

## Воспроизведение и следующие пробы

Для нового стенда используйте образы из research-profile, отдельные сеть/метку и свежую конфигурацию. Назначайте порты только на loopback, после каждого запуска/перезапуска заново определяйте port binding. Перед reset/cleanup проверяйте метку владения и отсутствие чужих bind mounts. Не переносите этот сценарий на production и не сохраняйте credentials.

В указанном порядке проверьте onboarding HA и token exchange; status/schema Arr; startup Jellyfin и авторизацию; auth/jellyfin Seerr с hostname Jellyfin внутри общей сети. Синтетические пользователи и пароли создаются заново. После потери ответа сначала наблюдайте состояние: повторный запуск всего списка на уже созданном владельце не является корректным resume.

Дальнейшая работа перечислена в [M0 README](README.md). Повторяемый Docker harness реализован для описанного объёма Media и минимальной конфигурации HA; следующий инкремент — оставшиеся HA config flows/компоненты/рендер/автоматизации. CI ядра выполняет unit/fixture-проверки, отдельные `qb-runtime`, `arr-runtime`, `media-runtime` и `ha-runtime` проверяют реальные сервисы. Наличие workflow не считается доказательством успешного CI; результаты конкретного запуска опубликованы в соответствующем PR.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
