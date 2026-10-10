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

## Исходники и версии

[Image evidence](image-evidence.json) содержит публичные OCI digests и metadata загруженных образов, без container environment и credentials. Digests наблюдены при pull на этом стенде, не являются новой закреплённой политикой deploy и требуют проверки архитектуры NAS до использования.

Основные проверенные источники:

- [HA onboarding](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/onboarding/views.py), [config entries](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/config/config_entries.py), [entity registry](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/config/entity_registry.py).
- [HA HTTP WS](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/http/websocket_api.py): предусмотрены `http/config`, `http/config/configure`, `http/config/promote`; настройка может инициировать restart и требует подтверждения pending config. Пока это source evidence, без runtime-проверки WS.
- [Jellyfin Startup v12.2](https://github.com/jellyfin/jellyfin/blob/v12.2/Jellyfin.Api/Controllers/StartupController.cs); runtime OpenAPI доступен по `/api-docs/openapi.json`. Нельзя угадывать версию приложения по упаковочному tag; 12.2.0 получена из runtime API.
- [Seerr auth v3.5.0](https://github.com/seerr-team/seerr/blob/v3.5.0/server/routes/auth.ts), [settings](https://github.com/seerr-team/seerr/blob/v3.5.0/server/routes/settings/index.ts).
- [Sonarr OpenAPI](https://github.com/Sonarr/Sonarr/blob/v4.0.20.3014/src/Sonarr.Api.V3/openapi.json), [Radarr OpenAPI](https://github.com/Radarr/Radarr/blob/v6.4.4.10685/src/Radarr.Api.V3/openapi.json), [Prowlarr applications](https://github.com/Prowlarr/Prowlarr/blob/v2.6.5.5623/src/Prowlarr.Api.V1/Applications/ApplicationController.cs).
- [Monitor Docker manifest в закреплённом commit](https://github.com/ualex73/monitor_docker/blob/78abdc40d96871be44bd965202de59fe2e5b10d3/custom_components/monitor_docker/manifest.json), [лицензия](https://github.com/ualex73/monitor_docker/blob/78abdc40d96871be44bd965202de59fe2e5b10d3/LICENSE.md).

## Воспроизведение и следующие пробы

Для нового стенда используйте образы из research-profile, отдельные сеть/метку и свежую конфигурацию. Назначайте порты только на loopback, после каждого запуска/перезапуска заново определяйте port binding. Перед reset/cleanup проверяйте метку владения и отсутствие чужих bind mounts. Не переносите этот сценарий на production и не сохраняйте credentials.

В указанном порядке проверьте onboarding HA и token exchange; status/schema Arr; startup Jellyfin и авторизацию; auth/jellyfin Seerr с hostname Jellyfin внутри общей сети. Синтетические пользователи и пароли создаются заново. После потери ответа сначала наблюдайте состояние: повторный запуск всего списка на уже созданном владельце не является корректным resume.

Дальнейшая работа перечислена в [M0 README](README.md). Повторяемый Docker harness реализован для описанного объёма qBittorrent и Arr; расширение на Jellyfin/Seerr и HA ещё предстоит добавить. CI ядра выполняет unit/fixture-проверки, отдельные `qb-runtime` и `arr-runtime` проверяют реальные сервисы.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
