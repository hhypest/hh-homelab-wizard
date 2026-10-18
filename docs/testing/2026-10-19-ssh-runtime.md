# Одноразовый OpenSSH/SFTP-стенд

Дата: 2026-10-19. Задача: [#46](https://github.com/hhypest/hh-homelab-wizard/issues/46), часть prerequisites [#101](https://github.com/hhypest/hh-homelab-wizard/issues/101). Контракт: [ADR 0012](../adr/0012-ssh-connection-probe.md), SSH.NET 2026.0.0, SDK 10.0.401. Ранее выполненные [fixtures/TCP](2026-10-18-ssh-probe.md) остаются отдельным историческим доказательством.

## Интеграционная база

На начало работы `origin/main` — `7dacee4c00c02448ad5166a0c2997c6e54631c97`, merge #109. PR #110 слит в `feat/operation-contracts`, #111 — в `feat/file-operation-journal`; статус Merged не означал попадание их изменений в `main`. В новой ветке к `main` merge `547511eed9e906dd38c72a7731719f917f7b1ca8` сохраняет исходную историю. До нового стенда `git diff feat/ssh-connection-probe HEAD` пуст: дерево точно соответствует ранее проверенному `f7626e9d0cc1579a703ad59887df01804d1a03d3`. Переписывания истории нет.

## Изолированная среда

- `SshDockerProbe` использует существующий OwnedDockerBench: только локальный unix/npipe Docker context, Linux daemon, session labels, собственная сеть, случайное имя контейнера. Свободный порт выбирается ОС на `127.0.0.1` и явно закрепляется при создании контейнера; restart обязан сохранить endpoint, иначе проба останавливается. Конфликт в промежутке между выбором и bind завершает запуск с ошибкой, без молчаливого выбора другого порта. Bind mounts и доступ к NAS отсутствуют; cleanup проверяет ownership и имеет независимые дедлайны.
- Контекст сборки — только четыре встроенных файла `ssh-fixture`: Dockerfile, sshd_config, entrypoint.sh, dispatch.sh, переданные USTAR через stdin. Их составной SHA-256 публикуется как `ssh-fixture.definition`, используется в tag/label; после build проверяется label и используется immutable image ID. При ошибке этой публичной стадии допускается ограниченный вывод build diagnostics без управляющих символов; runtime/auth streams не публикуются. Кэш публичного образа остаётся после cleanup, credentials в нём отсутствуют.
- База: `alpine:3.24.2@sha256:d56c381f961d307a21b3ca004cf1e3910f106644aefb1f43e654c8a56c4fd395` (linux/amd64). Прямые пакеты: `openssh-server=10.3_p1-r1`, `openssh-sftp-server=10.3_p1-r1`, `sudo=1.9.17_p2-r2`; версии проверены по официальному APKINDEX Alpine v3.24. Фактические версии выводятся как `ssh-fixture.packages`. Транзитивные apk-зависимости не имеют полного snapshot; побитовая повторяемость сборки не заявляется. Исчезновение закреплённого пакета останавливает сборку.
- Пароль и RSA 2048 key с passphrase генерируются в памяти каждого запуска. Пароль и public authorized key передаются через stdin Docker exec, private key — только в credentials SSH.NET. Ни ключ, ни пароль не передаются аргументами процесса/переменными окружения/слоями образа. В контейнере остаются только password hash и публичный authorized key до его удаления. Сессионные строки .NET не обещают гарантированного стирания из памяти.
- Сервер использует новый Ed25519 host key в tmpfs; при каждом запуске явно очищает прежние временные ключи и fault state. Root-login и forwarding отключены. ForceCommand распознаёт только фиксированные identity/sudo-list/SFTP операции и известные fault modes. Клиентский текст не исполняется через eval. Sudo policy разрешает только `/bin/true`; клиент выполняет только `sudo -n -l`, успешный listing не доказывает права на установку.
- Публичный ключ для ожидаемого pin читается через проверенный Docker exec независимо от SSH. При HUP он заменяется; после kill/start tmpfs создаётся заново. Приватный host key не выгружается.

## Проверяемые сценарии

1. Первый контакт без pin возвращает HostKeyApprovalRequired даже с неверным паролем; evidence совпадает с независимо прочитанным ключом.
2. Верный pin + неверный пароль/чужой key дают AuthenticationRequired. Пароль и разрешённый encrypted private key дают Ready, UID 1000, sudo listing и настоящий SFTP stat.
3. Повреждённый marker и ответ больше лимита дают InvalidResponse. Sudo-list denial сохраняет Ready с capability false. Отказ SFTP не даёт Ready/capabilities; допустим SftpUnavailable либо TransportFailure при закрытии subsystem, фактический тип записывается.
4. После наблюдаемого входа в задержанную команду второй вызов даёт Busy; caller cancellation даёт Cancelled, отдельный budget — TimedOut. Оба результата должны вернуться за ограниченное время, нормальная следующая проба снова успешна.
5. При ротации host key старый pin даёт HostKeyChanged с новым evidence; соединение успешно только при явно переданном новом pin. Короткое ожидание HUP использует исключительно read-only пробы.
6. Kill собственного контейнера во время реальной SSH-команды даёт TransportFailure. После start новый ключ отвергается старым pin; явно переданный новый pin снова даёт Ready.
7. Cleanup удаляет только ресурсы текущей сессии; неуспешный cleanup делает итог неуспешным. CI повторяет весь инструмент дважды на свежих контейнерах, с новыми аккаунтными данными/ключами.

## Воспроизведение и доказательства

Нужен локальный Linux Docker daemon (linux/amd64). Сначала отдельно загрузить точную базу из Dockerfile; инструмент не выполняет скрытый pull. Сборка публичного fixture устанавливает закреплённые пакеты из подписанного Alpine repository и ограничена дедлайном. Затем:

```text
dotnet restore HhHomelab.Wizard.slnx --locked-mode
dotnet build HhHomelab.Wizard.slnx --no-restore -c Release
dotnet test HhHomelab.Wizard.slnx --no-build -c Release
dotnet run --project tools/HhHomelab.Wizard.SshDockerProbe --no-build -c Release
```

CI job `ssh-runtime` на Ubuntu 24.04 выполняет pull exact base и два запуска. Успех требует всех `ssh-runtime.*: passed` и `bench.cleanup: passed`; изоляция и статусы являются assertions, а не ручным health check.

Первый успешный живой прогон: [run 39176573212](https://github.com/hhypest/hh-homelab-wizard/actions/runs/39176573212/job/117820367610), commit `a51f6c60195b24e35ca8945bb0acb1130724885c`, PR #112. Оба свежих запуска выполнили все перечисленные сценарии и cleanup; SFTP denial вернул SftpUnavailable. Наблюдаемые пакеты: openssh-server-10.3_p1-r1, openssh-sftp-server-10.3_p1-r1, sudo-1.9.17_p2-r2. Fixture definition `75ceac708d83e5a05afcd05fe3e774cacdb71f3c9ba8d0110d7b2d7961eb9356`. Последующий commit разделяет сборку/запуск/вывод evidence на короткие методы и уточняет комментарий entrypoint; его точный head и повтор CI фиксируются в PR/#46, без переноса результата на непроверенную сборку.

При отладке настоящий Docker выявил два дефекта самого стенда: leading PAX header распознавался как текст Dockerfile, а автоматически опубликованный порт менялся после restart. Исправлены USTAR и явное закрепление выбранного порта; полный runtime-сценарий является регрессией для обоих случаев. Производственный SSH-порт в этом инкременте не изменён.

Локально Windows: Docker CLI доступен, Linux daemon не запущен, поэтому живой Docker-прогон здесь не заявляется. Locked restore/build, unit/fixture/headless, JournalProbe и ProfileCheck выполняются отдельно; финальные результаты прикладываются к PR. Полная [GUI-приёмка владельца PR #110](../m1/2026-10-18-owner-check.md) сохранена, на новые изменения автоматически не переносится.

DSM/DS725+, аппаратные sudo/ACL, UI подключения, SFTP upload/atomic delivery и SSH→Docker→journal/resume вертикаль этим протоколом не проверяются. NAS не изменялся. #46/#51/#101 и этапы M2/M3 остаются открытыми.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
