# ADR 0013: первый read-only preflight NAS

- Дата: 2026-10-19.
- Статус: частичный инкремент [#48](https://github.com/hhypest/hh-homelab-wizard/issues/48), design/fixtures и одноразовый SSH-стенд по [ADR 0008](0008-audit-development-sequence.md).
- Основа: [SSH-порт](0012-ssh-connection-probe.md), [неизменяемые операции](0010-operation-boundaries.md), исследовательский профиль hh-homelab 1.3.0.

## Контракт наблюдения

Domain содержит immutable facts/snapshot и чистую оценку prerequisites, Application — `INasPreflightProbe`, Infrastructure — SSH-реализацию. Desktop ещё не подключён. Новый вызов открывает свежее соединение, повторно проверяет явно переданный host/port-bound pin и собирает данные заново. Проверка ключа общая с connection probe; протокол не обходит пароль/key policy, конечный budget, cancellation, Busy и ограничение stdout/stderr.

Снимок привязан к endpoint, публичному ключу, fingerprint SSH-пользователя и профилю текущей сборки. Чужая учётная запись не может использовать прежнее evidence о доступе. Локальное начало/окончание измерения отделено от времени сервера. `IsCurrentFor` проверяет всю длительность наблюдения, отсутствие времени из будущего и возраст не более явно заданного окна (верхняя граница API — пять минут). Это вспомогательная проверка, не разрешение пропустить свежий preflight перед Apply.

Environment fingerprint включает identity/profile, версии и отсортированные container IDs; естественное движение часов не считается сменой окружения. Изменение версии/наблюдаемых контейнеров меняет fingerprint. Он не доказывает неизменность всех параметров NAS: конфигурации контейнеров, содержимое файлов и ACL пока не наблюдаются.

## Ограниченное чтение

В сборку встроен один версионированный POSIX shell script. Его полный текст — фиксированная SSH-команда; UI-ввод, пути и параметры в неё не подставляются. Скрипт читает `uname`, epoch time, кандидатные DSM VERSION/Container Manager INFO, Docker server version, Compose version и до 129 container IDs. Парсер допускает максимум 128 ID; дополнительная запись, дубликаты, неизвестные поля, неправильный порядок, незавершённый/неуспешный ответ отвергают весь снимок. Сохраняются только типизированные поля; имена, env, mounts, labels и содержимое конфигураций контейнеров не выгружаются.

VERSION/INFO читаются как данные через awk, не исполняются через source/eval. DSM file layout (`/etc.defaults/VERSION`, `/var/packages/ContainerManager/INFO`) пока является кандидатным контрактом: аппаратного подтверждения DS725+ нет. Missing/Unavailable обозначают результат доступа текущего SSH-пользователя; отсутствие файла в недоступной иерархии не является доказательством удаления пакета. Ошибка или неизвестная версия не скрывается автоматической установкой/сменой прав.

Docker вызывается с явным `--host unix:///var/run/docker.sock`; endpoint/context/TLS/API/header overrides окружения сбрасываются только внутри дочерней команды. Сеть, Docker context пользователя и настройки DSM не меняются. Sudo не выполняется. Отсутствие CLI отличается от неуспешного запроса; неуспешный container listing нельзя представить пустым. Compose plugin failure остаётся Unavailable, без запуска альтернативного installer.

Основание команд: [Docker version](https://docs.docker.com/reference/cli/docker/version/), [container ls](https://docs.docker.com/reference/cli/docker/container/ls/), [local daemon socket](https://docs.docker.com/reference/cli/dockerd/). Формат версии/identity пакета: [Synology INFO](https://help.synology.com/developer-guide/synology_package/INFO_necessary_fields.html), [package FHS](https://help.synology.com/developer-guide/integrate_dsm/fhs.html). Документация не заменяет аппаратный smoke.

## Оценка и границы

Состояние результата `Ready` означает только завершённый сбор фактов. Чистая оценка имеет Passed/Blocking/Unverified/NotChecked. Linux/x86_64 и окно часов проверяются явно; допуск часов — две минуты относительно локального интервала, это не проверка NTP. Наблюдаемая версия остаётся Unverified до подтверждённой матрицы совместимости; совпадение с сообщённой владельцем DSM 7.4.1-90080 не считается испытанием.

Непустой inventory блокирует установочный путь до проверки ownership. Он не объявляется чужим или собственным по одному ID. Пустой список не доказывает отсутствие старых volumes/баз/Compose-проектов. NAS model, пути/ACL, disk space, порты и ownership явно NotChecked; все обязательные проверки не могут стать Passed от этого инкремента.

Это read-only probe, не установочная операция: mutation intent не создаётся, конфигурация NAS не изменяется. Remote lock/recovery остаются #51–#53, пути/план — #49/#50, полный preflight/аппаратная проверка — #48/#39. Полные критерии #46/#48/#101 и M2 не закрываются.

Модель NAS, слушающие порты и место томов добавлены протоколом v2 в [ADR 0015](0015-preflight-host-resources.md); описанное выше NotChecked для них относится к v1.

Доказательства и воспроизведение: [протокол](../testing/2026-10-19-nas-preflight.md).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
