# ADR 0026: read-only проверка имени Docker-сети

- Дата: 2026-10-19.
- Статус: частичный [#50](https://github.com/hhypest/hh-homelab-wizard/issues/50)/[#48](https://github.com/hhypest/hh-homelab-wizard/issues/48) для вертикали [#101](https://github.com/hhypest/hh-homelab-wizard/issues/101) (A6), родители #20/#21; fixtures/одноразовый стенд по [ADR 0008](0008-audit-development-sequence.md).
- Основа: [SSH](0012-ssh-connection-probe.md), [план qBittorrent](0021-qbittorrent-installation-plan.md), [граф](0023-qbittorrent-configuration-graph.md).

## Решение

`INasDockerNetworkProbe` — обязательный третий порт `QbittorrentPlanCompiler`. Каждая компиляция заново читает host preflight, пути, затем выбранную Docker-сеть. Необязательного fallback, превращающего отсутствие probe в успех, нет. Это чтение; новых network create/rm/connect, pull, Compose up или изменений NAS не добавлено.

Фиксированный `nas-network-read.sh` принимает только stdin-запрос `nas-network-request:v1`: `network=<выбранное имя>`, `end`. Имя — прежний allow-list запроса: 1–63 ASCII alnum/underscore/hyphen, первый символ alnum. Одна проверка `DockerNetworkSnapshot.IsValidName` используется запросом, adapter/protocol; shell дополнительно проверяет то же ограничение. Имя не встраивается в текст SSH-команды.

Docker вызывается только на `unix:///var/run/docker.sock`; ambient endpoint/context/API/header overrides удаляются. `network ls --no-trunc --filter name=...` — read-only команда. По [официальному контракту](https://docs.docker.com/reference/cli/docker/network/ls/#name) фильтр имени допускает подстроку, поэтому Go-template отдельно сравнивает `.Name` с выбранной строкой через `eq`. Строка не содержит кавычек/Go-template syntax благодаря allow-list. Точное совпадение выдаёт только полный network ID, похожие имена — фиксированный `skip`; чужие имена/labels/subnets/config не возвращаются. Семантика на закреплённом daemon проверяется сценарием `host` против `hos`.

Ответ `nas-network:v1` содержит строки `network=<id>`/`skip`, итоговый `status=available|unavailable`, `end`. CLI stdout передаётся прямо в ограниченный SSH reader (16 KiB), без накопления полного списка в shell и без pipeline, теряющего exit code. Parser разрешает максимум 16 записей наблюдения, проверяет полное framing/status, 64 hex local IDs либо 25 ASCII alnum Swarm IDs, дубли и согласованность. Частичные IDs + Unavailable, неизвестные поля/версии, лишние записи и ненулевой SSH command exit отвергают весь ответ. Отсутствие CLI, отказ socket/daemon без частичного вывода дают Unavailable, никогда не «сети нет».

`DockerNetworkFacts` копирует, нормализует и сортирует ID. Swarm ID — отдельный формат Docker, а не SHA-256 fingerprint: [upstream SwarmKit generator](https://github.com/moby/swarmkit/blob/master/identity/randomid.go) использует 25 символов base36. Поддержка чтения такого ID даёт тот же Blocking-конфликт, не совместимость Swarm/overlay и не право присвоения сети. Truncated, недопустимые длины/символы и дубли отклоняются; Swarm-ветка проверена fixtures, настоящий стенд остаётся local Engine без Swarm. `DockerNetworkSnapshot` неизменяем: выбранное имя, host key, principal/endpoint/profile, время и facts. Fingerprint исключает timestamp, включает имя/status/IDs и binding. Снимок должен относиться ровно к запросу и быть не раньше path observation, не в будущем и не старше двух минут. Старейшее preflight-доказательство сохраняет прежний общий предел свежести.

| Наблюдение выбранного имени | Проверка плана |
| --- | --- |
| Available, совпадений нет | `plan.network-name`: Passed только для отсутствия на момент чтения |
| Available, один или несколько ID | `plan.network-name.conflict`: Blocking |
| Unavailable | `plan.network-name.unavailable`: Blocking |
| SSH/protocol failure или несовместимый/старый snapshot | Свежего плана нет, типизированный отказ |

Любая существующая сеть блокирует **новую установку**, даже если имя кажется собственным. По имени, Docker defaults или маркеру каталога сеть не присваивается. Проверка labels/Compose project/точной network identity для resume собственной сети — будущая работа. `QbittorrentInstallationGraph.Create` уже отклоняет Blocking, поэтому отказы происходят до journal/marker/directory/file writes.

Перепроверка всегда повторяет все чтения. Появление, смена ID или потеря доступности выбранной сети меняют fingerprint; ошибка не возвращает прежний план как свежий. Порядок IDs и timestamps не дают ложный drift. `skip` не включает похожие сети в identity выбранной.

## Границы

Ни Passed, ни свежесть не являются lease/ownership или разрешением установки. Между наблюдением и записью среда может измениться; перед будущим созданием сети нужны отдельные Observe/intent/Apply/Verify, собственные labels, fresh baseline и защита чужих ресурсов. Не проверены driver/IPAM/subnet/DNS, namespace mapping, DSM compatibility, доступ контейнера и реальный NAS. Остальные Unverified/NotChecked сохраняются, `CanApply` не появляется. Полные #48/#50/#101/M2/M3 не закрываются.

Доказательства: [протокол](../testing/2026-10-19-docker-network-preflight.md).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
