# Согласованность pins: первый инкремент #100

Дата: 18.10.2026. Baseline: `b7aff12d2ff96d05fa20d964d30f6b384df11590`, merge PR #107. Решение и совместимость: [ADR 0009](../adr/0009-pinned-runtime-profile.md). Текущая готовность — [STATUS](../STATUS.md), commit и результаты CI текущего PR — в [#100](https://github.com/hhypest/hh-homelab-wizard/issues/100).

## Изменение

В схеме 2 исследовательского профиля точные image tags и ожидаемые ответы version API находятся рядом. Значения извлечены из прежних проверок клиентов; версии сервисов и исходные fingerprints не обновлены. Клиенты qBittorrent, Arr, Jellyfin, Seerr и HTTP/WebSocket Home Assistant используют один встроенный снимок. Docker-пробы используют его образы и отклоняют несовпадающий внешний профиль до подключения к Docker. Monitor Docker сверяет commit/manifest/repository/license с lock и сохраняет проверку байтов архива.

`ProfileCheck` сохраняет режим проверки каталога и исходного checkout. Новый режим выдаёт только образы для выбранных сервисов после проверки совместимости:

```text
dotnet run --project tools/HhHomelab.Wizard.ProfileCheck --no-build --configuration Release -- --images profiles/hh-homelab/1.3.0/research-profile.json qbittorrent radarr
```

CI helper строит этот инструмент с locked restore и использует результат до вызова прежнего acquisition helper. Неизвестный сервис после допустимого не приводит к частичному pull. Сбой селектора даже с частичным stdout и пустой результат блокируют все вызовы Docker. Приобретение образов по-прежнему отделено от применения настроек и не повторяет мутации.

## Проверка и воспроизведение

Среда локальной проверки: Windows x64, SDK 10.0.401/runtime 10.0.12. Locked restore, Release build, полный unit/fixture и headless набор запускаются командами [README](../../README.md#сборка-и-демонстрация). Итоговый результат и точный commit фиксируются в PR/#100; отчёты локально — `artifacts/test-results/`.

Новые регрессии проверяют несовпадение image/runtimeVersion/API family/source/tag/hash/component/coverage, неизменяемость снимка и Docker mods, совместимость схемы 1 только для каталога, обязательный формат версии в схеме 2 и согласованность upstream refs CI. Существующие fixtures независимо задают ответы API, поэтому чтение профиля клиентом не превращает проверку version gates в сравнение значения с самим собой.

`bash .github/scripts/test-pull-profile-images.sh` использует искусственные dotnet/docker команды, проверяя успех, сбой с частичным выводом, пустой результат и отсутствие выбора. Реальный Docker не вызывается. Контейнерные пробы всех изменённых клиентов выполняются CI на Ubuntu с исходными закреплёнными образами. Это отдельное доказательство от локальных fixtures.

## Оставшаяся работа

#100 остаётся открытым: HA roles/entity IDs/container names/integration mapping, versioned transforms, общие path/host правила, счётчики и соответствующие отрицательные тесты требуют следующих инкрементов. Не заявляется выполнение всех критериев этой задачи. Клиентские #42–#45, аппаратные #35–#40 и потоковая доставка компонента из #61 остаются отдельными. Новая GUI/DSM-приёмка не выполняется этим протоколом.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
