# Первый инкремент HTTP-транспорта: B1/B2

Дата: 2026-10-18. Задача: [#97](https://github.com/hhypest/hh-homelab-wizard/issues/97). Основание — [ADR 0008](../adr/0008-audit-development-sequence.md). Baseline: `3248dc2d4e731084b461f0abd18e4ddc26dd518b`, после слияния PR #105 с правилами читаемости. Это отдельное исправление поведения; объединение транспорта и модель установочных операций не объявляются завершёнными.

## Ошибка и изменение

`HttpClient.SendAsync` в .NET может завершаться `OperationCanceledException` как при caller cancellation, так и при истечении `HttpClient.Timeout`; [контракт Microsoft](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.sendasync?view=net-10.0). До исправления Media/Arr/qBittorrent выпускали deadline как обычную отмену. Некоторые чтения и login qBittorrent также выпускали исходный `HttpRequestException`, который может содержать адрес и чувствительные данные.

Обновлены клиенты qBittorrent, Arr и общий обмен Jellyfin/Seerr, включая form login Arr. Существующие статусные коды и правила cookies/auth сохранены. Сбой HTTP/IO и deadline чтения получают безопасное исключение с `Kind = Transport`. Отмена caller во время чтения сохраняет `OperationCanceledException` с caller token без исходного сетевого сообщения. Уже отменённый токен проверяется до входа в отправку запроса.

После входа в отправку POST/PUT отсутствие ответа, сбой чтения тела, deadline и caller cancellation дают `Kind = Unknown`. Запись не повторяется автоматически. Отмена в гонке между проверкой токена и отправкой также может быть Unknown: клиент не доказывает отсутствие передачи по сети. Коды `*.result-unknown` сохранены для совместимости; новая классификация доступна через enum, без разбора строки.

`ContractFailureKind` классифицирует отдельный HTTP-обмен Infrastructure. Он не является моделью исходов целой установки из #99. `Contract` для остальных существующих ошибок не означает доказанный отказ сервера до мутации и не разрешает повтор. Новый общий транспорт, явные политики дедлайнов, общий контракт исключений и дальнейшая интеграция HA остаются в #97; 4xx/recovery и журнал требуют отдельных доказательств.

## Регрессия и проверки

Новые `HttpTransportRegressionTests` сначала запускались против baseline до изменения клиентов. Deadline-сценарии воспроизводили выход `OperationCanceledException`, а qBittorrent preflight/login/version/preferences/categories — выход исходного сетевого исключения. Первоначальный диагностический набор затем расширен проверками тела ответа, typed Kind и form login Arr; его ранний TRX не является отчётом окончательного набора.

Проверки используют HttpClient с настоящим конечным Timeout поверх искусственного обработчика. Отдельно моделируются ожидание ответа, отменяемая загрузка тела и IO-обрыв при загрузке тела; внешний сервер не нужен. Токен caller отменяется после сигнала о входе в обработчик. Запись учитывается до потери ответа, отсутствие автоматического повторения проверяется счётчиком запросов. Существующие stateful fixtures Arr/Jellyfin/Seerr дополнительно подтверждают наблюдение уже применённого изменения и отсутствие второй записи.

Проверены:

- qBittorrent, Radarr, Sonarr, Prowlarr, Jellyfin и Seerr: deadline/обрыв при чтении;
- qBittorrent preferences/category, Arr root и form login, Jellyfin/Seerr login: неизвестный исход после отправки;
- caller cancellation до отправки и во время чтения/записи;
- preflight, login, version proof, preferences и categories qBittorrent при сетевых отказах;
- отсутствие URL, исходного body/credentials и inner exception в новых безопасных ошибках;
- наблюдение состояния после отменённой записи в существующих fixtures без дублирования изменений.

Локальная среда: Windows x64, SDK 10.0.401 и runtime 10.0.12, установленные отдельно от системного SDK. Использованы locked dependencies без изменения pins/lock-файлов. Locked restore, Release build и полный набор unit/fixture/headless тестов завершились успешно; сборка без предупреждений. Команды из корня:

```text
dotnet restore HhHomelab.Wizard.slnx --locked-mode
dotnet build HhHomelab.Wizard.slnx --no-restore --configuration Release
dotnet test HhHomelab.Wizard.slnx --no-build --configuration Release --logger trx --results-directory artifacts/test-results
dotnet run --project tools/HhHomelab.Wizard.ProfileCheck --no-build --configuration Release -- profiles/hh-homelab/1.3.0/research-profile.json
```

Результаты локальных запусков сохраняются в игнорируемом `artifacts/`: диагностический `transport-before`, итоговый `transport-final`, журналы сборки и тестов. Актуальные числа берутся из TRX, не поддерживаются вручную в STATUS. CI запускает Windows/Linux core, Desktop и runtime-пробы на закреплённых версиях; конкретный run и его результат фиксируются в PR и Issue #97.

GUI-приёмка обычного клиента и реальная DSM не выполнялись. Fixture и Docker не доказывают работоспособность NAS/устройств. Этот инкремент не закрывает всю #97, M0 или M1 и не добавляет установочный движок.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
