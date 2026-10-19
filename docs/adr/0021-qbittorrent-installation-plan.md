# ADR 0021: конкретный read-only план qBittorrent

- Дата: 2026-10-19.
- Статус: частичный разработческий инкремент [#50](https://github.com/hhypest/hh-homelab-wizard/issues/50), родитель #20, зависимости #47/#49; подготовка вертикали #101 по [ADR 0008](0008-audit-development-sequence.md).
- Основа: [ресурсы preflight](0015-preflight-host-resources.md), [пути](0016-remote-path-inspection.md), [ownership](0017-installation-ownership-marker.md), [исполняемый граф](0020-configuration-plan-recovery.md).

## Решение

`QbittorrentInstallationRequest` содержит явные installation ID, SSH target, корни данных/конфигурации, IPv4 NAS, BIND_ADDR, host-порты WebUI/torrent, имя сети, PUID/PGID, UMASK, TZ и два бюджета свободного места. Значения другого NAS не подставляются. Лексическая валидация не доказывает доступность адреса, существование пользователя или корректность TZ. Для этого первого preview поддержаны канонический IPv4 и bind на все адреса, loopback либо указанный NAS; DNS/IPv6 остаются последующим расширением.

Версионированный `profiles/hh-homelab/1.3.0/qbittorrent-plan.json` закрепляет геометрию файлов, каталогов, контейнера и политику согласования WebUI ports. Он ссылается на commit `b05047b081f3543520582315002e359ef1d93e59` и SHA-256 `media/compose.yaml` из существующего research profile. Адаптер проверяет schema/provenance и берёт image/mod pins из `PinnedRuntimeProfile`, без второго списка версий. Источник прочитан через GitHub по точному commit; личные значения `.env.example` не импортируются. Шаблон не рендерит Compose и не меняет upstream-профиль.

`QbittorrentPlanCompiler` использует существующие read-only порты `INasPreflightProbe` и `INasPathProbe`: заново собирает inventory, затем пути. Снимки должны совпадать по endpoint, одобренному host key, research fingerprint, SSH principal и выбранному layout. Чтение путей не начинается при несовместимой host identity. Максимальный возраст — две минуты; path observation не раньше завершения host observation и не в будущем. Ошибка/отмена не заменяются старым планом. Одновременный запуск одного compiler отклоняется.

`QbittorrentInstallationPlan` неизменяем и перечисляет:

- каталоги данных с `PreserveOrCreate`, без присвоения существующей медиатеки;
- каталоги конфигурации, Compose, `.env`, marker, контейнер и сеть с ожидаемым `AbsentOrOwned` и конкретным installation owner;
- volume mappings `/data`, `/config`, `/watch`, TCP WebUI и TCP/UDP torrent;
- отдельные адреса компьютер → NAS, HA host network → публикация хоста и Compose → `qbittorrent:<выбранный порт>`.

Выбранный WebUI port одновременно задаёт внутренний `WEBUI_PORT`, host/container publication и Compose-адрес. Защищённый WebUI qBittorrent 5.2.4 отвергает несовпадающий Host port — это уже проверено [M0 runtime-пробой](../m0/README.md#повторяемая-проба-qbittorrent); Host validation не отключается. Политика `host-and-container` закреплена в шаблоне, другой режим отклоняется. Для BIND_ADDR=конкретный NAS адрес HA также использует NAS, а не неработающий loopback. При bind на `127.0.0.1` адрес с компьютера отсутствует и остаётся блокировка `plan.computer-access.proxy-required`; несуществующий туннель не предполагается.

Свободное место оценивается относительно явных бюджетов. На одном томе бюджеты данных и конфигурации складываются, на разных оцениваются отдельно. Это не бюджет Docker image storage: он явно `NotChecked`. TCP listeners проверяются существующей оценкой; UDP не выдаётся за проверенный TCP.

## Свежая перепроверка

`RevalidateAsync` заново собирает оба снимка, возвращает свежий план и сравнивает отпечатки. Отпечаток включает запрос, весь шаблон, стабильные environment fingerprints и результаты проверок. Изменение identity, путей, портов, ownership или переход свободного места через бюджет инвалидирует прежний результат. Новые timestamps и колебание свободного места выше бюджета не создают ложный drift. Смена шаблона меняет fingerprint даже при прежнем research profile.

Совпадение отпечатков означает только неизменность проверенных фактов. У модели нет `CanApply`: даже план без Blocking сохраняет Unverified/NotChecked, включая DSM ACL/PUID, совместимость версий, UDP, дочерние конфигурационные пути/базы, имя сети, достижимость адресов, TZ и Docker storage. Preflight container inventory пока консервативно блокирует непустой Docker; точное распознавание собственного контейнера остаётся отдельной задачей.

## Границы

Это первый конкретный preview одного сервиса, ещё не полный v1 compiler и не `ConfigurationOperationPlan`. Он не записывается в исполняемый журнал, не выдаёт разрешение установки и не вызывает мутации. Доставка/рендер файлов #53, связь с графом #52, secrets #47, настоящие VM/экран #103/#50, полный стек и аппаратные проверки остаются открытыми. Публичного пользовательского пути запуска compiler пока нет; демонстрационный UI прежний. Полные #50/#101/M2 не закрываются.

Доказательства: [протокол](../testing/2026-10-19-qbittorrent-plan.md).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
