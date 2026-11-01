# Полный каталог документации

Тип: реестр документов и статусов.

[Выбрать рабочую задачу](README.md) · [Главная страница](../README.md) · [Текущая готовность и очередь](../ROADMAP.md)

Каталог охватывает 85 Markdown-документов репозитория, кроме импортированных текстов лицензий в `LICENSES/`. Сверено 01.11.2026 по `main` d5ca91d. Числа тестов, «сейчас» и «следующие шаги» в протоколах и архиве относятся к их исходной дате и базе.

## Как читать статусы

«Действующее описание», «план», «ADR», «протокол» и «архив» обозначают назначение документа. «Реализовано на fixtures», «проверено на стенде», «принято владельцем» и «аппаратная приёмка» — разные уровни доказательства; один не выводится из другого ([уровни](engineering/testing.md#уровни-доказательств)). Ответственность указана ролью, без назначения конкретного человека.

## Демонстрация

Пользователь пробной сборки. Актуализация — сопровождающий упаковки; текст копируется в пакет CI как `DEMO.md`.

| Документ | Тип и статус | Для чего читать |
| --- | --- | --- |
| [Инструкция пробной сборки](guide/demo-package.md) | Действующая инструкция | Запуск пакета на Windows 11 и Ubuntu X11/Wayland |

## Разработка и действующие технические карты

Разработчик. Актуализация — ответственный за архитектуру или затронутый модуль. Карты описывают существующую реализацию и ведут к решениям и протоколам.

| Документ | Тип и статус | Для чего читать |
| --- | --- | --- |
| [Разработка, запуск и сборка](development.md) | Действующая техническая справка | SDK, команды, DocsCheck/ProfileCheck, публикация и зависимости Linux |
| [Архитектура и границы](engineering/architecture.md) | Действующая техническая справка | Слои, движок операций и реализованные механизмы с ADR и протоколами |
| [Правила написания кода](engineering/code-style.md) | Действующее правило | Строки, условия, методы, имена, исключения и тесты |
| [Инструменты разработчика и изолированные пробы](engineering/developer-probes.md) | Действующая инструкция | QbProbe и Docker-пробы Media/HA, DesktopProbe: команды, изоляция и пределы |
| [Как выполнять проверки](engineering/testing.md) | Действующая инструкция | Уровни доказательств, задания CI и запись результата |

## Проверка и приёмка

Разработчик и проверяющий. Актуализация — ответственный за приёмку этапа. Сводки этапов перечисляют принятое и оставшееся; результаты отдельных запусков — в протоколах ниже.

| Документ | Тип и статус | Для чего читать |
| --- | --- | --- |
| [Проверка мастера](../TESTING.md) | Действующий вход в проверки | Короткий вход в методику, чек-листы и протоколы |
| [M0: исследование профиля и контракты — сводка этапа](verification/m0.md) | Текущий индекс приёмки M0 | Артефакты, инкременты HA и оставшееся до закрытия M0 |
| [M1: демонстрационный мастер «Фокус» — сводка этапа](verification/m1.md) | Текущий индекс приёмки M1 | Результаты клиентской и автоматической проверки «Фокуса» |
| [Демонстрация «Фокус»: клиентский чек-лист](verification/checklists/client-demo.md) | Действующий чек-лист / не результат выполнения | Шаги клиентской проверки демонстрации |

## Контракты адаптеров и профиля

Разработчик адаптеров Media/HA. Актуализация — ответственный за модуль; формулировки о ещё не созданных частях мастера относятся к дате исследования M0.

| Документ | Тип и статус | Для чего читать |
| --- | --- | --- |
| [Покрытие исходной инструкции](contracts/source-coverage.md) | Действующее описание | 55 шагов Media и 63 HA исходной инструкции и будущие операции |
| [M0: подключение Jellyfin к Home Assistant](contracts/jellyfin-flow.md) | Действующий контракт M0 / исходный срез | Config flow Jellyfin в HA и границы восстановления |
| [M0: локальный профиль Home Assistant](contracts/ha-local-profile.md) | Действующий контракт M0 / исходный срез | Доставка Monitor Docker и рендер локального YAML |
| [M0: серверный запуск локального профиля HA](contracts/ha-runtime.md) | Действующий контракт M0 / исходный срез | Полный HA-сервер, proxy и ускоренные сценарии |
| [M0: реестры HA, датчики DSM и роли устройств](contracts/ha-entity-roles.md) | Действующий контракт M0 / исходный срез | Реестры HA, датчики DSM и роли устройств |
| [M0: интерактивные config flows DSM, LG, Android TV и Cast](contracts/ha-device-flows.md) | Действующий контракт M0 / исходный срез | USER flows DSM, LG, Android TV и Cast |
| [M0: выбор обнаруженной интеграции и техническая привязка устройства](contracts/ha-discovery-binding.md) | Действующий контракт M0 / исходный срез | Выбор discovery flow и техническая привязка устройства |

## Планы и требования

Владелец и разработчик. Требования не утверждают реализацию; состояния задач читаются из GitHub.

| Документ | Тип и статус | Для чего читать |
| --- | --- | --- |
| [План разработки hh-homelab-wizard](plans/development-plan.md) | План / требования | Требования первого выпуска, этапы M0–M8, матрица проверок и 2.0.0 |
| [Карта Milestones и Issues](plans/milestones.md) | План / карта этапов | Milestones, эпики, зависимости и задачи 2.0.0 |

## Архитектурные решения

Разработчик, которому нужны причины решения. ADR не переписываются задним числом; изменение оформляется новым ADR.

| Документ | Тип и статус | Для чего читать |
| --- | --- | --- |
| [Архитектурные решения](adr/README.md) | Реестр документов и статусов | Все решения по темам с кратким статусом |
| [ADR 0001: границы первого кода](adr/0001-architecture.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0002: неопределённый результат, журнал и секреты](adr/0002-journal-and-secrets.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0003: Monitor Docker и состав локального профиля](adr/0003-components-and-features.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0004: начало M1 и незакрытые проверки M0](adr/0004-m1-entry.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0005: интерфейс мастера «Фокус»](adr/0005-focus-ui.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0006: первый каркас M1 и закреплённые UI-пакеты](adr/0006-desktop-demo.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0007: Windows 11 и Linux X11/Wayland](adr/0007-client-platforms.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0008: приоритет вертикального сценария после аудита](adr/0008-audit-development-sequence.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0009: снимок профиля и версии API](adr/0009-pinned-runtime-profile.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0010: операции Observe / Apply / Verify](adr/0010-operation-boundaries.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0011: локальный файловый журнал сессии](adr/0011-local-file-journal.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0012: SSH.NET и первый read-only порт подключения](adr/0012-ssh-connection-probe.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0013: первый read-only preflight NAS](adr/0013-read-only-nas-preflight.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0014: настоящий Docker preflight через SSH в одноразовой CI VM](adr/0014-docker-preflight-runtime.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0015: модель NAS, слушающие порты и место томов в read-only preflight](adr/0015-preflight-host-resources.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0016: read-only проверка путей медиатеки и конфигурации](adr/0016-remote-path-inspection.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0017: маркер владения установкой на NAS](adr/0017-installation-ownership-marker.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0018: создание маркера владения как первая журналируемая мутация NAS](adr/0018-ownership-marker-creation.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0019: удалённая блокировка установки на NAS](adr/0019-remote-installation-lock.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0020: неизменяемый граф конфигурации и восстановление](adr/0020-configuration-plan-recovery.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0021: конкретный read-only план qBittorrent](adr/0021-qbittorrent-installation-plan.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0022: создание конфигурационного файла через журнал](adr/0022-configuration-file-delivery.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0023: Compose и .env qBittorrent из плана и исполняемый граф](adr/0023-qbittorrent-configuration-graph.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0024: создание конфигурационных каталогов через журнал](adr/0024-configuration-directories.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0025: числовая identity SSH и PUID/PGID контейнера](adr/0025-container-unix-identity.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |
| [ADR 0026: read-only проверка имени Docker-сети](adr/0026-docker-network-preflight.md) | ADR / статус в тексте | Причина, альтернативы и границы решения |

## Аудит

Владелец и разработчик, работающие по находкам независимого аудита 11.10.2026.

| Документ | Тип и статус | Для чего читать |
| --- | --- | --- |
| [Интеграция независимого аудита 11.10.2026](audit/2026-10-11-follow-up.md) | Реестр находок | Решения и статусы по каждой находке; обновляется при исправлении |
| [Сведения об источнике независимого аудита](audit/2026-10-11-independent-review.md) | Протокол происхождения | Источник, база и хэш исходного файла аудита |

## Протоколы конкретных проверок

Разработчик и проверяющий. Результат действует только для указанной базы, среды и уровня доказательства; последующие проверки добавляют новый протокол, а не переписывают прежний.

| Документ | Тип и статус | Для чего читать |
| --- | --- | --- |
| [Протокол локальных проб M0 — 10.10.2026](verification/reports/2026-10-10-m0-local-probes.md) | Завершённый протокол / дата и база в тексте | Первый стенд M0 и device flows: операции, неудачные пробы, ограничения |
| [Локальный журнал: fixtures и принудительное завершение процессов — 18.10.2026](verification/reports/2026-10-18-file-journal.md) | Завершённый протокол / дата и база в тексте | Локальный журнал при принудительном завершении процессов |
| [Первый инкремент HTTP-транспорта: B1/B2 — 18.10.2026](verification/reports/2026-10-18-http-transport-b1-b2.md) | Завершённый протокол / дата и база в тексте | Воспроизведение и исправление B1/B2 транспорта |
| [Клиентская проверка владельцем — 18.10.2026](verification/reports/2026-10-18-m1-owner-check.md) | Завершённый протокол / дата и база в тексте | Клиентская приёмка M1 владельцем, PR #110 / cac10f0 |
| [Проверка границ операций #99 — 18.10.2026](verification/reports/2026-10-18-operation-contracts.md) | Завершённый протокол / дата и база в тексте | Границы операций #99 на fixtures |
| [Согласованность pins: первый инкремент #100 — 18.10.2026](verification/reports/2026-10-18-profile-consistency.md) | Завершённый протокол / дата и база в тексте | Снимок pins для клиентов, probes и CI (#100) |
| [Общий HTTP-транспорт и конечные дедлайны — 18.10.2026](verification/reports/2026-10-18-shared-http-transport.md) | Завершённый протокол / дата и база в тексте | Общий HTTP-транспорт и конечные дедлайны (#97) |
| [Первый SSH-инкремент: fixtures и локальный TCP — 18.10.2026](verification/reports/2026-10-18-ssh-probe.md) | Завершённый протокол / дата и база в тексте | SSH-порт на fixtures и локальном TCP |
| [Создание конфигурационных каталогов через журнал — 19.10.2026](verification/reports/2026-10-19-configuration-directories.md) | Завершённый протокол / дата и база в тексте | Каталоги монтирования под собственным маркером |
| [Создание конфигурационного файла через журнал — 19.10.2026](verification/reports/2026-10-19-configuration-file-delivery.md) | Завершённый протокол / дата и база в тексте | Создание конфигурационного файла без перезаписи |
| [Протокол: числовая SSH identity и план PUID/PGID — 19.10.2026](verification/reports/2026-10-19-container-unix-identity.md) | Завершённый протокол / дата и база в тексте | Сверка SSH identity с PUID/PGID |
| [Протокол: read-only имя Docker-сети — 19.10.2026](verification/reports/2026-10-19-docker-network-preflight.md) | Завершённый протокол / дата и база в тексте | Read-only проверка имени Docker-сети |
| [Docker/Compose preflight через настоящий SSH — 19.10.2026](verification/reports/2026-10-19-docker-preflight.md) | Завершённый протокол / дата и база в тексте | Docker/Compose preflight через настоящий SSH в CI VM |
| [Первый инкремент read-only preflight — 19.10.2026](verification/reports/2026-10-19-nas-preflight.md) | Завершённый протокол / дата и база в тексте | Первый read-only preflight NAS |
| [Создание маркера владения через журнал — 19.10.2026](verification/reports/2026-10-19-ownership-marker-creation.md) | Завершённый протокол / дата и база в тексте | Первая журналируемая мутация: создание маркера |
| [Маркер владения установкой: read-only наблюдение — 19.10.2026](verification/reports/2026-10-19-ownership-marker.md) | Завершённый протокол / дата и база в тексте | Read-only наблюдение маркера владения |
| [Протокол: конфигурационный граф и process-restart recovery — 19.10.2026](verification/reports/2026-10-19-plan-recovery.md) | Завершённый протокол / дата и база в тексте | Конфигурационный граф и восстановление после перезапуска |
| [Preflight: модель NAS, порты и место томов — 19.10.2026](verification/reports/2026-10-19-preflight-resources.md) | Завершённый протокол / дата и база в тексте | Модель NAS, порты и место томов |
| [Compose и .env qBittorrent из плана и исполняемый граф — 19.10.2026](verification/reports/2026-10-19-qbittorrent-configuration-graph.md) | Завершённый протокол / дата и база в тексте | Compose/.env qBittorrent и исполняемый граф |
| [Протокол: конкретный read-only план qBittorrent — 19.10.2026](verification/reports/2026-10-19-qbittorrent-plan.md) | Завершённый протокол / дата и база в тексте | Конкретный read-only план qBittorrent на fixtures |
| [Удалённая блокировка установки — 19.10.2026](verification/reports/2026-10-19-remote-lock.md) | Завершённый протокол / дата и база в тексте | Удалённая блокировка установки на стенде |
| [Read-only проверка путей — 19.10.2026](verification/reports/2026-10-19-remote-paths.md) | Завершённый протокол / дата и база в тексте | Read-only проверка путей |
| [Одноразовый OpenSSH/SFTP-стенд — 19.10.2026](verification/reports/2026-10-19-ssh-runtime.md) | Завершённый протокол / дата и база в тексте | Одноразовый OpenSSH/SFTP-стенд |

Отдельный файл [image evidence M0](verification/reports/2026-10-10-m0-image-evidence.json) содержит OCI digests образов первого стенда.

## Прежние смешанные документы

Разработчик при восстановлении истории. Действующее содержание перенесено; архив сохраняет исходный текст.

| Документ | Тип и статус | Для чего читать |
| --- | --- | --- |
| [M0: исследование профиля и первые контракты](archive/m0-readme-before-2026-11-01.md) | Архив исходной смешанной инструкции | Исходный README M0 до разделения на сводку, пробы и разработку |
| [M1: демонстрационный мастер «Фокус»](archive/m1-readme-before-2026-11-01.md) | Архив исходной смешанной инструкции | Исходный README M1 до разделения на сводку, чек-лист и разработку |

## Общие правила и навигация

Все участники. Актуализация — сопровождающий проекта; очередь принадлежит владельцу.

| Документ | Тип и статус | Для чего читать |
| --- | --- | --- |
| [.github/PULL_REQUEST_TEMPLATE.md](../.github/PULL_REQUEST_TEMPLATE.md) | Действующее правило | Шаблон описания и условия приёмки PR |
| [Инструкции для агентов](../AGENTS.md) | Действующее правило | Обязательные ограничения автоматизированных изменений |
| [История изменений](../CHANGELOG.md) | История изменений | Фактически внесённые изменения |
| [Участие в разработке](../CONTRIBUTING.md) | Действующее правило | Рабочий процесс, проверки и правила документации |
| [hh-homelab-wizard](../README.md) | Действующее описание | Назначение, границы, статус и начало работы |
| [Дорожная карта](../ROADMAP.md) | Текущий план | Единственная текущая готовность и очередь |
| [Сторонние компоненты](../THIRD_PARTY_NOTICES.md) | Действующее описание | Версии, источники и тексты лицензий зависимостей пакета |
| [Документация проекта](README.md) | Навигатор | Короткий вход по задачам читателя |
| [Полный каталог документации](catalog.md) | Реестр | Полный перечень, типы, роли и карта прежних путей |
| [GitHub Project и прозрачность разработки](github-project.md) | Действующее правило | Представления и статусы Project, ведение Issues и Milestones |
| [Текущая готовность](STATUS.md) | Стабильная страница перехода | Прежний адрес; переход в ROADMAP |
| [План разработки hh-homelab-wizard](DEVELOPMENT_PLAN.md) | Стабильная страница перехода | Прежний адрес; переход в plans/development-plan |

## Как поддерживать документацию

- Текущий контракт или инструкция хранится в одном действующем описании; причины — в ADR, результат конкретной проверки — в датированном протоколе `docs/verification/reports/ГГГГ-ММ-ДД-тема.md`, готовность и очередь — только в [ROADMAP](../ROADMAP.md).
- Каждый документ в `docs/`, кроме ADR и архива, и `TESTING.md` начинается строкой `Тип: …` (действующее описание, текущий индекс, план, протокол, реестр, навигатор или страница перехода). ADR объявляет «Статус:», архив — дату и точный исходный commit.
- При добавлении или переносе документа обновляйте этот каталог, [реестр ADR](adr/README.md) при необходимости и все входящие ссылки в том же PR; прежний путь записывайте в карту ниже. Имена файлов — строчные, через дефис.
- `dotnet run --project tools/HhHomelab.Wizard.DocsCheck --no-build -c Release` проверяет ссылки и якоря, полноту и счётчик каталога, строки «Тип»/«Статус», происхождение архива, существование упомянутых путей `src/`, `tests/`, `tools/`, `profiles/` и `.github/`, а также проектов в командах `dotnet run --project`. Проверка входит в задание CI `core`.

## Карта прежних путей

Переносы выполнены вместе с внутренними ссылками. Для двух адресов, на которые ссылаются открытые Issues, прежняя страница остаётся переходом; остальные прежние ссылки из Issues и PR ведут на удалённый путь и сопоставляются по этой таблице.

| Прежний путь | Новый документ |
| --- | --- |
| `docs/CODE_STYLE.md` | [Правила написания кода](engineering/code-style.md) |
| `docs/DEVELOPMENT_PLAN.md` | [План разработки hh-homelab-wizard](plans/development-plan.md) |
| `docs/ISSUE_ROADMAP.md` | [Карта Milestones и Issues](plans/milestones.md) |
| `docs/PROJECT.md` | [GitHub Project и прозрачность разработки](github-project.md) |
| `docs/STATUS.md` | [Дорожная карта](../ROADMAP.md) |
| `docs/design/assets/focus-plan.png` | [focus-plan.png](assets/focus-plan.png) |
| `docs/m0/COVERAGE.md` | [Покрытие исходной инструкции](contracts/source-coverage.md) |
| `docs/m0/HA_DEVICE_FLOWS.md` | [M0: интерактивные config flows DSM, LG, Android TV и Cast](contracts/ha-device-flows.md) |
| `docs/m0/HA_DISCOVERY_BINDING.md` | [M0: выбор обнаруженной интеграции и техническая привязка устройства](contracts/ha-discovery-binding.md) |
| `docs/m0/HA_ENTITY_ROLES.md` | [M0: реестры HA, датчики DSM и роли устройств](contracts/ha-entity-roles.md) |
| `docs/m0/HA_LOCAL_PROFILE.md` | [M0: локальный профиль Home Assistant](contracts/ha-local-profile.md) |
| `docs/m0/HA_RUNTIME.md` | [M0: серверный запуск локального профиля HA](contracts/ha-runtime.md) |
| `docs/m0/JELLYFIN_FLOW.md` | [M0: подключение Jellyfin к Home Assistant](contracts/jellyfin-flow.md) |
| `docs/m0/PROBE_REPORT.md` | [Протокол локальных проб M0](verification/reports/2026-10-10-m0-local-probes.md) |
| `docs/m0/PROFILE_CONSISTENCY_REPORT.md` | [Согласованность pins: первый инкремент #100](verification/reports/2026-10-18-profile-consistency.md) |
| `docs/m0/README.md` | [M0: исследование профиля и контракты — сводка этапа](verification/m0.md) |
| `docs/m0/SHARED_HTTP_REPORT.md` | [Общий HTTP-транспорт и конечные дедлайны](verification/reports/2026-10-18-shared-http-transport.md) |
| `docs/m0/TRANSPORT_REPORT.md` | [Первый инкремент HTTP-транспорта: B1/B2](verification/reports/2026-10-18-http-transport-b1-b2.md) |
| `docs/m0/image-evidence.json` | [2026-10-10-m0-image-evidence.json](verification/reports/2026-10-10-m0-image-evidence.json) |
| `docs/m1/2026-10-18-owner-check.md` | [Клиентская проверка владельцем](verification/reports/2026-10-18-m1-owner-check.md) |
| `docs/m1/PACKAGE_README.md` | [Демонстрация hh-homelab wizard M1](guide/demo-package.md) |
| `docs/m1/README.md` | [M1: демонстрационный мастер «Фокус» — сводка этапа](verification/m1.md) |
| `docs/m1/assets/focus-plan-native.png` | [focus-plan-native.png](assets/focus-plan-native.png) |
| `docs/testing/2026-10-18-file-journal.md` | [Локальный журнал: fixtures и принудительное завершение процессов](verification/reports/2026-10-18-file-journal.md) |
| `docs/testing/2026-10-18-operation-contracts.md` | [Проверка границ операций #99](verification/reports/2026-10-18-operation-contracts.md) |
| `docs/testing/2026-10-18-ssh-probe.md` | [Первый SSH-инкремент: fixtures и локальный TCP](verification/reports/2026-10-18-ssh-probe.md) |
| `docs/testing/2026-10-19-configuration-directories.md` | [Создание конфигурационных каталогов через журнал](verification/reports/2026-10-19-configuration-directories.md) |
| `docs/testing/2026-10-19-configuration-file-delivery.md` | [Создание конфигурационного файла через журнал](verification/reports/2026-10-19-configuration-file-delivery.md) |
| `docs/testing/2026-10-19-container-unix-identity.md` | [Протокол: числовая SSH identity и план PUID/PGID](verification/reports/2026-10-19-container-unix-identity.md) |
| `docs/testing/2026-10-19-docker-network-preflight.md` | [Протокол: read-only имя Docker-сети](verification/reports/2026-10-19-docker-network-preflight.md) |
| `docs/testing/2026-10-19-docker-preflight.md` | [Docker/Compose preflight через настоящий SSH](verification/reports/2026-10-19-docker-preflight.md) |
| `docs/testing/2026-10-19-nas-preflight.md` | [Первый инкремент read-only preflight](verification/reports/2026-10-19-nas-preflight.md) |
| `docs/testing/2026-10-19-ownership-marker-creation.md` | [Создание маркера владения через журнал](verification/reports/2026-10-19-ownership-marker-creation.md) |
| `docs/testing/2026-10-19-ownership-marker.md` | [Маркер владения установкой: read-only наблюдение](verification/reports/2026-10-19-ownership-marker.md) |
| `docs/testing/2026-10-19-plan-recovery.md` | [Протокол: конфигурационный граф и process-restart recovery](verification/reports/2026-10-19-plan-recovery.md) |
| `docs/testing/2026-10-19-preflight-resources.md` | [Preflight: модель NAS, порты и место томов](verification/reports/2026-10-19-preflight-resources.md) |
| `docs/testing/2026-10-19-qbittorrent-configuration-graph.md` | [Compose и .env qBittorrent из плана и исполняемый граф](verification/reports/2026-10-19-qbittorrent-configuration-graph.md) |
| `docs/testing/2026-10-19-qbittorrent-plan.md` | [Протокол: конкретный read-only план qBittorrent](verification/reports/2026-10-19-qbittorrent-plan.md) |
| `docs/testing/2026-10-19-remote-lock.md` | [Удалённая блокировка установки](verification/reports/2026-10-19-remote-lock.md) |
| `docs/testing/2026-10-19-remote-paths.md` | [Read-only проверка путей](verification/reports/2026-10-19-remote-paths.md) |
| `docs/testing/2026-10-19-ssh-runtime.md` | [Одноразовый OpenSSH/SFTP-стенд](verification/reports/2026-10-19-ssh-runtime.md) |

Документ: [CC BY 4.0](../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
