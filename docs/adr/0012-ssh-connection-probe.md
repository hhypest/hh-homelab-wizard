# ADR 0012: SSH.NET и первый read-only порт подключения

- Дата: 2026-10-18.
- Статус: первый design/fixture инкремент [#46](https://github.com/hhypest/hh-homelab-wizard/issues/46), не полная приёмка SSH/DSM.
- Основа: [архитектура](0001-architecture.md), [приоритет вертикали](0008-audit-development-sequence.md), [операции](0010-operation-boundaries.md).

## Выбор транспорта

Выбрана **SSH.NET 2026.0.0** — стабильная версия с net10.0, MIT, password/public-key authentication, асинхронными SSH/SFTP API. Версия закреплена в central packages; транзитивные зависимости и content hashes — в lock-файлах. Включены лицензии SSH.NET, BouncyCastle.Cryptography 2.7.0 и Microsoft.Extensions.Logging.Abstractions 8.0.3. Изменение существующих pins устанавливаемого hh-homelab не требуется.

Рассмотренные альтернативы: системный OpenSSH потребовал бы отдельного управления наличием CLI, process/stdin и различиями Windows/Linux; отдельная SFTP-библиотека добавила бы второй trust/auth путь. SSH.NET сохраняет транспорт внутри Infrastructure. Отсутствие глобального CLI не мешает работе будущего клиента. Не включаются agent, keyboard-interactive, proxy, forwarding, интерактивный shell и автоматический fallback между типами credentials.

Источники: [закреплённый release](https://github.com/sshnet/SSH.NET/releases/tag/2026.0.0), [NuGet и зависимости](https://www.nuget.org/packages/SSH.NET/2026.0.0), [ConnectAsync](https://github.com/sshnet/SSH.NET/blob/2026.0.0/src/Renci.SshNet/BaseClient.cs), [SFTP](https://github.com/sshnet/SSH.NET/blob/2026.0.0/src/Renci.SshNet/SftpClient.cs). Проверка NuGet advisory выполняется при подготовке PR; её результат описывает текущий источник, не гарантию отсутствия неизвестных уязвимостей.

## Порт и доверие

Domain содержит target, host-key evidence, typed status и capabilities; Application — `ISshConnectionProbe`. Infrastructure создаёт отдельные короткоживущие SSH/SFTP подключения. Desktop этот порт ещё не вызывает. Probe — только чтение; это не установочная `IConfigurationOperation`, поэтому durable mutation intent не создаётся.

Host/port канонизируются в endpoint fingerprint. Host key содержит algorithm и стандартный `SHA256:` отпечаток публичного ключа. Без pin соединение отвергается при HostKeyReceived **до авторизации**, а вызывающему возвращается HostKeyApprovalRequired с evidence. Для продолжения нужен следующий вызов с явно подтверждённым pin того же адреса/порта. Автоматического TOFU/сохранения доверия нет. Несовпадение возвращает HostKeyChanged, без авторизации/автоматической замены. UI доверия и защищённое хранение pin ещё не подключены.

Второе SFTP-подключение проверяет ключ независимо; успешная проверка SSH не подменяет его evidence. Legacy ssh-dss/ssh-rsa host signatures, SHA-1 key exchange/MAC, MD5 MAC и CBC отключены. Совместимость этих ограничений с конкретной DSM требует стендового и аппаратного подтверждения; ослабление алгоритмов при ошибке не выполняется.

## Секреты и ограниченное чтение

`SshProbeCredentials` хранит password либо собственную копию private-key bytes и необязательную passphrase в памяти. Credentials не входят в порт, журнал, аргументы процесса или диагностику. ToString скрывает значения, raw exceptions/ответы не возвращаются. Caller освобождает credentials; byte copy очищается, ссылки на managed strings сбрасываются. Гарантированное стирание всех managed/криптографических внутренних копий не обещается. Для продолжения новой сессии доступы вводятся локально заново.

Разрешены только две фиксированные команды: маркер протокола + `uname -s` + `id -u`, затем `sudo -n -l`. SFTP делает только stat `.`. UI-ввод не попадает в shell; shell/пути/произвольные команды API не принимает. Не выполняются sudo-команды установки, upload, правки конфигурации или скрытая установка prerequisites. Успех sudo listing доказывает только возможность получить список без запроса пароля, не право выполнять произвольную установку. Linux/UID/SFTP не доказывают DSM, Docker, ACL, ownership или готовность стека.

Один конечный budget охватывает подключение, чтения и SFTP; отдельные timeout SSH.NET также конечны. Caller cancellation отличается от deadline. Синхронные участки открытия канала, чтения и cleanup выполняются вне UI-потока. stdout/stderr читаются параллельно с пределом 16 KiB на поток; stderr и sudo output не сохраняются, identity строго разбирается до typed UID. Неверный/слишком большой ответ блокирует Ready. Обычные сетевые буферы библиотеки не являются гарантией жёсткого общего memory cap. Второй вызов того же probe получает Busy.

## Объём доказательств и следующий инкремент

Проверены policy/orchestration fixtures, реальный разбор SSH.NET private keys с/без passphrase и настоящий SSH.NET connect к зависшему loopback TCP socket для timeout/cancellation. Успешная авторизация SSH/SFTP, host-key policy на живом сервере, реальные команды/разрыв канала, SFTP delivery и DSM/sudo аппаратно ещё не доказаны. Они остаются в #46/#53/#101. До отдельного стендового smoke нельзя считать этот PR рабочим установщиком NAS.

Следующий результат #46/#101: одноразовый SSH-сервер со своим ключом и синтетическими аккаунтами, password/key auth и отказ неправильных credentials, key rotation, SFTP, потеря связи/отмена. Затем — read-only preflight #48, безопасные пути #49, конкретный план #50 и минимальные remote ownership/lock/durability механизмы M3. Полные критерии #46 сохраняются открытыми.

Повторение и доказательства: [протокол](../testing/2026-10-18-ssh-probe.md).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
