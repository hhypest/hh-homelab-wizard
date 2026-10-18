# Первый инкремент read-only preflight

Дата: 2026-10-19. База: `dcca96e`, слияние PR #112 в `main`; журнал, SSH и предыдущий одноразовый стенд проверены как присутствующие в основной ветке. Задачи: [#48](https://github.com/hhypest/hh-homelab-wizard/issues/48), prerequisites [#101](https://github.com/hhypest/hh-homelab-wizard/issues/101). SDK 10.0.401, SSH.NET 2026.0.0, build-time profile hh-homelab 1.3.0; зависимости/версии устанавливаемых сервисов не изменены. Решение: [ADR 0013](../adr/0013-read-only-nas-preflight.md).

## Локальные fixtures

- Полный синтетический inventory, Missing/Unavailable, неизвестные/некорректные версии, повреждённый marker, лишние/переставленные/повторные поля, неправильный exit status и незавершённый ответ.
- Лимит 128 ID, отказ при 129, дубликатах и ID не того формата; невозможно превратить failed listing в empty. Коллекции копируются и доступны только для чтения.
- Привязка к host/port/key/SSH-пользователю/profile; конечное окно свежести всего наблюдения, clock skew, смена версии/ресурсов, отсутствие ложной смены environment fingerprint при естественном движении часов.
- Неверная платформа блокирует соответствующую проверку. Наблюдаемые версии остаются Unverified; model/paths/ACL/space/ports/ownership — NotChecked. Непустой inventory блокирует путь до проверки ownership; пустой не даёт общую готовность установки.
- Первый/изменённый ключ останавливает до auth/read, неверный endpoint pin — до создания wire. Ошибка чтения не возвращает частичные факты/секреты. Cancellation/deadline/Busy, новый connection/snapshot на каждом вызове.
- Прежние SSH policy/parser/TCP tests сохраняются после выделения общего host-key validator.

Номера версий DSM/Container Manager/Docker/Compose в fixtures — синтетические данные, не результаты доступа к NAS и не подтверждённая матрица совместимости.

## Расширенный OpenSSH-стенд

Используется [прежний изолированный стенд](2026-10-19-ssh-runtime.md): локальный Linux Docker, loopback, собственные labels/resources, без bind mounts и доступа к Docker socket хоста. К четырём публичным файлам build context добавлена пятая embedded-копия того же `nas-preflight.sh`, который вызывает production adapter. ForceCommand сравнивает полный текст и исполняет только собственную копию; клиентский текст не передаётся eval.

Только в disposable sshd отключены `PerSourcePenalties`: серия намеренных auth/trust failures не должна временно блокировать следующие проверки с того же loopback-источника. [OpenSSH](https://man.openbsd.org/sshd_config#PerSourcePenalties) по умолчанию накапливает такие штрафы; исходный расширенный прогон остановился до changed-key evidence с TransportFailure. Полный повтор проверяет изменение fixture policy; retry и ослабление настроек реального сервера в production adapter не добавлены.

Проба проверяет:

1. First-use/changed-key/wrong-password отказывают с typed результатом без снимка.
2. Пароль и encrypted private key собирают настоящий Linux/x86_64/time inventory. В fixture нет DSM, Container Manager и Docker CLI: они должны остаться Missing, listing — Unavailable, оценка — блокирующей/неполной. Стенд не притворяется DSM.
3. Два вызова дают новые снимки одного окружения с одинаковым environment fingerprint. Pin/profile/SSH principal проверяются через freshness contract.
4. Fault modes malformed/oversized, реальное ожидание команды с caller cancellation/Busy и отдельный deadline не дают частичного success. После сброса fault следующий сбор снова успешен.
5. Все прежние SSH/SFTP/rotation/kill/reconnect проверки и cleanup сохраняются. CI запускает полный инструмент дважды на свежих контейнерах.

Точный commit, definition hash, run URL и фактический результат фиксируются в новом PR/#48. Добавление кода job само по себе не объявляется успешным runtime. Положительные Docker/Compose/package branches скрипта пока представлены fixture-данными, а не исполнением настоящего Docker daemon через SSH; это отдельный следующий стендовый инкремент #48.

## Воспроизведение и ограничения

```text
dotnet restore HhHomelab.Wizard.slnx --locked-mode
dotnet build HhHomelab.Wizard.slnx --no-restore -c Release
dotnet test HhHomelab.Wizard.slnx --no-build -c Release
dotnet run --project tools/HhHomelab.Wizard.JournalProbe --no-build -c Release
dotnet run --project tools/HhHomelab.Wizard.ProfileCheck --no-build -c Release -- profiles/hh-homelab/1.3.0/research-profile.json
dotnet run --project tools/HhHomelab.Wizard.SshDockerProbe --no-build -c Release
```

Последний инструмент требует локального Linux Docker и предварительно загруженной точной базы из fixture Dockerfile. На рабочем Windows-компьютере Docker engine выключен, поэтому живой прогон выполняется в `ssh-runtime` Ubuntu 24.04 CI. Локальные unit/fixture/headless и CI runtime различаются.

NAS/DSM не подключались и не менялись; Desktop wiring, disk space, ports, model, ACL/paths, volume/database inventory, ownership/resume и полная hardware compatibility не проверялись. Полная клиентская GUI-приёмка владельца PR #110 сохраняется отдельно. #48 и M2 остаются открытыми.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
