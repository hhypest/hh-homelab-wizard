# Протокол: числовая SSH identity и план PUID/PGID

Дата: 19.10.2026. База: `c76b7b951392df8ad6d7532e539b5047c31b132f` (`main`, merge PR #127). Частичный #49/#50 для #101 (A6), родители #20/#21. Решение: [ADR 0025](../adr/0025-container-unix-identity.md). Точный head, CI и review фиксируются в PR.

## Проверенная база

Свежий clone содержит PR #121–#127; ancestry `94c247d → main` проверена. Последний main CI [39278870476](https://github.com/hhypest/hh-homelab-wizard/actions/runs/39278870476) успешен. При начале открытых PR нет; review threads #122–#127 прочитаны, незакрытых нет. Это результаты существующей базы, а не нового запуска.

## Локальный запуск

Ubuntu 24.04 x64, SDK 10.0.401 / runtime 10.0.12. Source/image pins, NuGet versions и lock-файлы не менялись. Числовые identities в fixtures синтетические; настоящие NAS credentials не использовались.

```text
dotnet restore HhHomelab.Wizard.slnx --locked-mode -m:1 -p:BuildInParallel=false
dotnet build HhHomelab.Wizard.slnx --no-restore -c Release -m:1 -p:BuildInParallel=false
dotnet test HhHomelab.Wizard.slnx --no-build -c Release -m:1 -p:BuildInParallel=false
dotnet run --project tools/HhHomelab.Wizard.JournalProbe --no-build -c Release
dotnet run --project tools/HhHomelab.Wizard.ProfileCheck --no-build -c Release -- profiles/hh-homelab/1.3.0/research-profile.json
sh -n src/HhHomelab.Wizard.Infrastructure/nas-paths.sh
```

Locked restore и Release build успешны, 0 warnings / 0 errors. **1213 unit/fixture + 18 Desktop tests** пройдены без пропусков. JournalProbe: `journal.probe.restart-and-exclusive-lock.passed`, `journal.probe.plan-recovery.passed`, `journal.probe.passed`. ProfileCheck: 9 services / 19 artifacts / 118 steps, research inventory only. Последовательный MSBuild — параметр локальной среды; workflow не менялся.

| Сценарий | Наблюдаемый результат |
| --- | --- |
| Числовой пользователь не совпадает с владельцем data root или предка configuration root | Session UID/GID сохранены отдельно; владелец каталога не подставляется |
| Наблюдаемый root / обычный UID / int.MaxValue | Допустимый факт; выбранный PUID/PGID=0 по-прежнему запрещён запросом |
| Неполная identity, дополнительные поля, отрицательное/нечисловое значение, whitespace, overflow, повтор строки | Весь ответ InvalidResponse; snapshot не собирается |
| Старый response v2 | Отказ вместо default identity |
| Прямое создание snapshot с отрицательной identity | ArgumentException `paths.identity.invalid` |
| Совпадающие UID/primary GID и выбранные PUID/PGID | Две узкие проверки Passed; permissions Unverified, DSM ACL NotChecked |
| Несовпадение PUID, PGID или обеих величин | Соответствующая Blocking-проверка, создание графа отклонено до marker/directory/file writes |
| Перенумерация UID/GID того же имени | PrincipalFingerprint имени прежний, environment fingerprint изменён; свежая перепроверка Unchanged=false |
| Существующая медиатека другого владельца | Данные не присваиваются и не меняются; проверка реального доступа остаётся открытой |

Дополнительно встроенный read-only скрипт выполнен локально через `/bin/sh` с запросом v3 и восемью путями. Проверено соответствие строки identity действующим `geteuid/getegid` процесса и полное число наблюдений. Запрос v2 отвергнут с exit 2 без ответа. В отдельном временном PATH подставлен `id`, возвращающий ошибку: exit 2 без частичного успешного ответа. Каталоги данных/конфигурации не создавались, права не менялись. Это локальный Linux-процесс, не SSH/DSM доказательство.

## Замечания review

Perch исходного `b940785` предположил null в `VolumeOf` и неверный результат `contents_of`. У первого все текущие production callers получают уже валидированный layout, но public helper теперь явно отклоняет null, пустой/неполный/traversal path безопасным доменным кодом; регрессии проверяют отказ и прежний корректный том.

Для второго воспроизведён настоящий отказ: первая команда `ls` успешна, повторная завершается с ошибкой, а pipeline `head | wc` теряет её код и выдаёт `empty`. Чтение заменено `find` с остановкой после первого элемента и проверкой exit code; при ошибке, даже после частичного вывода, результат Unavailable. Вывод ограничен одним полным pathname и наружу не возвращается. Шесть Linux shell tests выполняют саму функцию из embedded source: пустота, hidden file, newline-only имя, broken symlink и отказ перечисления с/без частичного вывода. На Windows эти шесть случаев явно skipped; прежний Linux symlink test также skipped. Опции `find` на DSM требуют отдельного аппаратного подтверждения; при отсутствии команды успех не выдумывается.

## Сценарий настоящего SSH в CI

`ssh-runtime (ssh)` использует прежний одноразовый сервер с синтетическим `wizard` UID=1000/GID=1000. Проверка путей сверяет эту identity при data root владельца root. `ContainerIdentityScenarios` вызывает production compiler через настоящие `NasPreflightProbe`/`NasPathProbe`: совпадение IDs, свежая неизменная перепроверка, отдельные отказы PUID=1026 и PGID=100. Диагностический итог: `ssh-runtime.container-unix-identity: passed`. Всё остаётся read-only; fixture без DSM/Docker сохраняет prerequisite Blocking даже при совпадении IDs. Задание повторяет весь сценарий на двух свежих контейнерах.

Встроенный `paths.sh` fixture собирается из production source; definition hash вычисляется автоматически из файлов. Наличие кода не считается успехом: результат точного CI head фиксируется отдельно в PR. Docker/SSH runtime локально не запускался — Docker CLI/daemon отсутствуют. Native Win32/X11/Wayland и прежние runtime jobs проверяются матрицей CI, не повторной обычной клиентской приёмкой.

## Ограничения

Не доказаны фактическая запись контейнера, ACL, supplementary groups, user namespace/rootless mapping, setgid наследование и доступ ко всем предкам. Разрешённая первая политика требует primary PGID; другой PGID может быть технически допустим, но пока блокируется до отдельного подтверждённого механизма. Нет нового chown/chmod/sudo, сети или Compose up. Нельзя выдавать свежий план за защиту от ручных изменений после его создания. Полные #49/#50/#101 и M2/M3 открыты. NAS не подключался и не изменялся.

Markdown-relative links и `git diff --check` проверяются перед публикацией.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
