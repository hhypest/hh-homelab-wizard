# Протокол: read-only имя Docker-сети

Дата: 19.10.2026. База: `245d7e2b9d2c153f5aa268089aa9423ce1dbf4e4` (`main`, merge PR #128). Частичный #50/#48 для #101 (A6), родители #20/#21. Решение: [ADR 0026](../adr/0026-docker-network-preflight.md). Точный head/CI/review фиксируются в PR.

## База и среда

Main CI [39291163508](https://github.com/hhypest/hh-homelab-wizard/actions/runs/39291163508) успешен; #128 включён в main с сохранением истории, открытых PR и незакрытых review threads #128 при начале нет. Это доказательство прежней базы.

Локально Ubuntu 24.04.3 x64, .NET SDK 10.0.401 / runtime 10.0.12. Версии и lock-файлы не менялись. Профиль hh-homelab v1.3.0 / source commit b05047b081f3543520582315002e359ef1d93e59 прежний. Docker CLI/daemon отсутствуют; NAS credentials не использовались.

```text
dotnet restore HhHomelab.Wizard.slnx --locked-mode -m:1 -p:BuildInParallel=false
dotnet build HhHomelab.Wizard.slnx --no-restore -c Release -m:1 -p:BuildInParallel=false
dotnet test HhHomelab.Wizard.slnx --no-build -c Release -m:1 -p:BuildInParallel=false
dotnet run --project tools/HhHomelab.Wizard.JournalProbe --no-build -c Release
dotnet run --project tools/HhHomelab.Wizard.ProfileCheck --no-build -c Release -- profiles/hh-homelab/1.3.0/research-profile.json
sh -n src/HhHomelab.Wizard.Infrastructure/nas-network-read.sh
sh -n tools/HhHomelab.Wizard.ProbeSupport/ssh-fixture/dispatch.sh
```

Для исходного `33f7a75` locked restore, Release build успешны: 0 warnings / 0 errors. **1251 unit/fixture + 18 Desktop tests**, без пропусков. JournalProbe restart/exclusive-lock и plan-recovery passed, итог `journal.probe.passed`. ProfileCheck: 9 services / 19 artifacts / 118 steps, research inventory only. Последовательный MSBuild — параметр локальной среды, workflow не менялся.

## Fixtures и локальный shell

| Сценарий | Результат |
| --- | --- |
| Недействительное имя / shell/regex/template syntax / длина >63 | Отказ до SSH connect; имя только в stdin |
| Успешный пустой список / Unavailable | Разные facts; только первый доказывает отсутствие в момент наблюдения |
| Похожие имена | Фиксированный skip не становится совпадением выбранного имени |
| Malformed ID/status/framing/version, duplicates, partial denial, overflow, неизвестное поле | Весь ответ отклонён, прежнего success нет |
| Input IDs/порядок/разный timestamp | Коллекция скопирована; нормализация/сортировка сохраняют fingerprint |
| Первый/изменённый host key | Network request не отправляется |
| Auth/transport/protocol failure, Busy/caller cancel/deadline | Безопасные типизированные исходы, без raw exception text |
| Другие name/key/principal/profile в snapshot | Conflict/IdentityMismatch, плана нет |
| Старое/будущее сетевое наблюдение | Conflict/ObservationChanged |
| Сеть появилась / Docker стал Unavailable | Повторные чтения, Unchanged=false, Blocking до создания графа и writes |
| Ошибка SSH при перепроверке | Старый snapshot не выдаётся как свежий |

Сам embedded shell выполнен локально с поддельным Docker CLI в приватном временном PATH: проверены точные argv локального socket/filter/template, удаление Docker overrides, empty/present/substring/denied/partial ответы и отказ имени `bad.*` до вызова CLI. Это проверка shell/протокола, а не настоящий daemon. Временные файлы удалены.

## Настоящий SSH/Docker в CI

`DockerNetworkScenarios` читает production probe на прежнем одноразовом nested Engine 29.8.2 / OpenSSH стенде: встроенная сеть `host`, password/key с одинаковым fingerprint, гарантированно отсутствующее имя, `hos` не совпадает с `host`, Docker overrides не меняют endpoint, отказ socket не выдаётся за отсутствие, восстановление возвращает прежний fingerprint. Полный logical resource snapshot до/после одинаков: сети/контейнеры/образы/тома не меняются. Диагностический итог: `ssh-docker.network-name-read-only: passed`. Режим повторяется дважды на свежих контейнерах. Прежний SSH-only compiler получает Unavailable при отсутствии Docker, сохраняя Blocking, а PUID/PGID-проверки продолжают работать.

Fixture embedding/COPY/forced-command whitelist включают production `nas-network-read.sh`; definition hash вычисляется автоматически. Наличие сценария не считается успехом: CI точного head фиксируется отдельно в PR. Native Win32/X11/Wayland и прежние runtime jobs оцениваются CI отдельно от обычной клиентской и DSM-приёмки.

## Исправление по review

Codex Review `33f7a75` выявил неверное ограничение ID только 64 hex: Docker network ls включает Swarm/overlay с 25-символьными base36 ID. Формат подтверждён [официальным CLI контрактом](https://docs.docker.com/reference/cli/docker/network/ls/), [upstream примером](https://github.com/moby/moby/issues/34493) и [SwarmKit generator](https://github.com/moby/swarmkit/blob/master/identity/randomid.go). Теперь обе формы принимаются с прежними bounds/нормализацией; существующая Swarm-сеть даёт plan.network-name.conflict, а не response.invalid. Новые регрессии проверяют parser → revalidation → Blocking, case-insensitive duplicate, truncated/неверные длины/символы, отказ графа до writes. Итоговый локальный повтор: Release build 0 warnings/errors, **1258 unit/fixture + 18 Desktop**, без пропусков; JournalProbe/ProfileCheck passed. Реальный Swarm не запускался.

Perch исходного `33f7a75` сообщил вероятностное wrong order в неизменённом RotateSshHostKeyAsync. Проверен порядок замены ключей до HUP и отдельный bounded gate VerifyKeyRotationAsync, требующий HostKeyChanged с ожидаемым новым ключом и затем Ready с новым pin. CI job [118200820335](https://github.com/hhypest/hh-homelab-wizard/actions/runs/39293750599/job/118200820335) подтвердил ротацию дважды. В thread дано объяснение, предупреждение закрыто как неподтверждённое, unrelated код не менялся; локального perch check нет. Исходный CI [39293750599](https://github.com/hhypest/hh-homelab-wizard/actions/runs/39293750599): 12/12 jobs success, network-name-read-only passed дважды, fixture definition ef452a4144d8fb1b1e433e2eabdb49092b52730b3d51180ee1178e3b0d760615. Это результаты исходного commit; CI итогового head фиксируется в PR.

## SonarQube и ограничения

CLI `sonar` отсутствует в PATH, callable SonarQube MCP-инструментов нет; Docker/Podman/Nerdctl также отсутствуют, project key в репозитории не задан. Quality Gate/файловый анализ SonarQube **не выполнены**. Прочитаны sonar-quality-gate/sonar-analyze/sonar-integrate skills; установка CLI требует явного подтверждения владельца. Результаты CI/других review не подменяют SonarQube.

Нет создания/удаления/присвоения сети, Compose up, проверки driver/IPAM/DNS, network ownership для resume, установки на NAS или полного стека. Отсутствие сети в момент чтения не защищает от гонки после него. Полные #48/#50/#101/M2/M3 остаются открытыми.

Markdown-relative links и `git diff --check` проверяются перед публикацией.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
