# Локальный журнал: fixtures и принудительное завершение процессов

Дата: 2026-10-18. Основа: PR #109, commit `844e4d3b6410b35e60a4fce8b6f9bf55f0235d04`. Инкремент #51 использует те же Application ports/scenario. Точный commit и CI результата фиксируются в PR и Issue. Контракт и ограничения — в [ADR 0011](../adr/0011-local-file-journal.md).

## Проверяемые сценарии

- Intent и confirm сохраняются при повторном открытии; pending/confirmed запись не допускает Apply.
- Идентичность установки/сессии/профиля/ресурса/endpoint/спецификации проверяется; новый operation ID не скрывает pending intent.
- Каждый метод требует lease; второй экземпляр и другой ресурс той же установки получают конфликт; освобождённая lease доступна повторно.
- Отсутствующая, повреждённая, пустая, слишком большая и несовместимая запись блокирует продолжение. Неизвестные JSON поля, некорректные evidence и дубли с пересчитанным hash не принимаются.
- Отмена до публикации не создаёт intent/confirm и не удаляет прежнее намерение. Замена snapshot во время lease блокирует commit. Orphan temp не становится журналом.
- Symlink каталога/файла проверяется Linux unit-тестом; Windows тест пропускает, поскольку право создания symlink не предполагается. Windows reparse-point guard реализован, отдельного аппаратного результата для него нет.

`JournalProbe` создаёт собственный временный каталог с синтетической identity, без HTTP/SSH/credentials. Дочерний процесс публикует intent и остаётся с lease; независимый процесс подтверждает конфликт блокировки. Родитель принудительно завершает только созданный им процесс. Ещё один процесс открывает журнал и получает Unknown при Missing с нулём Apply. Затем новый процесс наблюдает Satisfied, выполняет Verify/confirm через тот же Application scenario и вновь принудительно завершается. Следующий процесс получает AlreadySatisfied с нулём Apply. Каталог удаляется после проверки; путь проверяется как непосредственный дочерний каталог системного temp.

## Повторение

Нужен SDK из `global.json`. Из корня репозитория:

```text
dotnet restore HhHomelab.Wizard.slnx --locked-mode
dotnet build HhHomelab.Wizard.slnx --no-restore -c Release
dotnet test HhHomelab.Wizard.slnx --no-build -c Release
dotnet run --project tools/HhHomelab.Wizard.JournalProbe --no-build -c Release
dotnet run --project tools/HhHomelab.Wizard.ProfileCheck --no-build -c Release -- profiles/hh-homelab/1.3.0/research-profile.json
```

Проба включена в существующие Windows/Ubuntu core CI jobs. Локально проверяется Windows; Linux подтверждается только фактически успешным CI этого PR. Полные результаты находятся в отчётах тестов/CI, не выводятся из прохождения build.

## Границы результата

Это локальное process-restart evidence на файловом журнале и искусственном внешнем состоянии. Не проверены питание/диск/kernel crash, удалённый lock/ownership, живой qBittorrent в этом сценарии, SSH, DSM ACL, NAS, защищённое хранилище и граф установки. Probe не разворачивает стек и не переносит старые in-memory Media/HA intent. Перенос пользовательского GUI acceptance на новый commit не предполагается.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
