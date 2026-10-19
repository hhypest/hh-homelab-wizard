# Создание конфигурационных каталогов через журнал

Дата: 2026-10-19. База: `main` / `59abc3a`. Задачи: [#53](https://github.com/hhypest/hh-homelab-wizard/issues/53), [#101](https://github.com/hhypest/hh-homelab-wizard/issues/101). SDK 10.0.401, SSH.NET 2026.0.0, build-time profile hh-homelab 1.3.0; зависимости не изменены. Решение: [ADR 0024](../adr/0024-configuration-directories.md).

## Локальные fixtures

Настоящий `FileOperationJournal` и `ConfigurationOperationScenario`; NAS — синтетический каталог за поддельным `INasDirectoryWire` с правилами скриптов.

- Нет каталога: Applied, одно создание с запрошенным режимом. Повтор — AlreadySatisfied без создания.
- Потеря ответа — Unknown; новый процесс подтверждает наблюдением, создание одно.
- Другой режим, каталог чужого владельца (root от Docker), файл на пути, symlink, чужой маркер, корень без маркера — Conflict без создания.
- Унаследованный setgid (`present:2750:own`, режим 0x5E8) — AlreadySatisfied для 750.
- Доказанный отказ — Rejected. Другой host key на соединении записи — IdentityMismatch без создания. Сбой соединения — Transport.
- Ресурс другого endpoint или профиля отвергается при создании операции.
- Путь: правила файлов плюс запрет `-` в начале компонента. Режимы только 700/750/755. Путь длиннее 255 отвергается до хеширования. Спецификация зависит от режима.
- Протокол: параметры только в stdin; подтверждение только при точном выводе; ответ чтения принимается только в точной форме.

## Скрипты на настоящем Linux

dash (как `/bin/sh`) и bash, GNU coreutils:
- нет каталога — `absent`; создание вложенного `media/config/qbittorrent` с режимом 750 — родители 755, сам каталог 750; повтор — `already-present`;
- тот же путь с режимом 755 — отказ 3, режим остался 750;
- файл на пути — отказ 3, чтение — `not-directory`;
- каталог-symlink на посторонний — отказ 3, чтение — `redirected`, посторонний каталог пуст;
- чужой маркер — отказ 3, чтение — `marker=different`, `not-checked`;
- `../x`, `media/-p`, режим 777 — код 2;
- родитель с setgid (2755): создание `media/watch` 750 — `created`, фактически 2750; повтор — `already-present`; чтение — `present:2750:own`;
- каталог другого uid (750): чтение — `present:2750:other`, создание — отказ 3;
- под bash создание с 700 даёт 700;
- от `nobody` в корне, принадлежащем root, — отказ 3 без изменений.

Локально Linux / SDK 10.0.401: Release build без предупреждений и ошибок, все unit/fixture и Desktop тесты.

## Одноразовый OpenSSH-стенд в CI

Скрипты встроены в стенд (BusyBox Alpine), проба от `wizard` выполняется после сценариев файлов на корне `/volume1/apps/created`:

1. `media/config/qbittorrent` (755) уже создан как родитель файла — AlreadySatisfied;
2. `media/watch` (755) — Applied, затем AlreadySatisfied;
3. `media/state/deep/leaf` (700) в режиме `directory-lost-response` — Unknown, затем Applied наблюдением;
4. `media/watch` с режимом 700 — Conflict, режим не изменён;
5. `media/compose.yaml` как каталог — Conflict;
6. `linked/new`, где `linked` — symlink на `/volume1/apps/file-victim`, — Conflict, цель пуста;
7. чужой корень `/volume1/docker/foreign` — Conflict.

Результат CI фиксируется в PR и #53; наличие сценария не объявляется успешным прогоном.

## Не проверено

Шаги каталогов в графе qBittorrent; каталоги данных; PUID/PGID и DSM ACL; `mkdir -m` на DSM.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
