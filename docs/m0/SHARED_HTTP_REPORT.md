# Общий HTTP-транспорт и конечные дедлайны

Дата: 2026-10-18. Задача: [#97](https://github.com/hhypest/hh-homelab-wizard/issues/97), A4/B1/B2. Baseline — merge PR #106, `f289c84`: [первый инкремент](TRANSPORT_REPORT.md) уже нормализовал ошибки; этот инкремент завершает перенос общей механики обмена. Порядок разработки определён [ADR 0008](../adr/0008-audit-development-sequence.md).

## Реализация

`ContractHttpTransport` выполняет ровно один SendAsync, буферизацию ответа и callback адаптера под связанным deadline token. Request принадлежит адаптеру, response освобождается транспортом и при успехе, и при исключении callback. HttpClient, cookies, headers, TLS и redirects остаются у вызывающего кода; транспорт их не меняет и не создаёт повторные запросы. Проверки HTTP status, pinned version, идентичности и ownership остаются в адаптерах.

`ContractException` — общий безопасный базовый тип с Code/StatusCode/Kind. `QbittorrentContractException`, `ArrContractException` и `MediaContractException` сохранены как специализированные типы с прежними кодами. `Contract` не означает Rejected и не разрешает повтор; модель исходов установки и ErrorCode/Kind Domain остаются в #99. WebSocket HA сохраняет отдельный протокол и собственные deadlines; переустройство HA и разделение обязанностей остаются в #102.

Общий HTTP exchange используется qBittorrent (preflight/login/version/preferences/categories), Arr API и form login, Jellyfin, Seerr, HA onboarding/auth/token/обычными HTTP-запросами и отзывом refresh token. Загрузка пакета Monitor Docker остаётся отдельным потоковым путём доставки с ограничениями размера и caller token; конечные бюджеты API-обмена на неё не распространяются.

## Политика ожидания

| Контекст | GET | Остальные HTTP-методы |
| --- | --- | --- |
| qBittorrent / Arr / Jellyfin / Seerr | 30 секунд | 60 секунд |
| HA HTTP по умолчанию | 30 секунд | 30 секунд |
| Отзыв HA refresh token | 10 секунд | 10 секунд |

Публичный `ContractHttpTimeouts` задаёт конечные положительные бюджеты чтения/записи до `Int32.MaxValue` миллисекунд. Адаптеры принимают его необязательным параметром. Отзыв HA использует отдельный фиксированный cleanup budget, сохраняя прежнее ограничение. Меньший HttpClient.Timeout ограничивает весь exchange, включая callback после получения ответа; бесконечный HttpClient.Timeout не отменяет конечный бюджет. Исходный HttpClient.Timeout не меняется.

Сохранён `ResponseContentRead`; [документация Microsoft](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0) различает его охват и ResponseHeadersRead. Связанный токен дополнительно передаётся потребителю ответа, поэтому deadline действует и при чтении/разборе тела в callback. Отмена кооперативная, через CancellationToken.

До входа в SendAsync отменённый caller token предотвращает запрос. HTTP/IO/deadline чтения → Transport; отсутствие подтверждения записи → Unknown. Caller cancellation чтения сохраняет безопасный OperationCanceledException; Media-записи после начала отправки сохраняют Unknown. HA явно сохраняет прежний caller-cancellation контракт сессионного адаптера: OperationCanceledException не означает откат, а pending/unknown intent и cleanup принадлежат HA-клиенту. Истечение внутреннего deadline HA-записи даёт Unknown. Эта совместимость покрыта отдельно; общий транспорт не присваивает себе сессионное состояние.

## Проверки и границы

`HttpTransportRegressionTests` первого инкремента и весь существующий набор прошли после переноса. Новые `ContractHttpTransportTests` проверяют конечность бюджетов при бесконечном HttpClient.Timeout во всех адаптерах, отдельный выбор read/write budget, ожидание headers/body, меньший client timeout во время callback, invalid JSON и освобождение response, сохранение status/code/headers и явное различие HA cancellation. Fixtures используют реальные таймеры HttpClient/CTS, искусственный HTTP handler и приветствие WebSocket без внешних credentials.

Locked restore, Release build и unit/fixture/headless выполняются локально на SDK 10.0.401/runtime 10.0.12, Windows x64. Итоговые отчёты — `artifacts/test-results/shared-final`, журналы `artifacts/shared-build.log` и `artifacts/shared-final.log`; числа не копируются в STATUS. Windows/Linux и все runtime-пробы закреплённых версий выполняются CI PR; окончательные commit/run/результаты фиксируются в Issue #97 и PR перед приёмкой.

Реальная DSM не использовалась. Первичная проверка интерфейса владельцем относится к другой точно указанной сборке, [протокол M1](../m1/2026-10-18-owner-check.md), и не выдаётся за клиентскую приёмку нового транспорта. Завершение #97 не закрывает M0/M1 и не добавляет журнал или установочный движок. Следующая независимая работа — модели/порты #99 и согласованность профиля #100, затем вертикаль #101.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
