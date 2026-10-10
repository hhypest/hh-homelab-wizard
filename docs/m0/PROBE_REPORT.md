# Протокол локальных проб M0

Дата: 2026-10-10. Среда: Windows, .NET SDK 10.0.401, Docker Desktop с Linux Engine **29.8.2**, linux/amd64. Это не NAS, не испытание DSM и не проверка Avalonia.

Создана отдельная bridge-сеть и девять временных контейнеров с меткой `com.hh-homelab-wizard.session=m0-20261010-a1`. Опубликованные порты привязаны к 127.0.0.1 и назначены Docker динамически. Конфигурация временная, медиатека и каталог реального hh-homelab не монтировались. Для Docker Socket Proxy использовался только read-only socket с `POST=0` и ограниченными GET-категориями. Accounts/credentials синтетические; значения не входят в протокол.

## Подтверждённый объём

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

## qBittorrent: незавершённая проба

Образ профиля запущен; `/app/qbittorrent-nox --version` подтвердил **v5.2.4**. Успешный bootstrap/login на стенде пока не получен: попытки с временным паролем и с подготовленной PBKDF2-конфигурацией не подтвердили аутентификацию. Необработанная HTTP-ошибка, таймаут при устаревшем динамическом порте и результат отказа credentials различаются; ни один не записан как успех. Причину отказа ещё нужно локализовать. Это не доказательство ошибки самого qBittorrent.

При исследовании [authcontroller.cpp выбранной версии](https://github.com/qbittorrent/qBittorrent/blob/release-5.2.4/src/webui/api/authcontroller.cpp) и [формирования HTTP-ответа](https://github.com/qbittorrent/qBittorrent/blob/release-5.2.4/src/webui/webapplication.cpp) обнаружено отличие от старого описания WebUI API: успешный login без data получает 204, неверные credentials — 401. Поэтому клиент для этого профиля принимает 204 и обязательно проверяет последующий авторизованный GET версии. Текст `Ok.` со статусом 200 отклоняется. Поведение ошибки 401 наблюдалось; успешный путь 204 подтверждён source и unit-fixtures, **ещё не runtime-пробой**.

QbProbe на C# сохраняется как диагностический инструмент с безопасными error codes и передачей credentials через stdin. Пока он не является готовым адаптером первичной настройки. Смена пароля, preferences и categories не считаются проверенными.

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

Дальнейшая работа перечислена в [M0 README](README.md). Полный автоматический harness для этих live-проб, fault injection и повторяемый bootstrap всех сервисов ещё предстоит добавить; обычный CI пока выполняет unit/fixture-проверки без Docker.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
