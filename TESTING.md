# Проверка мастера

Тип: действующий вход в проверки. [Документация](docs/README.md) · [Текущая готовность](ROADMAP.md).

Выберите задачу; прежние результаты не нужно читать как последовательность новых действий.

| Задача | Основной документ |
| --- | --- |
| Подготовить среду, выполнить build/test, DocsCheck и ProfileCheck | [Разработка](docs/development.md#сборка-тесты-и-запуск) |
| Понять уровни доказательств и задания CI | [Методика](docs/engineering/testing.md) |
| Запустить изолированную пробу qBittorrent, Arr, Jellyfin/Seerr, HA или DesktopProbe | [Инструменты разработчика](docs/engineering/developer-probes.md) |
| Проверить демонстрацию «Фокус» на клиентской ОС | [Чек-лист демонстрации](docs/verification/checklists/client-demo.md) |
| Прочитать настоящий NAS без изменений: SSH, preflight, пути, сеть, Compose | [Read-only проверка NAS](docs/verification/checklists/nas-read-only.md) |
| Узнать, что принято и что осталось в M0 и M1 | [Сводка M0](docs/verification/m0.md) · [Сводка M1](docs/verification/m1.md) |
| Найти факт проверки конкретного инкремента | [Протоколы](docs/catalog.md#протоколы-конкретных-проверок) |

Fixture, Docker, CI GUI, обычный клиентский GUI и реальная DSM — разные уровни доказательств. Сценарий проверки и запись о его выполнении — разные документы.

Документ: [CC BY 4.0](LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
