# Протокол: конфигурационный граф и process-restart recovery

Дата: 19.10.2026. База: `c4d6439a8c69bc241d9f8ec31cfadc5847542860` (`main`, PR #119). Изменение #52, prerequisites #51, родитель #21, часть вертикали #101 по ADR 0008. Решение: [ADR 0020](../adr/0020-configuration-plan-recovery.md). Точный commit изменения фиксируется в PR.

## Среда

- Ubuntu 24.04 x64, .NET SDK 10.0.401, runtime 10.0.12; версии и lock-файлы не менялись.
- Профиль: `profiles/hh-homelab/1.3.0/research-profile.json`; опубликованный источник hh-homelab v1.3.0 / `b05047b081f3543520582315002e359ef1d93e59`.
- Синтетические операции и ownership в fixtures; настоящий файловый журнал в приватных временных каталогах; отдельные дочерние процессы в JournalProbe. Секретов и реальных адресов NAS нет.

## Проверки

```text
dotnet restore HhHomelab.Wizard.slnx --locked-mode -m:1 -p:BuildInParallel=false
dotnet build HhHomelab.Wizard.slnx --no-restore -c Release -m:1 -p:BuildInParallel=false
dotnet test HhHomelab.Wizard.slnx --no-build -c Release -m:1 -p:BuildInParallel=false
dotnet run --project tools/HhHomelab.Wizard.JournalProbe --no-build -c Release
dotnet run --project tools/HhHomelab.Wizard.ProfileCheck --no-build -c Release -- profiles/hh-homelab/1.3.0/research-profile.json
```

MSBuild здесь запускался последовательно: параллельная оценка solution в ограниченной среде завершалась без сообщения об ошибке. Это параметр локального запуска, CI workflow не менялся.

Locked restore и Release build прошли: 0 warnings / 0 errors. Полный набор: 1035 unit/fixture (включая 28 новых plan tests) и 18 Desktop tests пройдены, без пропусков.

| Сценарий | Наблюдаемый результат |
| --- | --- |
| Потребитель указан раньше зависимости | Выполняется сначала зависимость; при resume каждый шаг снова Observe/Verify, без второго Apply |
| Потеря ответа после Apply | Unknown, потребитель даже не наблюдается; новый журнал открывается с тем же планом |
| Pending и результат уже присутствует | Observe/Verify подтверждают прежний intent, затем разрешается зависимый шаг |
| Pending и результат отсутствует | Unknown, Apply не повторяется; зависимые шаги не запускаются |
| Подтверждённая зависимость исчезла / утрачено владение | Conflict; запись из журнала не разрешает продолжение |
| Изменены dependency, budget, specification, endpoint, добавлен/убран шаг | Conflict/IdentityMismatch до Observe и до intent, в том числе при ещё пустой истории |
| Адаптер отсутствует или не совпадает | Отказ до привязки плана и первой операции |
| Одиночная операция вне закреплённого плана | Journal lease не выдан, Observe/Apply не вызываются |
| Привязка плана к прежней одиночной истории | Отказ без присвоения истории/сброса |
| Deadline до intent / во время Apply | Transport/DeadlineExceeded / Unknown соответственно; второй шаг не запущен |
| Caller cancellation между шагами | Cancelled, более ранний проверенный успех остаётся в отчёте |
| Два запуска одного executor / два журнала | OperationInProgress до конкурирующих шагов |
| Cycles, missing dependencies, duplicates, чужая identity | Граф отклонён при построении |
| Изменение входных массивов | Сохранён исходный граф и fingerprint |
| Повреждение плана при пересчитанном внешнем checksum | Семантическая проверка отвергает файл |
| Journal schema 1 | Отклонён; исходные байты не изменены |

JournalProbe дополнительно создаёт graph journal, запускает дочерний процесс через **настоящий ConfigurationPlanScenario** и принудительно завершает его внутри первого Apply после durable intent. Второй процесс с прежним планом и Missing получает `plan:Unknown:0:0`; следующий процесс с Satisfied подтверждает первый шаг без его Apply и выполняет второй (`plan:Applied:0:1`). Изменение dependencies в новом процессе — `plan:Conflict:0:0`. Старая process-restart/exclusive-lock проба также сохранена. Вывод: `journal.probe.plan-recovery.passed`, затем `journal.probe.passed`.

ProfileCheck прошёл; это проверка исследовательского каталога, а не установка. Markdown-ссылки и `git diff --check` проверяются перед публикацией.

## Ограничения

- Локально не запускались Docker/SSH runtime, native GUI Windows/X11/Wayland и реальная DSM: Docker daemon отсутствует. Матрица GitHub Actions проверяется отдельно в PR.
- Процессный synthetic resource не доказывает восстановление реального HTTP/SSH сервиса. Нет Compose delivery, concrete installation-plan compiler, интерактивных шагов или Desktop подключения графа.
- Бюджеты кооперативные, NAS-граф не является транзакцией, lease действует на операцию; непрерывной блокировки всего графа нет.
- Schema 1 не мигрируется. До продолжения старых исследовательских сессий потребуется отдельный проверенный перенос; сброс журнала для обхода недопустим.
- Нет directory fsync / доказательства power-loss durability, компенсации и аппаратной проверки. #52/#51/#101/M3 остаются открытыми. NAS не подключался и не изменялся.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
