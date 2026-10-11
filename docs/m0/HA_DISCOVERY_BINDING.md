# M0: выбор обнаруженной интеграции и техническая привязка устройства

Профиль: hh-homelab v1.3.0, HA Container **2026.10.0**. Реализованы ограниченный discovery adapter, чистая проверка registry binding и расширение developer probe. Пользовательский мастер ещё не создан; настоящее оборудование не подключалось.

## Явный выбор discovery flow

`GetDiscoveredDeviceFlowsAsync` читает WebSocket `config_entries/flow/progress` и возвращает только flow ID, тип интеграции, допустимый discovery source, исходный step и технический `context.unique_id`. Полный context, titles, description placeholders, credentials и схемы форм не экспортируются. Другие интеграции, reauth/reconfigure и уже продвинувшиеся steps не предлагаются как новый выбор.

| Интеграция | Source | Исходный step | Discovery unique ID | Продолжение |
| --- | --- | --- | --- | --- |
| DSM | ssdp / zeroconf | link | MAC без разделителей, uppercase | credentials → при необходимости OTP / backup share |
| LG | ssdp | pairing | device UUID | явная LG confirmation |
| Android TV Remote | zeroconf | zeroconf_confirm | MAC с двоеточиями, lowercase | явная confirmation → PIN |
| Cast | zeroconf | confirm | строка `cast` | явная confirmation общей интеграции |

Ни первый flow, ни имя устройства не выбираются автоматически. `SelectDiscoveredDeviceFlowAsync` требует переданный пользователем снимок и повторно сверяет все его поля. Незавершённый onboarding и любая entry того же domain блокируют выбор. Особенно важно условие onboarding для Cast: его GET confirm до завершения onboarding может сам создать entry в закреплённом HA.

Намерение сохраняется до GET выбранного flow, поскольку GET повторно вызывает обработчик шага. Потеря ответа, отмена или неподдерживаемый результат не разрешают второй выбор, start или submit. Перед последующим чтением формы/записью проверяются flow ID, handler, source, unique ID и ожидаемый step; удалённый или изменённый процесс блокирует сопряжение. Это проверка снимка, не атомарный lock: вызывающий сценарий обязан исключать параллельную настройку в HA UI.

DSM `link` использует адрес, уже полученный HA из discovery. Клиент проверяет и хранит этот host только в памяти; credentials не подменяют его. Если подтверждённый ответ после OTP возвращает DSM `user` form, повтор credentials передаёт только ранее подтверждённый host. Изменённый host блокирует запись. OTP GET по-прежнему не выполняется из-за [контракта закреплённой версии](HA_DEVICE_FLOWS.md).

Созданная entry сохраняет source ssdp/zeroconf. Клиент проверяет соответствующий source при readback; USER observation не присваивает discovery entry. `ObserveDiscoveredDeviceEntryAsync` допускает явное чтение после restart по ожидаемому source/entry ID, без разрешения продолжать записи и без доказательства physical identity. Нового USER flow для выбранного discovery не создаётся.

## Проверка entry/device/identifier

`HomeAssistantDeviceBindingVerifier` — чистое правило Domain; `ObserveDeviceBindingAsync` получает свежий ограниченный inventory и применяет его. Для проверки нужны явные integration, entry ID, device ID и ожидаемый технический identifier. Переданные IDs сверяются независимо от отображаемого имени и списка плееров.

| Интеграция | Device registry identifier |
| --- | --- |
| DSM | `synology_dsm` + serial именно корневого NAS; дочерний storage device не допускается |
| LG | `webostv` + точный deviceUUID |
| Android TV Remote | `androidtv_remote` + MAC в форме lowercase с двоеточиями; допустимые входные MAC нормализуются |
| Cast | `cast` + UUID N без дефисов; входной UUID D/N нормализуется |

У Cast `entity.unique_id` имеет UUID с дефисами, а device identifier — без них. Discovery unique ID `cast` относится к общей интеграции и не является идентификатором GX1. MAC discovery DSM также не заменяет serial корневого NAS.

Результаты различают `EntryMissing`, `EntryPending`, `EntryFailed`, `EntryDisabled`, `DeviceMissing`, `DeviceDisabled`, `IdentityMismatch`, `Ambiguous` и `Matched`. Дубли entry/device IDs, несколько устройств с одним identifier или несколько identifiers одного domain у выбранного устройства не дают Match. Выбранное device должно принадлежать выбранной loaded/enabled entry; DSM serial не сопоставляется с другим NAS или дочерним устройством.

`Matched` подтверждает только согласованность реестра с явным ожиданием. Это не показания, доступность, криптографическое доказательство устройства или проверка управления. Отдельные MAC Android и UUID Cast не позволяют автоматически доказать, что это одна GX1; связь подтверждается пользователем. Проверка не включает и не переименовывает сущности, не отключает лишние плееры. [План ролей](HA_ENTITY_ROLES.md) и аппаратные функциональные проверки остаются отдельными операциями.

## Проверки и ограничения

Добавлена 51 unit/fixture-проверка; общий набор — 497. Проверяются продолжение исходных discovery flows, отсутствие новых starts, выбор второго кандидата, source readback, изменение/удаление snapshot, отказ на незавершённом onboarding/existing entry, потеря submit/selection GET, отмена selection, DSM host/OTP continuation, исключение reauth и ограниченный DTO. Domain отдельно проверяет все binding states, MAC/UUID форматы, связи родителей и неоднозначность.

Расширенная HaDockerProbe выполняет обработчики начальных discovery forms и serializer progress непосредственно из закреплённого образа с синтетическими metadata. LG entry check и Cast onboarding в этой проверке заменены ограниченными test doubles; сеть и сопряжение не выполняются. Нативные конструкторы Cast/Android entities подтверждают формы identifiers на синтетических данных в отдельном Python-процессе; аппаратные entities в сервере не регистрируются.

Сам HA сервер проверяется через настоящий read-only progress/inventory API. Loaded Cast entry с отсутствующим device остаётся `DeviceMissing`. Прежние USER forms, loaded Cast/Jellyfin, registry/dashboard/HTTP/restart/revocation и cleanup остаются в пробе. Завершение настоящего ssdp/zeroconf discovery flow на NAS/TV этой проверкой не подтверждается.

Общий журнал и восстановление после перезапуска процесса остаются M3/M5. Неизвестный config-flow outcome блокируется, но не разрешается подбором entry по имени или повтором credentials. Привязка устройства не закрывает M0 и не блокирует создание демонстрационного [каркаса M1](../adr/0004-m1-entry.md).

Команды после locked restore/build:

```text
dotnet test HhHomelab.Wizard.slnx --no-build --configuration Release
dotnet run --project tools/HhHomelab.Wizard.HaDockerProbe --no-build --configuration Release -- profiles/hh-homelab/1.3.0/research-profile.json
```

Python нужен только внутри собственного HA-контейнера. NAS credentials в репозиторий, вывод, PR и support bundle не входят.

## Источники

- [Config entries API и progress](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/config/config_entries.py), [flow progress serializer](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/data_entry_flow.py).
- [DSM discovery](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/synology_dsm/config_flow.py), [LG](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/webostv/config_flow.py), [Android](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/androidtv_remote/config_flow.py), [Cast](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/cast/config_flow.py).
- [Cast device/unique IDs](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/cast/media_player.py), [Android device](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/androidtv_remote/entity.py), [LG device](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/webostv/entity.py), [DSM parent/storage devices](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/synology_dsm/entity.py).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
