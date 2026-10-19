# ADR 0014: настоящий Docker preflight через SSH в одноразовой CI VM

- Дата: 2026-10-19.
- Статус: стендовый инкремент #48/#101 по [ADR 0008](0008-audit-development-sequence.md), не аппаратная приёмка.
- Основа: [SSH-порт](0012-ssh-connection-probe.md), [снимок preflight](0013-read-only-nas-preflight.md).

## Решение и граница изоляции

Первый preflight подтверждал настоящую SSH-команду на Linux без Docker, положительные ветки оставались protocol fixtures. Новый режим `SshDockerProbe --docker-preflight` проверяет ту же production-команду через SSH на настоящем Docker Engine и Compose plugin. Production adapter, профиль устанавливаемого стека и UI не меняются.

В собственном контейнере запускаются OpenSSH и отдельный Docker daemon. Он имеет собственные data/exec root, vfs storage, только Unix socket, без bridge/iptables/IP forwarding/masquerading и registry pull. Host Docker socket и каталоги хоста не монтируются. Контрольный запрос сравнивает daemon ID с внешним Docker: они должны отличаться. Публикуется только случайно выбранный и затем закреплённый loopback SSH-порт.

Docker-in-Docker требует privileged container. **Граница безопасности здесь — одноразовая GitHub-hosted VM, а не privileged container относительно этой VM.** Внешние устройства/ядро runner не считаются изолированными контейнером. Режим доступен только при явном аргументе и policy guard Linux/GITHUB_ACTIONS/RUNNER_ENVIRONMENT=github-hosted/RUNNER_OS=Linux; обычный локальный запуск отвергается до обращения к Docker. Env guard предотвращает случайный запуск, не является защитой от человека, намеренно меняющего код/окружение.

CI использует `ubuntu-24.04`, без self-hosted runner. Внутрь не передаются GitHub token/секреты runner; credentials SSH создаются позже в памяти и передаются stdin. Проверяются session label, отсутствие bind/foreign volume mounts, собственная сеть и private PID/IPC/cgroup namespaces. Privileged mode не добавляется обычному SSH fixture или приложению. Применение к NAS не разрешается этим ADR.

Альтернатива с host socket/proxy отвергнута: она дала бы доступ к ресурсам внешнего daemon и ослабила прежнюю границу ownership. Fake Docker CLI/API не доказывает работу настоящего daemon. Rootless dind также требует privileged запуска в типовом Docker окружении; усложнение user namespace не заменяет отдельную VM.

Основание: [Docker-in-Docker](https://hub.docker.com/_/docker), [параметры отдельного daemon](https://docs.docker.com/reference/cli/dockerd/). Наличие этих механизмов не означает готовность DSM/ACL/ownership.

## Закреплённый fixture

Остаётся прежняя digest-pinned Alpine 3.24.2/amd64 и OpenSSH 10.3_p1-r1. Multistage Dockerfile имеет явные targets `ssh-only` (без Docker, default) и `ssh-docker`. Последний устанавливает `docker-engine=29.8.2-r1`, `docker-cli=29.8.2-r1`, `docker-cli-compose=5.1.4-r2` из подписанного Alpine v3.24 community repository. Пины сверены с официальным APKINDEX; Compose plugin path дополнительно сверяется при сборке.

Это версии тестового окружения, не новая матрица совместимости DSM и не обновление сервисов hh-homelab. Транзитивный APK snapshot не закреплён полностью, побитовая повторяемость сборки не заявляется. Контекст состоит из embedded публичных файлов; definition hash, target и реальные версии записываются в очищенный результат. Сборка не получает credentials.

## Сценарии и ограничения

Проверяются password/key, реальный empty inventory, 1/2 stopped containers, изменение environment fingerprint, неизменность прежнего snapshot, отказ доступа к socket и Compose plugin, daemon stop/start/recovery и игнорирование endpoint/context/TLS/API/header overrides. Наблюдаемые версии остаются Unverified. Наличие контейнеров требует отдельного ownership, Linux не выдаётся за DSM.

Тестовый control plane создаёт только собственный tiny rootfs image и два stopped containers во вложенном daemon. Они не запускаются; pull, установка медиасервисов и обращения к NAS отсутствуют. Production collector остаётся read-only. До/после сравниваются IDs/state/image контейнеров и IDs images/volumes/networks, чтобы выявить изменение логических ресурсов. Это не filesystem audit всех служебных записей daemon.

Cleanup удаляет собственный внешний контейнер и сеть с проверкой session identity; его nested state исчезает вместе с writable layer. Кэш публичного build image может остаться, credentials в нём нет. Провал cleanup делает probe неуспешным. CI отдельно запускает каждый режим дважды на свежих контейнерах.

Доказательства: [протокол](../testing/2026-10-19-docker-preflight.md). Остаток #48: DSM/Container Manager hardware, model, space, ports, volumes/databases/conflicts; #49/#50 — paths/plan, #51–#53 — ownership/lock/recovery. Этапы и полные Issues не закрываются.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
