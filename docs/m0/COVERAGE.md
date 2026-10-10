# Покрытие исходной инструкции

Источник: [hh-homelab v1.3.0](https://github.com/hhypest/hh-homelab/tree/v1.3.0), commit `b05047b081f3543520582315002e359ef1d93e59`. Все 55 шагов Media и 63 шага HA сопоставлены с будущими операциями. **Все операции установки пока NotImplemented.** Это план покрытия, а не результат выполнения.

Automatic — предполагаемая автоматическая операция; Interactive — выбор или действие пользователя плюс операция; Guidance — объяснение/prerequisite; Optional — отключаемый модуль; Excluded — вне первого выпуска; Deferred — после первого выпуска. HTTP API наличие само по себе не доказывает работоспособность шага на NAS.

Количество: Automatic 48, Guidance 10, Interactive 31, Optional 11, Deferred 4, Excluded 14.

Номера соответствуют заголовкам [Media](https://github.com/hhypest/hh-homelab/blob/v1.3.0/docs/media-stack.html) и [HA](https://github.com/hhypest/hh-homelab/blob/v1.3.0/docs/index.html). Машинный список — [research-profile.json](../../profiles/hh-homelab/1.3.0/research-profile.json).

| Шаг | Исходное действие | Режим | Будущая операция |
| --- | --- | --- | --- |
| media.1.1 | Создать структуру | Automatic | filesystem |
| media.1.2 | Понять, почему монтирование именно такое | Guidance | filesystem.explain |
| media.1.3 | Убедиться, что хардлинки работают | Automatic | filesystem |
| media.2.1 | Склонировать репозиторий | Automatic | media.deploy |
| media.2.2 | Заполнить .env | Automatic | media.deploy |
| media.2.3 | Создать каталоги состояния | Automatic | media.deploy |
| media.2.4 | Поднять стек | Automatic | media.deploy |
| media.2.5 | Проверить, что тома смонтированы верно | Automatic | media.deploy |
| media.3.1 | Найти временный пароль и сменить его | Interactive | qb.bootstrap |
| media.3.2 | Включить VueTorrent | Automatic | qb.configure |
| media.3.3 | Задать пути загрузок | Automatic | qb.configure |
| media.3.4 | Создать категории movies и tv | Automatic | qb.configure |
| media.3.5 | Ограничить число соединений и скорость | Interactive | qb.limits |
| media.3.6 | Настроить раздачу | Interactive | qb.seeding |
| media.4.1 | Проверить, что отвечает | Automatic | flare.verify |
| media.4.2 | Решить, нужен ли он вообще | Interactive | flare.selection |
| media.4.3 | Знать, что красная кнопка Test врёт | Guidance | flare.test-caveat |
| media.5.1 | Включить аутентификацию | Automatic | prowlarr.configure |
| media.5.2 | Добавить индексаторы | Interactive | indexers.select |
| media.5.3 | Подложить своё определение индексатора | Optional | indexers.custom-definition |
| media.5.4 | Привязать FlareSolverr | Automatic | prowlarr.configure |
| media.5.5 | Связать с Radarr | Automatic | prowlarr.configure |
| media.5.6 | Убедиться, что индексаторы доехали | Automatic | prowlarr.configure |
| media.6.1 | Аутентификация и корневая папка | Automatic | arr.configure |
| media.6.2 | Включить хардлинки | Automatic | arr.configure |
| media.6.3 | Профиль качества под ваш телевизор | Interactive | radarr.quality |
| media.6.4 | Подключить qBittorrent | Automatic | arr.configure |
| media.6.5 | Уведомления в Пачку | Optional | notifications.radarr |
| media.6.6 | Добавить первый фильм | Interactive | media.test-film |
| media.6.7 | Sonarr: вход, корень библиотеки и хардлинки | Automatic | arr.configure |
| media.6.8 | Sonarr: качество, имена и отслеживание | Interactive | sonarr.quality |
| media.6.9 | Sonarr: подключить qBittorrent | Automatic | arr.configure |
| media.6.10 | Prowlarr: синхронизировать индексаторы с Sonarr | Automatic | arr.configure |
| media.6.11 | Sonarr: уведомления в Пачку | Optional | notifications.sonarr |
| media.6.12 | Sonarr: первый сериал и проверка поиска | Interactive | media.test-series |
| media.6.13 | Sonarr: подтвердить импорт и библиотеку Jellyfin | Automatic | arr.configure |
| media.6.14 | Sonarr: проверить надзор и журнал | Automatic | arr.configure |
| media.7.1 | Пройти мастер и добавить библиотеку | Interactive | jellyfin.onboarding-libraries |
| media.7.2 | Транскодирование: оставить выключенным | Automatic | jellyfin.configure |
| media.7.3 | Отключить лишнее | Automatic | jellyfin.configure |
| media.7.4 | Завести пользователя для приставки | Interactive | jellyfin.users |
| media.7.5 | Настроить клиент на приставке | Interactive | gx1.jellyfin-client |
| media.7.6 | Снять отчёт о возможностях приставки | Interactive | gx1.capabilities |
| media.8.1 | Подключить Jellyfin | Interactive | seerr.jellyfin-login |
| media.8.2 | Подключить Radarr и Sonarr | Automatic | seerr.configure |
| media.8.3 | Пользователи и права | Interactive | seerr.permissions |
| media.8.4 | Проверить фильтры витрины | Interactive | seerr.filters |
| media.8.5 | Уведомления в Пачку | Optional | notifications.seerr |
| media.9.1 | Запросить фильм и проследить путь | Interactive | media.request-test |
| media.9.2 | Проверить, что импорт прошёл хардлинком | Automatic | media.verify |
| media.10.1 | Включить встроенные бэкапы | Interactive | arr.backup-settings |
| media.10.2 | Порядок обновления образов | Deferred | updates.images |
| media.10.3 | Забрать изменения конфигурации | Deferred | updates.configuration |
| media.10.4 | Следить за местом | Automatic | media.maintenance |
| media.10.5 | Раз в квартал заглядывать в Prowlarr | Guidance | indexers.maintenance |
| ha.1.1 | Включить SSH на NAS | Guidance | nas.ssh-prerequisite |
| ha.1.2 | Склонировать репозиторий | Automatic | nas.preflight |
| ha.1.3 | Проверить, что порты свободны | Automatic | nas.preflight |
| ha.1.4 | Завести служебного пользователя hass | Interactive | nas.service-account |
| ha.1.5 | Внести NAS в белый список автоблокировки | Guidance | nas.autoblock-policy |
| ha.2.1 | Убедиться, что файлы на месте | Automatic | ha.deploy |
| ha.2.2 | Заполнить .env | Automatic | ha.deploy |
| ha.2.3 | Заполнить secrets.yaml | Automatic | ha.deploy |
| ha.2.4 | Собрать проект в Container Manager | Automatic | ha.deploy |
| ha.2.5 | Дождаться первого старта | Automatic | ha.deploy |
| ha.2.6 | Проверить docker-socket-proxy | Automatic | ha.deploy |
| ha.2.7 | Проверить настройки HTTP в интерфейсе | Automatic | ha.deploy |
| ha.3.1 | Создать владельца | Interactive | ha.owner-onboarding |
| ha.3.2 | Проверить конфигурацию | Automatic | ha.bootstrap |
| ha.3.3 | Убедиться, что пакеты загрузились | Automatic | ha.bootstrap |
| ha.3.4 | Установить HACS | Optional | components.hacs |
| ha.3.5 | Поставить мобильное приложение | Optional | ha.mobile-app |
| ha.4.1 | Подключить Synology DSM | Interactive | ha.synology-flow |
| ha.4.2 | Поднять docker-socket-proxy | Automatic | ha.monitoring |
| ha.4.3 | Установить Monitor Docker | Automatic | ha.monitoring |
| ha.4.4 | Свериться с HTTP-проверками | Automatic | ha.monitoring |
| ha.4.5 | Подключить прикладные интеграции стека | Interactive | ha.media-flows |
| ha.4.6 | Поправить имена сенсоров в пакетах | Automatic | ha.monitoring |
| ha.5.1 | Создать чат «Мониторинг» | Excluded | notifications.external-chat |
| ha.5.2 | Создать бота и получить URL | Excluded | notifications.external-bot |
| ha.5.3 | Вписать URL и перезагрузить конфигурацию | Optional | notifications.webhook |
| ha.5.4 | Отправить тестовое сообщение | Optional | notifications.test |
| ha.5.5 | Что теперь будет приходить само | Optional | notifications.events |
| ha.5.6 | Посмотреть утреннюю сводку заранее | Optional | notifications.report |
| ha.6.1 | Подготовить телевизор | Guidance | lg.prerequisites |
| ha.6.2 | Добавить интеграцию | Interactive | ha.webos-pairing |
| ha.6.3 | Привести entity_id к media_player.lg_tv | Automatic | lg |
| ha.6.4 | Проверить включение | Interactive | lg.power-test |
| ha.6.5 | Проверить, на какой вход встаёт телевизор | Interactive | lg.input-test |
| ha.6.6 | Уведомления на экран | Optional | lg.notification-test |
| ha.7.1 | Закрепить адрес 192.168.3.54 | Guidance | gx1.address-prerequisite |
| ha.7.2 | Сопрячь с Home Assistant | Interactive | ha.androidtv-pairing |
| ha.7.3 | Привести идентификаторы к ожидаемым | Automatic | gx1 |
| ha.7.4 | Узнать реальное имя пакета Jellyfin | Interactive | gx1.package-detection |
| ha.7.5 | Настроить «Продолжить просмотр» | Interactive | jellyfin.resume-preferences |
| ha.7.6 | Проверить сценарии по очереди | Interactive | tv.functional-tests |
| ha.7.7 | Проверить ночное автоотключение | Interactive | tv.night-tests |
| ha.7.8 | Включить SIMPLINK, если ещё не включён | Guidance | lg.simplink-prerequisite |
| ha.8.1 | Убедиться, что HACS на месте | Excluded | cloud |
| ha.8.2 | Поставить Yandex Smart Home | Excluded | cloud |
| ha.8.3 | Настроить облачное подключение | Excluded | cloud |
| ha.8.4 | Выбрать, что отдать Алисе | Excluded | cloud |
| ha.8.5 | Привязать навык в «Доме с Алисой» | Excluded | cloud |
| ha.8.6 | Проверить голосом | Excluded | cloud |
| ha.8.7 | Спросить у Алисы температуру | Excluded | cloud |
| ha.8.8 | Научить Алису произносить отчёт | Excluded | cloud |
| ha.9.1 | Понять, где сейчас ходит трафик NAS | Excluded | network |
| ha.9.2 | Если что-то не отвечает | Excluded | network |
| ha.9.3 | Добавить резервный DNS | Excluded | network |
| ha.9.4 | Открыть доступ снаружи | Excluded | network |
| ha.10.1 | Создать панель и вставить YAML | Automatic | dashboard |
| ha.10.2 | Что где лежит | Guidance | dashboard.explain |
| ha.10.3 | Поправить два места под себя | Automatic | dashboard |
| ha.11.1 | Включить встроенные резервные копии | Interactive | ha.backup-settings |
| ha.11.2 | Добавить папку в Hyper Backup | Guidance | nas.hyperbackup |
| ha.11.3 | Порядок обновления образа | Deferred | updates.ha-image |
| ha.11.4 | Забрать изменения конфигурации | Deferred | updates.ha-configuration |
| ha.11.5 | Следить за размером базы | Automatic | ha.maintenance |

Отдельные межсервисные действия (например, Prowlarr → Sonarr) проверяются независимо, даже если объединены в исходном шаге. Guidance не изменяет роутер/DSM firewall. HACS необязателен при выбранной прямой доставке Monitor Docker; облачные компоненты не устанавливаются. Создание внешнего бота исключено, предоставленный пользователем webhook — опция.

Названия шагов воспроизведены из hh-homelab, © 2026 hhypest, CC BY 4.0. Этот документ — [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt).
