# ADR 0010: операции Observe / Apply / Verify

- Дата: 2026-10-18.
- Статус: принято для #99 по [ADR 0008](0008-audit-development-sequence.md).
- Основа: [транспорт](../m0/SHARED_HTTP_REPORT.md), [снимок профиля](0009-pinned-runtime-profile.md), [журнал и секреты](0002-journal-and-secrets.md).

## Модели и границы

Domain содержит `ResourceIdentity`, `OperationIdentity`, `OperationDescriptor`, типизированные observation/verification, `OperationOutcomeKind` и `OperationErrorCode`. Identity включает installation/resource GUID, service, отпечатки профиля и канонического адреса API. Descriptor связывает operation GUID с видом операции и отпечатком желаемых параметров. Изменение ресурса, профиля, адреса или параметров несовместимо с прежним intent. Это внутренние модели, а не уже принятая схема сериализации журнала.

Отпечаток адреса сравнивается с фактическим BaseAddress клиента до HTTP. Он защищает от подключения адаптера к другому адресу при корректной identity NAS. Сам по себе hash адреса, успешный API login или имя категории не доказывают владение сервером. `IOperationOwnership` обязан свежим чтением проверить marker, instance и profile в контексте фактической цели. Реализация SSH/NAS отсутствует; в тестах используется явно искусственный ответ.

Application определяет `IConfigurationOperation`, `IOperationJournal`, `IOperationOwnership` и однооперационный `ConfigurationOperationScenario`. Domain/Application не зависят от Infrastructure, HTTP, Avalonia или русских UI-текстов. HTTP adapters реализуют отдельные Observe, Apply и Verify. Desktop переводит безопасные code/kind в сообщение и действие через `OperationResultPresenter`; привязка к экранным VM остаётся #103.

## Исходы

| Kind | Семантика |
| --- | --- |
| Applied | После intent независимое чтение подтвердило желаемое состояние, затем journal confirm завершился; это включает reconciliation потерянного ответа |
| AlreadySatisfied | Уже выполненное состояние независимо проверено без нового intent/Apply; ранее подтверждённый journal entry тоже проверяется заново |
| Rejected | Адаптер доказал отказ без изменения; одного HTTP 4xx недостаточно |
| Unknown | Возможный intent/изменение или подтверждение не разрешены доказательствами; следующий запуск с тем же descriptor только наблюдает |
| Conflict | Чужой ресурс, несовместимая identity/конфигурация, изменённая baseline или занятой исполнитель; Apply не повторяется |
| Transport | Не удалось получить сведения до отправки изменения; это не разрешение повторить Apply без нового прохождения журнала и наблюдения |
| Invalid | Неподдерживаемый запрос/версия/ответ или отсутствующий доступ; требуется исправление входных условий |
| Cancelled | Доказанная остановка до отправки; отмена после записи intent в сценарии консервативно даёт Unknown |

`OperationResult` не принимает success с error code или failure с None. `OperationRead<T>` отделяет evidence от failure; `OperationWriteAttempt.Acknowledged` не является Applied. Обработчики не выводят raw exception text или HTTP body. Маппинг ошибок чтения использует точные коды/Kind, без определения семантики по суффиксу строки. Неизвестный исход имеет приоритет в UI и не получает сообщение о доказанном отказе или откате.

## Последовательность

1. Проверить descriptor и получить исключительную journal lease. Блокировка экземпляра сценария дополняет её, не заменяет межпроцессные locks.
2. Прочитать существующий intent/confirm, проверить identity/параметры, затем свежие ownership и Observe.
3. При существующем intent никогда не вызывать Apply. Satisfied → независимый Verify → confirm при незавершённом intent. Missing/неполное доказательство → Unknown; конфликт не исправляется автоматически.
4. Для новой Missing baseline заново проверить ownership, атомарно записать intent, снова проверить ownership и вызвать Apply не более одного раза.
5. Адаптер Apply повторяет чтение baseline/version/auth перед POST. Изменившаяся baseline блокирует запись. Это не remote CAS: API не обеспечивает общей транзакции с ручными действиями других клиентов.
6. После acknowledgement проверить ownership, выполнить независимый Verify, ещё раз проверить ownership и атомарно confirm. Исключение/отмена после intent оставляет Unknown. Lease освобождается при любом исходе.

`IOperationJournal` требует атомарность, привязку к descriptor и исключительную lease, но production implementation, schema/hash envelope, filesystem locks и восстановление между процессами реализуются в #51/#52. У сценария нет fallback journal. Тестовая реализация находится только в тестах. Предыдущий незавершённый intent нельзя очищать для повтора через этот API; решение recovery требует отдельного проверенного сценария #52.

## Образцы и ограничения

Образцы: создание категории qBittorrent и root folder Radarr. Они используют прежние клиенты, версии из профиля, API auth и conflict checks. qBittorrent требует штатную сессию; Arr — API key. Root observation сохраняет ID корневой записи, поэтому замена записи с тем же путём видна при Verify. Неоднозначный статус, обрыв и отмена после отправки дают Unknown. Rejected проверен на искусственном порте; sample POST-контракты его не объявляют.

Сценарий не удаляет категории, папки или медиа. Автоматической компенсации нет; безопасный откат этих созданий не доказан. Неподтверждённое изменение требует наблюдения и решения пользователя/будущего recovery. Полный граф, SSH/Compose, durable resume, остальные конфигурации Media/HA и интеграция экранов не реализованы этим ADR. Клиентские и аппаратные условия M0/M1/M3 сохраняются.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
