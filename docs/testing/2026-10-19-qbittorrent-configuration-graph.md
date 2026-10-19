# Compose и .env qBittorrent из плана и исполняемый граф

Дата: 2026-10-19. База: `main` / `59abc3a` (merge PR #122). Задачи: [#101](https://github.com/hhypest/hh-homelab-wizard/issues/101), [#50](https://github.com/hhypest/hh-homelab-wizard/issues/50), [#52](https://github.com/hhypest/hh-homelab-wizard/issues/52), [#53](https://github.com/hhypest/hh-homelab-wizard/issues/53). SDK 10.0.401, build-time profile hh-homelab 1.3.0; зависимости не изменены. Решение: [ADR 0023](../adr/0023-qbittorrent-configuration-graph.md).

## Сверка с источником

Файл `media/compose.yaml` hh-homelab на commit `b05047b081f3543520582315002e359ef1d93e59` прочитан как текст. Его SHA-256 `a07918982051b11f4064b91026325bdb12e372ccc02e7991c0075a089c7ffde8` совпадает с `sourceComposeSha256` шаблона. Блок `qbittorrent` и секцию `networks` сравнили с отрисованным Compose для порта 9080 и сети `media` через `diff`. Отличаются только:

- `WEBUI_PORT` и публикация WebUI: выбранный порт с обеих сторон;
- текст подсказок `.env.example` → `.env`;
- удалённый комментарий о Dependabot.

Образ, Docker mods, монтирования, порты torrent TCP/UDP, сеть, `restart` и `logging` совпадают.

## Локальные fixtures

План собирается настоящим `QbittorrentPlanCompiler` на синтетических снимках. Граф выполняет настоящий `ConfigurationPlanScenario` с `FileOperationJournal`. NAS — один синтетический корень для путей, маркера и файлов, правила которого совпадают со скриптами.

- Compose: закреплённые образ и mods, `WEBUI_PORT` и публикация с выбранным портом 19080, три монтирования, имя сети из `.env`, хеш источника в шапке; нет `9080` и адресов другого NAS.
- `.env`: ровно девять значений запроса, `MEDIA_ROOT` в одинарных кавычках. Повторная отрисовка даёт те же байты.
- Корень данных с `:` или `'` — `render.data-root.unsupported`.
- Граф: три шага, маркер первым, оба файла зависят от него, три разных ресурса. Повторное построение даёт тот же отпечаток; другой WebUI port — другой.
- План с Blocking (bind на loopback) — `graph.plan.blocking`. План старше двух минут — `graph.plan.stale`. Pin другого endpoint — `graph.pin.mismatch`.
- Выполнение: Applied, одна запись маркера и по одной записи каждого файла; содержимое совпадает с отрисованным, `.env` 600. Повтор новым процессом — AlreadySatisfied без записей.
- Потеря ответа при записи `.env` — Unknown; новый процесс подтверждает наблюдением, записей по-прежнему две.
- Ручное изменение Compose после установки: resume — Conflict, изменённый файл не перезаписан.
- Чужой маркер: Conflict на первом шаге, ни одного файла.
- Маршрутизатор владения отдаёт проверку владельцу ресурса, чужой ресурс — Foreign; операция без порта владения и дубль ресурса отвергаются.
- Стабильные ID: GUID версии 8, зависят от установки и роли.

Локально Linux / SDK 10.0.401: Release build без предупреждений и ошибок, все unit/fixture и Desktop тесты.

## Не проверено

`docker compose config` и запуск контейнера; создание каталогов монтирования и сети; работа графа на SSH-стенде (стендовый preflight без DSM даёт Blocking и намеренно не превращается в граф); DS725+/DSM.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
