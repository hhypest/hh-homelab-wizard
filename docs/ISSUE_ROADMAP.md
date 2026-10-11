# Карта Milestones и Issues

[План разработки](DEVELOPMENT_PLAN.md) определяет требования и критерии этапов. [Milestones](https://github.com/hhypest/hh-homelab-wizard/milestones) и связанные [Issues](https://github.com/hhypest/hh-homelab-wizard/issues) ведут конкретную работу и доказательства приёмки. Эта карта связывает их; актуальные состояния задач читаются из GitHub.

Первоначальная структура создана 2026-10-11 после слияния PR #17: **10 Milestones, 15 эпиков и 60 дочерних задач**. Три дочерние задачи закрыты как исторические результаты с доказательствами. M0 и M1 остаются открытыми; M2–M8 и 2.0.0 не объявлены выполненными. Код установщика и реальные ресурсы NAS этим изменением не создаются.

## Этапы первого выпуска

| Milestone | Эпик | Результат и оставшаяся работа | Зависимости завершения |
| --- | --- | --- | --- |
| [M0](https://github.com/hhypest/hh-homelab-wizard/milestone/1) | [#18](https://github.com/hhypest/hh-homelab-wizard/issues/18) | Контракты закреплённого профиля; оставшиеся Media policies/indexers, DSM/сопряжение, панель/плееры и read-only capabilities NAS. | Нет предшествующего этапа. |
| [M1](https://github.com/hhypest/hh-homelab-wizard/milestone/2) | [#19](https://github.com/hhypest/hh-homelab-wizard/issues/19) | Каркас «Фокус» уже слит; осталось принять обычные Windows 11, Ubuntu X11/Wayland и окончательную клиентскую матрицу. | Архитектурные решения M0 приняты по ADR 0004; полная приёмка M0 этим не закрывается. |
| [M2](https://github.com/hhypest/hh-homelab-wizard/milestone/3) | [#20](https://github.com/hhypest/hh-homelab-wizard/issues/20) | SSH/SFTP/host key, секреты, read-only preflight, пути/ACL/конфликты и конкретный план. | [#18](https://github.com/hhypest/hh-homelab-wizard/issues/18), [#19](https://github.com/hhypest/hh-homelab-wizard/issues/19) |
| [M3](https://github.com/hhypest/hh-homelab-wizard/milestone/4) | [#21](https://github.com/hhypest/hh-homelab-wizard/issues/21) | Журнал, ownership, executor/recovery, атомарные файлы/backup, fault injection и UI resume. | [#20](https://github.com/hhypest/hh-homelab-wizard/issues/20) |
| [M4](https://github.com/hhypest/hh-homelab-wizard/milestone/5) | [#22](https://github.com/hhypest/hh-homelab-wizard/issues/22) | Compose/readiness семи Media-сервисов, qBittorrent, Arr/Prowlarr/indexers, Jellyfin/Seerr и приёмка связей. | [#18](https://github.com/hhypest/hh-homelab-wizard/issues/18), [#20](https://github.com/hhypest/hh-homelab-wizard/issues/20), [#21](https://github.com/hhypest/hh-homelab-wizard/issues/21) |
| [M5](https://github.com/hhypest/hh-homelab-wizard/milestone/6) | [#23](https://github.com/hhypest/hh-homelab-wizard/issues/23) | HA Container/Monitor Docker/Socket Proxy, onboarding/HTTP, интерактивные flows и restart/loaded entries. | [#18](https://github.com/hhypest/hh-homelab-wizard/issues/18), [#21](https://github.com/hhypest/hh-homelab-wizard/issues/21), [#22](https://github.com/hhypest/hh-homelab-wizard/issues/22) |
| [M6](https://github.com/hhypest/hh-homelab-wizard/milestone/7) | [#24](https://github.com/hhypest/hh-homelab-wizard/issues/24) | DSM inventory/enable, роли и entity_id, безопасная обработка лишних плееров и полная панель. | [#23](https://github.com/hhypest/hh-homelab-wizard/issues/23) |
| [M7](https://github.com/hhypest/hh-homelab-wizard/milestone/8) | [#25](https://github.com/hhypest/hh-homelab-wizard/issues/25) | Многоуровневые проверки, реальные Media/HA/Jellyfin/YouTube/ночные сценарии, отчёт и support bundle. | [#22](https://github.com/hhypest/hh-homelab-wizard/issues/22), [#23](https://github.com/hhypest/hh-homelab-wizard/issues/23), [#24](https://github.com/hhypest/hh-homelab-wizard/issues/24) |
| [M8](https://github.com/hhypest/hh-homelab-wizard/milestone/9) | [#26](https://github.com/hhypest/hh-homelab-wizard/issues/26) | Аппаратные E2E трёх клиентских конфигураций, resume/сбои/данные, чистые пакеты и принятый выпуск 1.0.0. | [#25](https://github.com/hhypest/hh-homelab-wizard/issues/25) |

M3: дизайн журнала и fixtures [#51](https://github.com/hhypest/hh-homelab-wizard/issues/51) можно выполнять параллельно M1; интеграция и завершение M3 требуют M2. M2 начинается после приёмки M0/M1. Наличие кода/CI не снимает эти условия; реальные установочные мутации требуют готового M3 и авторизованного конкретного плана.

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

## Ближайшие действия

1. Завершить клиентскую приёмку M1: Windows 11 [#42](https://github.com/hhypest/hh-homelab-wizard/issues/42), Ubuntu 24.04 X11 [#43](https://github.com/hhypest/hh-homelab-wizard/issues/43), Ubuntu 24.04 Wayland [#44](https://github.com/hhypest/hh-homelab-wizard/issues/44); свести результаты в [#45](https://github.com/hhypest/hh-homelab-wizard/issues/45). Self-contained пакет запускается без установленного .NET/SDK; CI Server/Xvfb/Weston не заменяет интерактивный результат.
2. На изолированных стендах дополнить Media-контракты [#35](https://github.com/hhypest/hh-homelab-wizard/issues/35) и полный dashboard/зависимости плееров [#38](https://github.com/hhypest/hh-homelab-wizard/issues/38).
3. По отдельно согласованному аппаратному протоколу подтвердить DSM-сенсоры [#36](https://github.com/hhypest/hh-homelab-wizard/issues/36), flows/identity устройств [#37](https://github.com/hhypest/hh-homelab-wizard/issues/37) и read-only capabilities NAS [#39](https://github.com/hhypest/hh-homelab-wizard/issues/39). Доступы вводятся локально.
4. Принять покрытие M0 в [#40](https://github.com/hhypest/hh-homelab-wizard/issues/40); после M0/M1 перейти к эпикам M2/M3. План и актуальные blocked-by определяют доступную следующую задачу.

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

