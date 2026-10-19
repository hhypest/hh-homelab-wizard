# ADR 0023: Compose и .env qBittorrent из плана и исполняемый граф

- Дата: 2026-10-19.
- Статус: разработческий инкремент [#101](https://github.com/hhypest/hh-homelab-wizard/issues/101), части [#50](https://github.com/hhypest/hh-homelab-wizard/issues/50), [#52](https://github.com/hhypest/hh-homelab-wizard/issues/52) и [#53](https://github.com/hhypest/hh-homelab-wizard/issues/53); fixtures по [ADR 0008](0008-audit-development-sequence.md).
- Основа: [граф и recovery](0020-configuration-plan-recovery.md), [план qBittorrent](0021-qbittorrent-installation-plan.md), [создание файла](0022-configuration-file-delivery.md), [маркер](0018-ownership-marker-creation.md).

## Решение

### Содержимое файлов

`QbittorrentConfigurationRenderer` (Domain) детерминированно строит из `QbittorrentInstallationPlan` два файла:

- `media/compose.yaml` (644) — сервис `qbittorrent` и сеть `media`;
- `media/.env` (600) — значения запроса.

Сервис повторяет закреплённый источник: hh-homelab `b05047b081f3543520582315002e359ef1d93e59`, `media/compose.yaml`, SHA-256 `a07918982051…ffde8` — тот же хеш, что в шаблоне ADR 0021. Совпадают образ и Docker mods из `PinnedRuntimeProfile`, монтирования `./config/qbittorrent:/config`, `./watch:/watch` и `${MEDIA_ROOT}:/data`, публикации WebUI и torrent TCP/UDP на `BIND_ADDR`, сеть `media` с именем из `.env`, `restart` и ротация журналов.

Отличия от источника намеренные и проверены сравнением:
- внутренний `WEBUI_PORT` и публикация WebUI берут выбранный порт с обеих сторон (политика `host-and-container` ADR 0021): защищённый WebUI 5.2.4 отвергает несовпадающий Host port;
- подсказки об обязательных переменных ссылаются на `.env`, а не на `.env.example`;
- комментарий о Dependabot опущен, добавлена шапка с installation ID и происхождением.

`.env` содержит только выбранные значения: PUID, PGID, UMASK, TZ, BIND_ADDR, MEDIA_ROOT, MEDIA_NETWORK и два порта. Секретов, адресов другого NAS и личных значений из `.env.example` нет. `MEDIA_ROOT` записывается в одинарных кавычках, то есть буквально. Корень данных с `'` или `:` отвергается (`render.data-root.unsupported`): такой путь нельзя передать литералом в `.env` и в короткий синтаксис тома. Остальные значения уже ограничены валидацией запроса.

### Граф

`QbittorrentInstallationGraph.Create` строит из плана `ConfigurationOperationPlan` ADR 0020:

1. маркер владения (`OwnershipMarker`);
2. `media/compose.yaml` и `media/.env` (`ConfigurationFile`), каждый зависит от маркера.

Граф строится только из **свежего** плана (не старше двух минут, как его доказательства) **без Blocking**-проверок и только для pin, совпадающего с endpoint запроса; иначе — `graph.plan.blocking`, `graph.plan.stale` или `graph.pin.mismatch`. Unverified/NotChecked остаются на плане: граф не является разрешением установки, а план по-прежнему не имеет `CanApply`.

ID операций и ресурсов — именованные GUID версии 8 (RFC 9562): SHA-256 от installation ID и фиксированной роли (`operation:marker`, `resource:file:media/.env` и т. п.). Новый процесс строит тот же граф с тем же отпечатком, поэтому журнал schema 2 продолжает его, а не отвергает как другой план. Содержимое файлов входит в `SpecificationHash`, так что другой порт или путь даёт другой граф.

`OperationOwnershipRouter` (Application) — единый порт владения для графа: каждая свежая проверка уходит операции, которой принадлежит именно этот ресурс; чужой ресурс — Foreign, ничего не кешируется. `Dispose` графа обнуляет отрисованное содержимое.

## Границы

- Каталоги монтирования (`media/config/qbittorrent`, `media/watch`, данные), сеть, `docker compose up`, readiness и настройка qBittorrent — следующие шаги графа. Без них контейнер не запускается.
- `docker compose config` на отрисованных файлах не запускался: синтаксис подтвердит стендовый шаг с Compose.
- Публичного пути запуска графа и экрана #50/#55 нет; реальная DSM не затрагивается.
- Удалённая блокировка для графа (`RemoteLockedPlanJournal`) подключается вызывающим так же, как в ADR 0020.

Доказательства: [протокол](../testing/2026-10-19-qbittorrent-configuration-graph.md).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
