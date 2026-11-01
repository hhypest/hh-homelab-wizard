# Как выполнять проверки

Тип: действующая инструкция для разработчика и проверяющего. Сверено 01.11.2026 по `.github/workflows/ci.yml` на `main` d5ca91d.

[Документация](../README.md) · [TESTING](../../TESTING.md) · [Разработка](../development.md) · [Инструменты разработчика](developer-probes.md)

## Уровни доказательств

Fixtures, Docker-стенд, CI native GUI, обычный клиентский рабочий стол и настоящая DSM имеют разные доказательства и не заменяют друг друга. Незапущенная проверка остаётся непроверенной. Наличие workflow не доказывает его успех: результат конкретного запуска проверяется в Actions и фиксируется в PR, Issue или протоколе. Пробы на NAS выполняются только в авторизованном задании и согласованной среде.

| Уровень | Где выполняется | Что не доказывает |
| --- | --- | --- |
| Unit/fixture/contract | `dotnet test`, оба runner'а `core` | Поведение настоящего сервиса, SSH или NAS |
| Headless Desktop | `tests/HhHomelab.Wizard.DesktopTests` | Нативное окно и клиентскую приёмку |
| Native GUI в CI | `desktop`: Win32, X11 под Xvfb, Wayland под Weston | Приёмку на Windows 11 и обычном сеансе Ubuntu |
| Одноразовый стенд | `*-runtime` задания и [инструменты разработчика](developer-probes.md) | DSM ACL, аппаратуру, реальные устройства |
| Клиентская приёмка | Владелец по [чек-листу](../verification/checklists/client-demo.md) | Работу с NAS |
| Аппаратная DSM | Только авторизованное задание | — |

## Перед PR

Выполните команды из [разработки](../development.md#сборка-тесты-и-запуск): locked restore, Release build, `dotnet test`, DocsCheck и ProfileCheck. Для исправления поведения добавьте регрессию, которая демонстрирует исходную ошибку. Для мутаций проверяйте ownership/identity/conflicts, timeout и caller cancellation до/после отправки, lost response и resume. Полные правила — в [CONTRIBUTING](../../CONTRIBUTING.md#проверки).

## Задания CI

| Задание | Среда | Что проверяет |
| --- | --- | --- |
| `core` | Windows, Ubuntu 24.04 | Политика повторов pull, выбор образов профиля, locked restore, build, тесты, DocsCheck, JournalProbe и ProfileCheck |
| `desktop` | Windows, Ubuntu 24.04 | Self-contained публикация, native probe Win32/X11/Wayland, пробный пакет с лицензиями и `DEMO.md`, сборка NasReadOnlyCheck |
| `ssh-runtime` (`ssh`, `docker`) | Ubuntu 24.04 | SshDockerProbe дважды на свежих контейнерах; вариант `docker` — preflight, имя сети и наблюдение Compose-проекта на настоящем Docker через SSH |
| `qb-runtime` | Ubuntu 24.04 | QbDockerProbe дважды |
| `arr-runtime` | Ubuntu 24.04 | ArrDockerProbe дважды |
| `media-runtime` | Ubuntu 24.04 | MediaDockerProbe дважды |
| `ha-runtime` | Ubuntu 24.04 | HaDockerProbe дважды |
| `ha-profile-runtime` | Ubuntu 24.04 | HaProfileProbe дважды |
| `ha-local-runtime` | Ubuntu 24.04 | HaRuntimeProbe дважды |

Задания `*-runtime` используют только заранее закреплённые образы профиля; privileged Docker-режим допустим только в одноразовой GitHub-hosted VM ([ADR 0014](../adr/0014-docker-preflight-runtime.md)).

## Запись результата

Результат запуска указывает commit/profile/версии, среду, сценарии и ограничения. Датированный протокол сохраняется в `docs/verification/reports/` с именем `ГГГГ-ММ-ДД-тема.md` и строкой `Тип: протокол проверки` и добавляется в [каталог](../catalog.md#протоколы-конкретных-проверок). Исторические числа в протоколах не заменяются новыми; текущие числа тестов в прозу не копируются. Сценарий проверки и запись о его выполнении — разные документы.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
