# M0: интерактивные config flows DSM, LG, Android TV и Cast

Контракт закреплён на **Home Assistant Container 2026.10.0** профиля hh-homelab v1.3.0. Это C#-адаптер и developer probe. Application, пользовательский мастер и Avalonia UI ещё не созданы; работа с настоящими DSM/TV не подтверждена.

## Реализованный объём

`HomeAssistantContractClient.Devices.cs` запускает USER flow через REST API и продолжает его только по явному вызову соответствующего метода. `HomeAssistantDeviceProgress` возвращает этап, тип ожидаемого ввода, безопасный код проблемы, entry ID и технические значения доступных backup shares. Пароли, PIN, OTP, client secrets, defaults, description placeholders и полный ответ HA в DTO не попадают.

| Интеграция | Последовательность | Действие пользователя |
| --- | --- | --- |
| Synology DSM | `user` → при необходимости `2sa` → при наличии writable shares `backup_share` → создание entry | Ввести доступ и порт/SSL; одноразовый код; явно выбрать backup share и имя подкаталога |
| LG webOS | `user` с конкретным host → `pairing` → создание entry | Запустить сопряжение в мастере и принять запрос на телевизоре |
| Android TV Remote | `user` с конкретным host → `pair` → создание entry | Ввести PIN с экрана приставки; неверный PIN допускает явное исправление |
| Google Cast | `config` → создание общей entry | Передать выбранные known hosts или пустой список; конкретное устройство выбрать отдельно |

DSM SSL использует `verify_ssl=true`; отключение проверки сертификата не предоставляется. Ошибка сертификата/подключения не обходится автоматическим изменением сети. Host передаётся отдельно от порта, без URL, userinfo и пути. Поддерживаются DNS/IP; scoped IPv6 в этом ограниченном контракте не поддерживается. OTP ограничен шестью цифрами, Android PIN — ASCII hex. Имя backup-подкаталога ограничено 64 символами ASCII letters/digits/`-`/`_`. Это ограничения M0-адаптера, а не заявление обо всех допустимых значениях HA.

Backup share выбирается по `selector.select.options[].value`, без сопоставления по локализованной подписи. Перед отправкой клиент заново читает форму и проверяет присутствие выбранного значения; пустые, повторяющиеся и небезопасные пути блокируют продолжение. Ни первая папка, ни имя из default не назначаются автоматически.

Known hosts Cast являются подсказками обнаружения. Они не ограничивают список устройств до GX1, не доказывают соответствие Android Remote и не устраняют лишние плееры. Назначение аппаратных ролей остаётся отдельным [контрактом](HA_ENTITY_ROLES.md).

## Состояния и повторный запуск

| Состояние | Значение |
| --- | --- |
| `AwaitingInput` | Получена допустимая форма; требуется указанный тип ввода |
| `AwaitingSetup` | Получен entry ID, но HA ещё сообщает `setup_in_progress` / `setup_retry` |
| `EntryLoaded` | Entry ожидаемого source существует, включена и имеет state `loaded`; физическая идентичность и рабочие сущности не доказаны |
| `OutcomeUnknown` | Запись могла выполниться, но ответ не подтверждён; автоматическая повторная отправка запрещена |
| `Aborted` | Подтверждён abort, например `already_configured` или `cannot_connect`; он не считается успешным подключением |

Клиент проверяет текущую owner session, domain, flow ID, ожидаемый step, набор schema fields, entry source/state и наличие конфликтующих entries. При изменении host для существующего намерения возвращается конфликт. Новый USER start блокируется при любой entry того же domain и при обнаруженном discovery/reauth flow. Явно выбранный discovery допускает отдельный [контракт продолжения](HA_DISCOVERY_BINDING.md); существующая entry не присваивается, не удаляется и не перенастраивается.

Перед credentials/подтверждением сопряжения entry list читается ещё раз. Это проверка конфликта, а не атомарная блокировка: вызывающий сценарий обязан сериализовать операции и исключать конкурирующую настройку через HA UI. `config_entries/flow/progress` не возвращает другие USER flows, поэтому не защищает от второго клиента. Маркер собственного HA и общий lock/journal относятся к M3.

Намерение записывается в память до POST. Потеря ответа, отмена, timeout и неподдерживаемый ответ после отправки не разрешают новый start или повторный submit. Даже существующая entry с тем же названием не доказывает, что её создал именно этот запрос. После известного entry ID клиент повторяет только чтение; `ObserveDeviceEntryAsync` позволяет явное read-only наблюдение после restart, без присвоения entry и разрешения новых записей.

GET текущего flow в HA повторно вызывает обработчик шага и может очистить errors. Поэтому подтверждённый отказ credentials/PIN остаётся видимым до явного исправления. Для DSM `2sa` закреплённый обработчик ожидает dict: GET вызывает его с `None` и приводит к `AttributeError`. Клиент сохраняет подтверждённую OTP-форму в памяти и продолжает её POST; GET этого шага не выполняется. При истёкшем flow POST может вернуть ошибку, после которой исход остаётся неизвестным. Это учёт поведения выбранной версии, без правки HA или `.storage`.

При неизвестном результате клиент не вызывает flow GET: это не пассивное чтение и оно не даёт достаточного подтверждения исхода. Общего recovery, отмены/удаления незавершённых flows и восстановления намерения после перезапуска процесса пока нет. Авторизация и закрытие сессии следуют существующему контракту `IAsyncDisposable`; требуется `await using`, а для обязательного подтверждения отзыва — `RevokeSessionAsync`.

## Проверки

Добавлены **54 unit/fixture-теста**, общий набор — **446**. Проверяются явные шаги четырёх интеграций, OTP и backup selector, сохранение отказа после GET, исправление ввода, конфликты flow/entry/schema/host, setup pending, abort, потеря start/host/PIN/OTP/final response, отмена всех четырёх конечных submit, запрет повторов и ограниченный DTO. Fixtures не обращаются к DSM или телевизору.

Расширенная HaDockerProbe на своём свежем HA/Jellyfin стенде подтвердила:

- настоящие начальные USER forms DSM/LG/Android TV и переход LG host → confirmation без начала соединения с телевизором;
- настоящие DSM OTP/backup forms и их сериализацию на синтетической writable share, а также проблему `2sa(None)` внутри закреплённого образа;
- настоящий Cast USER flow с пустыми discovery hints, loaded entry, повтор без flow POST и сохранение после restart;
- прежние onboarding/Jellyfin/registry/dashboard/HTTP/revocation проверки и cleanup своих ресурсов.

До исправления допуска `errors: null` контейнерная проба завершилась ошибкой на LG initial form; ресурсы обоих диагностических запусков удалены. После исправления полный локальный сценарий завершился успешно. CI `ha-runtime` повторяет его дважды на новых конфигурациях; результат конкретного head публикуется в PR.

Команды из корня репозитория, после locked restore/build:

```text
dotnet test HhHomelab.Wizard.slnx --no-build --configuration Release
dotnet run --project tools/HhHomelab.Wizard.HaDockerProbe --no-build --configuration Release -- profiles/hh-homelab/1.3.0/research-profile.json
```

Python на клиентском компьютере не нужен: проверка нативных DSM forms выполняется штатным Python внутри собственного HA-контейнера. Ничего не подключается к настоящему NAS/TV. Cast integration может выполнять штатное discovery в сети стенда; пустые hints не являются режимом изоляции сети.

## Оставшиеся работы

Полное сопряжение DSM/LG/GX1 требует аппаратной проверки. [Выбор discovery и технический registry binding](HA_DISCOVERY_BINDING.md) реализованы отдельным инкрементом. До готовности установщика остаются их аппаратное подтверждение, безопасное восстановление намерений, активация и первые показания DSM, выбор Cast/Jellyfin клиента, apply/recovery всех ролей, анализ зависимостей лишних плееров и полный dashboard. `EntryLoaded` не заменяет эти проверки и не подтверждает воспроизведение/управление.

## Источники закреплённого контракта

- [REST config entries/flows](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/config/config_entries.py) и [GET/POST flow resource](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/helpers/data_entry_flow.py).
- [Synology DSM](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/synology_dsm/config_flow.py), [LG webOS](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/webostv/config_flow.py), [Android TV Remote](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/androidtv_remote/config_flow.py), [Cast](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/cast/config_flow.py).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
