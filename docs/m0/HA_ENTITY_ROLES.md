# M0: реестры HA, датчики DSM и роли устройств

Добавлены read-only `GetRoleInventoryAsync`, чистый `HomeAssistantRolePlanner` и ограниченный `EnsureEntityEnabledAsync` для Home Assistant Container **2026.10.0**, профиля hh-homelab **v1.3.0**. Это контракты Domain/Infrastructure, не готовый экран или массовый установщик. Config flows DSM/LG/Android TV/Cast, полноценный dashboard, журнал и продуктовый resume остаются открытыми; M0 не закрыт.

## Наблюдение и выбор

Через `config/entity_registry/list`, `config/device_registry/list` и GET `api/config/config_entries/entry` читаются технические идентичности. Модель содержит registry/entity/unique ID, platform, config entry/device IDs, translation key, disabled/hidden reasons, identifiers, связи родительских устройств и состояние записи интеграции. Имена, options, config data, passwords, токены и полные ответы API не возвращаются. Дубликаты registry/entity/device/entry IDs блокируют снимок.

Для NAS вызывающий сценарий явно передаёт выбранные config entry, корневое устройство, serial и технические storage IDs тома и двух дисков. Номера физических отсеков не выводятся из имени устройства: соответствие выбранных IDs ролям Drive 1/2 должно быть подтверждено при выборе. Для плееров передаются registry IDs выбранных сущностей. Сейчас UI выбора ещё нет; случайный первый плеер и похожее отображаемое имя не используются.

## План десяти ролей

| Роль | Требуемый entity_id | Техническое основание |
| --- | --- | --- |
| Температура NAS | `sensor.ds725_temperature` | DSM `temperature`, API `SYNO.DSM.Info`, корневое устройство |
| CPU NAS | `sensor.ds725_cpu_utilization_total` | DSM `cpu_total_load`, API `SYNO.Core.System.Utilization`, корневое устройство |
| Занято тома | `sensor.ds725_volume_1_volume_used` | DSM `volume_percentage_used`, API `SYNO.Storage.CGI.Storage`, выбранный том |
| Статус диска 1 | `sensor.ds725_drive_1_status` | DSM `disk_status`, API Storage, выбранный диск 1 |
| Статус диска 2 | `sensor.ds725_drive_2_status` | DSM `disk_status`, API Storage, выбранный диск 2 |
| LG | `media_player.lg_tv` | Явный выбор `media_player` / `webostv` |
| GX1 | `media_player.rocktek_gx1` | Явный выбор `media_player` / `androidtv_remote` |
| Remote GX1 | `remote.rocktek_gx1` | Явный выбор `remote` / `androidtv_remote`; то же устройство и config entry, что у плеера GX1 |
| Cast GX1 | `media_player.rocktek_gx1_cast` | Явный выбор `media_player` / `cast` |
| Jellyfin GX1 | `media_player.jellyfin_rocktek_gx1` | Явный выбор `media_player` / `jellyfin` |

DSM сопоставляется одновременно по platform, config entry, устройству, `translation_key` и точному unique ID: `serial_API:key`, для storage — дополнительно `_storageID`. Дочерний том/диск должен иметь identifier `serial_storageID` и `via_device_id` выбранного NAS. Serial другого NAS, другие API/метрики и другие тома/диски не принимаются. Для занятости требуется процент; размер использованных данных в байтах не подходит, несмотря на похожее имя.

Каждая запись интеграции должна быть `loaded`, правильного domain и не выключена. Устройство должно быть включено и связано с этой записью. Неоднозначная метрика, отсутствующая сущность, занятый целевой ID, неверная платформа или другой GX1 remote блокируют план. `CanApply` истинно только при наличии всех десяти действий без проблем. План содержит исходные идентичности, целевые IDs и необходимость включения; запись сам планировщик не выполняет.

Cast и клиент Jellyfin выбираются явно. Совпадение с физической приставкой этим планом автоматически не доказано; нужны подтверждение пользователя и проверка управления/просмотра на устройстве. Monitor Docker без registry unique ID не попадает в этот механизм переименования.

## Включение выбранной сущности

`EnsureEntityEnabledAsync` заново наблюдает выбранную registry identity и допускает очистку только `disabled_by=user` или `integration`. `config_entry`, `device`, `hass` и неизвестные причины блокируются. Если у сущности есть parent entry/device, проверяются loaded/domain/disabled и их связь; после дополнительных чтений сущность повторно сверяется перед записью. Конкурентные операции вызывающий сценарий должен сериализовать: API не предоставляет общей транзакции снимка и update.

Отправляется только `config/entity_registry/update` с `disabled_by=null`, затем реестр перечитывается. Результаты:

- `Unchanged`: выбранная registry identity уже включена; конфигурационный update не отправлялся;
- `RestartRequired`: HA подтвердил включение и сообщил о необходимости перезапуска;
- `ReloadScheduled`: HA подтвердил включение и сообщил о запланированной перезагрузке config entry.

Ни один из этих результатов не подтверждает рабочее показание. После reload/restart нужны наблюдение состояния и проверки метрики. Если ответ потерян или ожидание отменено, транспорт не повторяет update: после reconnect вызывающий сценарий сначала наблюдает реестр. При уже применённом включении повтор не пишет. Если способ активации в ответе неизвестен, возвращается безопасная ошибка, а не выдуманное требование restart/reload. После неизвестного исхода способ активации может остаться неизвестным даже при включённой записи; проверка live state обязательна.

Для переименования используется существующий `EnsureEntityIdAsync` после нового чтения актуальной идентичности. Исходный snapshot с прежним `DisabledBy` не переносится в rename вслепую. Здесь ещё нет общего apply/recovery engine для десяти операций.

Автоматическое отключение лишних плееров и изменение `hidden_by` не реализованы: сначала нужен анализ ссылок в YAML, UI, dashboard и динамических шаблонах. Нет изменения `.storage`, пересоздания интеграции или создания фиктивного DSM-датчика.

## Проверки и воспроизведение

49 новых unit/fixture-проверок, всего **392**. Проверяются точные метрики/API/serial/storage IDs, неверный NAS, неоднозначность, коллизии, loaded/disabled parents, GX1 remote, явный выбор, смена identity во время parent reads, restart/reload, неизвестный ответ активации, потеря ответа/отмена и отсутствие секретных полей в DTO.

Расширен существующий HaDockerProbe. В настоящем образе HA проверяются четыре пары DSM metric/API key из исходных sensor descriptions без подключения к NAS. Через штатный API создаётся собственный `input_boolean` helper, выключается и включается обратно; HA сообщает `require_restart`. Затем успешный ответ enable намеренно теряется: после reconnect read-only повтор не отправляет update. После restart проверяются identity, переименованный ID, включённая запись и живое `on`/`off`. Это тест штатного helper, не подмена DSM/плеера.

```sh
dotnet restore --locked-mode
dotnet build --no-restore --configuration Release
dotnet test --no-build --no-restore --configuration Release
docker pull ghcr.io/home-assistant/home-assistant:2026.10.0
docker pull lscr.io/linuxserver/jellyfin:12.2ubu2604-ls53
dotnet run --project tools/HhHomelab.Wizard.HaDockerProbe --no-build --configuration Release -- profiles/hh-homelab/1.3.0/research-profile.json
```

Требуются SDK 10.0.401 и локальный Linux Docker Engine. Python на компьютере не нужен. Используются только собственные временные ресурсы с проверкой владения, loopback-публикацией и независимым cleanup. Секреты синтетические и не выводятся. Существующий CI job `ha-runtime` повторяет расширенный сценарий дважды; результат конкретного run публикуется в PR.

До аппаратного этапа остаются подключение DSM/TV, сопоставление реальных storage IDs с отсеками, подтверждение конкретных Cast/Jellyfin клиентов, активация DSM sensors после reload и достоверность их показаний. Результаты helper и фикстур этого не доказывают. NAS не изменялся.

Первичные источники выбранной версии: [entity registry API](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/config/entity_registry.py), [registry metadata](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/helpers/entity_registry.py), [DSM unique ID и устройства](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/synology_dsm/entity.py), [DSM sensor descriptions](https://github.com/home-assistant/core/blob/2026.10.0/homeassistant/components/synology_dsm/sensor.py).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
