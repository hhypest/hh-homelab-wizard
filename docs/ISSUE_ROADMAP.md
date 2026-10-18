# Карта Milestones и Issues

[План разработки](DEVELOPMENT_PLAN.md) определяет требования и критерии этапов. [Milestones](https://github.com/hhypest/hh-homelab-wizard/milestones) и связанные [Issues](https://github.com/hhypest/hh-homelab-wizard/issues) ведут конкретную работу и доказательства приёмки. Эта карта связывает их; актуальные состояния задач читаются из GitHub.

[Project «hh-homelab-wizard — Разработка»](https://github.com/users/hhypest/projects/3) показывает эти Issues с нативным полем Milestone, текущим Status, исполнителями и PR. Представления выделяют работу первого выпуска, участие владельца, эпики и 2.0.0. [Правила Project](PROJECT.md) определяют обновление доски и отличают статус работы от фактической приёмки.

Текущая готовность продукта — [STATUS](STATUS.md). [Реестр независимого аудита](audit/2026-10-11-follow-up.md) связывает замечания и критерии с существующими задачами; #96–#103 включены в соответствующие Milestones, отдельный эпик аудита не создаётся.

## Этапы первого выпуска

| Milestone | Эпик | Результат и оставшаяся работа | Зависимости завершения |
| --- | --- | --- | --- |
| [M0](https://github.com/hhypest/hh-homelab-wizard/milestone/1) | [#18](https://github.com/hhypest/hh-homelab-wizard/issues/18) | Контракты закреплённого профиля; оставшиеся Media policies/indexers, DSM/сопряжение, панель/плееры и read-only capabilities NAS. | Нет предшествующего этапа. |
| [M1](https://github.com/hhypest/hh-homelab-wizard/milestone/2) | [#19](https://github.com/hhypest/hh-homelab-wizard/issues/19) | Каркас «Фокус» и [полная клиентская приёмка PR #110](m1/2026-10-18-owner-check.md): Windows 11 26H1, Ubuntu 24.04.4 LTS GNOME X11/Wayland. Остаток аудита — в STATUS/Issue. | Архитектурные решения M0 приняты по ADR 0004; полная приёмка M0 этим не закрывается. |
| [M2](https://github.com/hhypest/hh-homelab-wizard/milestone/3) | [#20](https://github.com/hhypest/hh-homelab-wizard/issues/20) | SSH/SFTP/host key, секреты, read-only preflight, пути/ACL/конфликты и конкретный план. | [#18](https://github.com/hhypest/hh-homelab-wizard/issues/18), [#19](https://github.com/hhypest/hh-homelab-wizard/issues/19) |
| [M3](https://github.com/hhypest/hh-homelab-wizard/milestone/4) | [#21](https://github.com/hhypest/hh-homelab-wizard/issues/21) | Журнал, ownership, executor/recovery, атомарные файлы/backup, fault injection и UI resume. | [#20](https://github.com/hhypest/hh-homelab-wizard/issues/20) |
| [M4](https://github.com/hhypest/hh-homelab-wizard/milestone/5) | [#22](https://github.com/hhypest/hh-homelab-wizard/issues/22) | Compose/readiness семи Media-сервисов, qBittorrent, Arr/Prowlarr/indexers, Jellyfin/Seerr и приёмка связей. | [#18](https://github.com/hhypest/hh-homelab-wizard/issues/18), [#20](https://github.com/hhypest/hh-homelab-wizard/issues/20), [#21](https://github.com/hhypest/hh-homelab-wizard/issues/21) |
| [M5](https://github.com/hhypest/hh-homelab-wizard/milestone/6) | [#23](https://github.com/hhypest/hh-homelab-wizard/issues/23) | HA Container/Monitor Docker/Socket Proxy, onboarding/HTTP, интерактивные flows и restart/loaded entries. | [#18](https://github.com/hhypest/hh-homelab-wizard/issues/18), [#21](https://github.com/hhypest/hh-homelab-wizard/issues/21), [#22](https://github.com/hhypest/hh-homelab-wizard/issues/22) |
| [M6](https://github.com/hhypest/hh-homelab-wizard/milestone/7) | [#24](https://github.com/hhypest/hh-homelab-wizard/issues/24) | DSM inventory/enable, роли и entity_id, безопасная обработка лишних плееров и полная панель. | [#23](https://github.com/hhypest/hh-homelab-wizard/issues/23) |
| [M7](https://github.com/hhypest/hh-homelab-wizard/milestone/8) | [#25](https://github.com/hhypest/hh-homelab-wizard/issues/25) | Многоуровневые проверки, реальные Media/HA/Jellyfin/YouTube/ночные сценарии, отчёт и support bundle. | [#22](https://github.com/hhypest/hh-homelab-wizard/issues/22), [#23](https://github.com/hhypest/hh-homelab-wizard/issues/23), [#24](https://github.com/hhypest/hh-homelab-wizard/issues/24) |
| [M8](https://github.com/hhypest/hh-homelab-wizard/milestone/9) | [#26](https://github.com/hhypest/hh-homelab-wizard/issues/26) | Аппаратные E2E трёх клиентских конфигураций, resume/сбои/данные, чистые пакеты и принятый выпуск 1.0.0. | [#25](https://github.com/hhypest/hh-homelab-wizard/issues/25) |

Зависимости таблицы определяют **завершение этапа**, а не запрет независимого инкремента. Design/fixtures и одноразовый стенд M2/M3 разрешены до полной M0/M1 по [ADR 0008](adr/0008-audit-development-sequence.md). В задачах #46–#54 это разделение обозначено явно; native blocked-by эпиков сохраняют условия полной приёмки. Реальные установочные мутации требуют готовности обязательных механизмов M3 и авторизованного конкретного плана.

## Версия 2.0.0

[Milestone 2.0.0](https://github.com/hhypest/hh-homelab-wizard/milestone/10) начинается после M8. Это согласованный объём настраиваемой **новой** установки и продолжения собственной сессии. Ремонт существующего стека, обычный Linux-сервер и облачные функции автоматически не включаются.

| Этап | Эпик | Дочерние задачи |
| --- | --- | --- |
| V2.1 | [#27](https://github.com/hhypest/hh-homelab-wizard/issues/27) — каталог компонентов и допустимый граф установки | [#77](https://github.com/hhypest/hh-homelab-wizard/issues/77), [#78](https://github.com/hhypest/hh-homelab-wizard/issues/78) |
| V2.2 | [#28](https://github.com/hhypest/hh-homelab-wizard/issues/28) — генерация конфигурации и операций по выбранному составу | [#79](https://github.com/hhypest/hh-homelab-wizard/issues/79), [#80](https://github.com/hhypest/hh-homelab-wizard/issues/80) |
| V2.3 | [#29](https://github.com/hhypest/hh-homelab-wizard/issues/29) — контракты Bazarr, обработчиков и вариантов связей | [#81](https://github.com/hhypest/hh-homelab-wizard/issues/81), [#82](https://github.com/hhypest/hh-homelab-wizard/issues/82), [#83](https://github.com/hhypest/hh-homelab-wizard/issues/83), [#84](https://github.com/hhypest/hh-homelab-wizard/issues/84), [#85](https://github.com/hhypest/hh-homelab-wizard/issues/85), [#86](https://github.com/hhypest/hh-homelab-wizard/issues/86) |
| V2.4 | [#30](https://github.com/hhypest/hh-homelab-wizard/issues/30) — выбор компонентов в UI, журнал и отчёт | [#87](https://github.com/hhypest/hh-homelab-wizard/issues/87), [#88](https://github.com/hhypest/hh-homelab-wizard/issues/88) |
| V2.5 | [#31](https://github.com/hhypest/hh-homelab-wizard/issues/31) — самостоятельные профили, документация и матрица 2.0.0 | [#89](https://github.com/hhypest/hh-homelab-wizard/issues/89), [#90](https://github.com/hhypest/hh-homelab-wizard/issues/90) |
| V2.6 | [#32](https://github.com/hhypest/hh-homelab-wizard/issues/32) — выпуск 2.0.0 и переход hh-homelab в архив | [#91](https://github.com/hhypest/hh-homelab-wizard/issues/91), [#92](https://github.com/hhypest/hh-homelab-wizard/issues/92) |

Обработчики исследуются отдельно: FlareSolverr [#82](https://github.com/hhypest/hh-homelab-wizard/issues/82), Byparr [#83](https://github.com/hhypest/hh-homelab-wizard/issues/83), Solvearr [#84](https://github.com/hhypest/hh-homelab-wizard/issues/84), NoFlare [#85](https://github.com/hhypest/hh-homelab-wizard/issues/85). NoFlare — самостоятельный сервис. Режим «Не использовать» и варианты Seerr/Prowlarr имеют свою задачу [#86](https://github.com/hhypest/hh-homelab-wizard/issues/86). Upstream совместимость и доступность образа проверяются до включения в устанавливаемый профиль.

Архивирование hh-homelab описано в [#92](https://github.com/hhypest/hh-homelab-wizard/issues/92): сначала перенос, проверенная самостоятельная поставка и выпуск 2.0.0, затем переход в архив выполняет владелец. Эта карта не выполняет архивирование и не меняет работающие установки.

## Приоритет после независимого аудита

| Очередь | Issue / Milestone | Результат |
| --- | --- | --- |
| P1 | [#99](https://github.com/hhypest/hh-homelab-wizard/issues/99) / M3 | Операции, ErrorCode/Kind и порты Application; первые образцы Qb/Arr |
| P1 | [#100](https://github.com/hhypest/hh-homelab-wizard/issues/100) / M0 | Единственный источник pins/roles и проверка clients/probes/CI |
| P1, после фундамента | [#101](https://github.com/hhypest/hh-homelab-wizard/issues/101) / M3 | Вертикаль qBittorrent на одноразовом SSH/Docker стенде и resume в новом процессе |
| P2, независимо | [#98](https://github.com/hhypest/hh-homelab-wizard/issues/98) / M1 | Читаемость, formatter/анализаторы, Dependabot, coverage/fixtures и pull retry |
| P2, до реальных экранов | [#103](https://github.com/hhypest/hh-homelab-wizard/issues/103) / M2 | Screen VM, typed interaction, валидация и UI mapping ошибок |
| P2, до M5 | [#102](https://github.com/hhypest/hh-homelab-wizard/issues/102) / M5 | Разделить HA, вынести probe helpers и journal intent |
| Организация / выполнено | [#96](https://github.com/hhypest/hh-homelab-wizard/issues/96) / M1 | Реестр аудита, план, STATUS и правила PR |

Модель #99 и профиль #100 можно проектировать независимо; runnable вертикаль использует исправленный транспорт и минимальные принятые стендовые части #46/#48/#49/#50/#51/#52/#53. #101 не подменяет полные критерии #57 или M4.

Клиентские #42–#45 [полностью приняты владельцем](m1/2026-10-18-owner-check.md); аппаратные #35–#40 продолжаются отдельно по согласованным протоколам. Новые HA-исследования вширь не должны задерживать вертикаль. Фиксированный состав v1 сохраняется; выбор графа V2 начинается после M8.

При первоначальном переносе аудита Project/native API были недоступны; это историческое ограничение того инкремента. Сейчас статусы и связи текущей работы проверяются в GitHub: #48 уже имеет карточку Project и native parent #20. Остальные связи проверяйте при выборе соответствующей задачи, не объявляйте синхронизацию всего backlog от обновления одной карточки.

## Как вести задачи

- Каждый Issue входит в Milestone. Эпики имеют `type:epic`; работа — `type:task`, исследования — `type:research`, приёмка — `type:acceptance`. Область определяется `area:nas`, `area:engine`, `area:media`, `area:homeassistant`, `area:ui` или `area:packaging`; тексты также используют `documentation`.
- Дочерние задачи связаны с эпиком нативными sub-issues. Обязательные предшественники указаны нативными blocked-by и в разделе зависимостей тела. Если изменился объём, синхронизируйте связи, Issue, эпик и план.
- `needs:hardware` означает необходимость согласованного NAS/устройств; `needs:manual` — интерактивного испытания или действия владельца/тестировщика. Это условия проверки, а не постоянное объявление задачи заблокированной: актуальные зависимости читаются из Issue.
- Перед PR выберите дочернюю задачу. Для полного выполнения допустимо `Closes #N`; для частичного результата используйте `Refs #N` и перечислите остаток. Не закрывайте приёмку только потому, что код слит.
- При закрытии приложите PR/commit, профиль/версии, среду, сценарии, результат и ограничения. Fixture, Docker, обычный клиентский GUI и настоящая DSM имеют разные доказательства. Секреты в Issues/PR не публикуются.
- Закрытие эпика требует приёмки всех обязательных дочерних задач, закрытие Milestone — его критериев. Чек-лист в теле эпика синхронизируется при изменении состояния; нативный список sub-issues отражает актуальный статус.
- Due dates пока не назначены: оценка зависит от контрактов, доступа к тестовому NAS и клиентских конфигураций. Промежуточные версии в плане являются предложениями, не опубликованными релизами.

## Уже подтверждённые результаты

[#33](https://github.com/hhypest/hh-homelab-wizard/issues/33) — базовые Media bootstrap-пробы; [#34](https://github.com/hhypest/hh-homelab-wizard/issues/34) — HA-контракты и серверные fixtures; [#41](https://github.com/hhypest/hh-homelab-wizard/issues/41) — демонстрационный каркас, self-contained публикации и native CI. Эти закрытые записи описывают ограниченный доказанный результат. Полная установка NAS, аппаратные свойства, playback и клиентская приёмка остаются в открытых задачах.

Документ: [CC BY 4.0](../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
