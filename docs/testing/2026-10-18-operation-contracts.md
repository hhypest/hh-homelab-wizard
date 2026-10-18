# Проверка границ операций #99

Дата: 18.10.2026. Baseline: `1a42f4b` (merge PR #108). Решение: [ADR 0010](../adr/0010-operation-boundaries.md). Текущая готовность — [STATUS](../STATUS.md), точный commit и результаты CI — в [#99](https://github.com/hhypest/hh-homelab-wizard/issues/99).

## Реализованный объём

Чистые модели identity/resource/descriptor, typed outcomes, Observe/Apply/Verify, journal/ownership ports, однооперационный сценарий, qB category/Radarr root HTTP bindings и Desktop mapping. У адаптеров нет параллельного долгоживущего intent; session auth остаётся в прежнем клиенте. Реальная реализация journal/ownership отсутствует и не подменяется fallback в памяти.

## Локальная проверка

Среда: Windows x64, SDK 10.0.401/runtime 10.0.12. Используются locked restore, Release build и полный набор команд [README](../../README.md#сборка-и-демонстрация). Отчёты — `artifacts/test-results/operations-*`; итоговые числа и commit фиксируются в PR/#99, не поддерживаются в STATUS.

До полного прогона целевые проверки подтвердили:

- intent предшествует Apply, а confirm следует независимому Verify; готовое состояние не вызывает POST;
- pending/confirmed journal entry, новый экземпляр адаптера и сценария не разрешают слепой повтор;
- после потери ответа как при фактической записи, так и без записи следующий проход только наблюдает;
- Rejected/Unknown/Conflict/Transport/Invalid различаются; неоднозначные 400/401/403/409/500 после POST остаются Unknown;
- отмена до intent, во время записи intent и после отправки различаются; незавершённый intent сохраняется;
- чужое/недоказанное владение, его изменение во время journal write, занятая lease и повторный вход блокируют Apply;
- несовместимые identity/параметры/profile/API endpoint, неверная версия/auth/path/service и изменённая baseline не принимаются;
- root ID, изменённый между Observe и Verify при том же пути, не считается прежней записью;
- недоступный journal/confirm, неуспешный Verify и raw transport errors не становятся успешным результатом и не раскрывают тексты исключений;
- Desktop выбирает действие по typed result; для любого code при Unknown предлагает Observe или восстановление журнала, без предложения повторить запись.

`TestOperationJournal` и ownership fixture искусственные, расположены только в UnitTests. Повторный объект сценария в тесте не является перезапуском приложения с дисковым журналом. HTTP bindings проверены синтетическим handler без сети. Прежние version/auth/conflict и runtime-пробы клиентов сохраняются и выполняются CI; они не объявляются runtime-проверкой нового сценария с durable journal.

## Непроверенные уровни

Не выполняются реальные NAS/SSH/Compose операции, межпроцессные filesystem locks, durable resume, пользовательские экраны нового сценария и аппаратная приёмка DSM. Эта работа относится к #46–#55/#101/#103. Компенсация удаления ресурсов не добавлена. #100 остаётся открытым для ролей/transforms/paths; M0/M1/M3 не закрываются от этого результата.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
