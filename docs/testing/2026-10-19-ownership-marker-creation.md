# Создание маркера владения через журнал

Дата: 2026-10-19. База: ветка PR #117 (`79f8e40`, read-only маркер). Задачи: [#51](https://github.com/hhypest/hh-homelab-wizard/issues/51), [#53](https://github.com/hhypest/hh-homelab-wizard/issues/53), prerequisites [#101](https://github.com/hhypest/hh-homelab-wizard/issues/101). SDK 10.0.401, SSH.NET 2026.0.0, build-time profile hh-homelab 1.3.0; зависимости не изменены. Решение: [ADR 0018](../adr/0018-ownership-marker-creation.md).

## Локальные fixtures

Настоящий `FileOperationJournal` во временном каталоге и `ConfigurationOperationScenario`; NAS — синтетическое состояние за поддельным SSH wire:

- новая установка: Applied, ровно одна запись канонического маркера; повтор — AlreadySatisfied без записи;
- ответ потерян после записи: Unknown/WriteUncertain; новый запуск с тем же журналом — Applied через наблюдение, запись по-прежнему одна; затем AlreadySatisfied;
- канал оборвался после отправки без изменения: Unknown, и новый запуск остаётся Unknown без второй попытки записи;
- чужой маркер, содержимое без маркера, другой профиль: Conflict без записи;
- `staged` остаток: Applied;
- доказанный отказ — Rejected/WriteRejected; сбой соединения до отправки — Transport без записи;
- разбор кода выхода: подтверждение только при точном выводе, отказ только для 2/3 без вывода, всё остальное Unknown;
- запрос несёт каноническую форму маркера, которая читается обратно строгим парсером; путь не попадает в текст команды; запрос с другим сервисом или установкой недопустим, спецификация зависит от корня.

Скрипт записи запущен локально (Linux, dash): создание, идемпотентный повтор, отказ при другом маркере, непустом корне, мусоре в каталоге маркера, отсутствующем родителе, symlink-корне; завершение `staged`; от `nobody` каталог только для чтения — отказ 3, доступный — создание. Нарушение формата запроса — код 2.

Локально Linux / SDK 10.0.401: locked restore, Release build без предупреждений и ошибок, все unit/fixture и Desktop тесты, JournalProbe и ProfileCheck. Docker daemon локально недоступен.

## Одноразовый OpenSSH-стенд в CI

ForceCommand исполняет embedded-копию того же `nas-marker-write.sh`; запрос остаётся на stdin. Control plane создаёт `/volume1/apps` (владелец — пользователь SSH) со `staged`-остатком и root-owned `/volume1/readonly`. Журнал — новый временный каталог runner; повторное открытие журнала имитирует новый процесс. Проба от `wizard` ожидает:

1. `/volume1/apps/created`: Applied, затем наблюдается своя установка, повтор — AlreadySatisfied; `staged` — новая установка и Applied;
2. режим `marker-lost-response`: скрипт действительно пишет, ответ не приходит → Unknown/WriteUncertain при таймауте записи 3 с; после сброса — Applied через наблюдение, затем AlreadySatisfied;
3. чужой маркер и `/volume1/docker/hh` без маркера — Conflict, маркер не изменён; недоступный для записи `/volume1/readonly/hh` — Rejected/WriteRejected, корень не создан.

Результат CI, commit и definition hash фиксируются в PR и #51; наличие сценария не объявляется успешным прогоном.

## Не проверено

Remote lock между компьютерами, fsync каталога и сбой питания, recovery pending intent без изменений (#52), удаление/компенсация маркера, DS725+/DSM ACL/PUID/PGID и утилиты DSM.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
