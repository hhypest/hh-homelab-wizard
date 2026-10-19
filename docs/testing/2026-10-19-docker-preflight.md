# Docker/Compose preflight через настоящий SSH

Дата: 2026-10-19. База `0c3781c`, PR #113 слит в `main`; первый inventory, fixtures и negative SSH runtime присутствуют. Работа: #48, prerequisite #101, native parent #20 / Milestone M2. SDK 10.0.401, SSH.NET 2026.0.0, profile hh-homelab 1.3.0; production код/версии сервисов не изменены. [ADR 0014](../adr/0014-docker-preflight-runtime.md) описывает privileged CI boundary и пины.

## Проверяемый результат

Новый `--docker-preflight` запускается только в одноразовой Linux GitHub-hosted VM. На рабочем Windows-компьютере проверяется отказ policy guard до обращения к Docker. Обычный SSH fixture сохраняется без privileged mode и без Docker; матрица `ssh-runtime` отдельно запускает `ssh` и `docker`, по два свежих прогона.

Docker target использует собственный daemon/Unix socket/data root. Внешний Docker daemon ID должен отличаться; host socket/bind mounts/shared host PID/network/IPC/cgroup не используются. Требуемый privileged container изолирован от пользовательской инфраструктуры VM runner, а не от ядра самого runner. Ни одна из этих проверок не подключается к NAS.

Сценарии:

1. Чтение production collector через SSH по password и encrypted private key: фактические Engine 29.8.2 / Compose 5.1.4, Available empty inventory, свежие snapshots одного environment. DSM/Container Manager остаются Missing; версии не превращаются в подтверждённую совместимость.
2. Test control plane импортирует собственный tiny USTAR rootfs без pull. Создаются два stopped containers по очереди, без запуска кода. Collector видит реальные полные IDs, environment fingerprint меняется, старый snapshot остаётся неизменным; assessment требует ownership.
3. До/после collector сравнивается hash логических ресурсов: IDs/state/image контейнеров, images, volumes и networks. Сам preflight не должен создавать/изменять их. Это не проверка отсутствия внутренних служебных записей Docker на диск.
4. В дочернее SSH-окружение инъецируются заведомо неверные Docker host/context/TLS/API/header overrides. Collector по-прежнему читает собственный local Unix socket, snapshot не меняется. Overrides не передаются в настройки runner/NAS.
5. На socket временно снимается group access в owned fixture: Docker/listing становятся Unavailable, ID не подменяются пустым Available; Compose version остаётся доступна без daemon. После восстановления прав нормальный snapshot возвращается.
6. Отключается executable permission собственного Compose plugin: Compose Unavailable, Docker/listing остаются Available. Восстановление возвращает прежний snapshot.
7. Собственный daemon останавливается, затем запускается на прежних data root. Между ними SSH доступен, Docker/listing Unavailable; после старта прежние IDs и environment fingerprint сохраняются. Ресурсы остаются неизменными относительно снимка после тестового setup.
8. Каждый режим заканчивается ownership cleanup; ошибка cleanup даёт ненулевой exit. Никакие volumes/media/config хоста не удаляются.

## Воспроизведение и доказательства

Локально доступны locked restore/build/tests, JournalProbe и ProfileCheck, как в [первом протоколе](2026-10-19-nas-preflight.md). Новый режим на локальной Windows/Linux системе намеренно завершается `bench.ssh.docker.github-hosted-required`; не нужно запускать Docker Desktop или менять компьютер для этой проверки.

CI `ssh-runtime (docker)` собирает digest-pinned base/target и дважды вызывает:

```text
dotnet run --project tools/HhHomelab.Wizard.SshDockerProbe --no-build -c Release -- --docker-preflight
```

Успех требует всех `ssh-docker.*: passed`, `bench.cleanup: passed` и итогового `ssh-runtime.all: passed`. Точные commit/definition hash/run URL и результаты публикуются в новом PR/#48; само наличие сценариев не считается успешным runtime. Сырые auth/daemon/process streams не публикуются; ошибки представлены безопасными кодами.

Не проверены DSM/Container Manager на DS725+, модель NAS, совместимость их версий, реальные ACL/paths, disk space, ports, чужие volumes/databases, ownership/resume, установка Media/HA и Desktop wiring. Полные #48/M2/#101 остаются открытыми. Приёмка владельца PR #110 на Windows 11 / Ubuntu GNOME X11/Wayland сохраняется отдельно.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
