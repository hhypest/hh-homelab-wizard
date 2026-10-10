# M0: исследование профиля и первые контракты

**Статус: M0 начат, не закрыт.** Исследован закреплённый источник, созданы C#-каталог и developer tools, выполнены unit-проверки и часть локальных проб API. Автоматический установщик и Avalonia UI ещё не реализованы.

## Артефакты

- [Покрытие инструкции](COVERAGE.md): все 55 Media + 63 HA шага, режим и будущая операция. Все операции установки имеют `NotImplemented`.
- [Research profile](../../profiles/hh-homelab/1.3.0/research-profile.json): 9 контейнеров, VueTorrent mod **2.36.1**, Monitor Docker с реальным значением manifest, 19 fingerprints исходных файлов.
- [Архитектура](../adr/0001-architecture.md), [журнал и секреты](../adr/0002-journal-and-secrets.md), [компоненты и функции](../adr/0003-components-and-features.md).
- [Протокол локальных проб](PROBE_REPORT.md): подтверждённые операции, неудачные пробы и ограничения.

## Проверка на машине разработчика

Нужен SDK **10.0.401**, закреплённый в global.json. Из корня репозитория:

```text
dotnet restore HhHomelab.Wizard.slnx --locked-mode
dotnet build HhHomelab.Wizard.slnx --no-restore --configuration Release
dotnet test HhHomelab.Wizard.slnx --no-build --configuration Release
dotnet run --project tools/HhHomelab.Wizard.ProfileCheck --no-build --configuration Release -- profiles/hh-homelab/1.3.0/research-profile.json
```

Для сравнения с имеющимся checkout hh-homelab v1.3.0 добавьте его путь вторым аргументом ProfileCheck. Инструмент проверяет содержимое перечисленных файлов, не весь репозиторий или runtime-совместимость. Путь checkout должен быть обычным каталогом без ссылок в проверяемом пути.

QbProbe — дополнительная диагностическая программа для API **5.2.4**. После сборки запускается с единственным аргументом base URL с завершающим `/`; две строки stdin — username и password. Секреты нельзя помещать в аргументы команд, историю shell, Git или перенаправлять в сохраняемый файл. Программа выполняет только login и чтение версии, не настраивает сервис. Наличие этого инструмента не означает, что bootstrap qBittorrent уже работает.

Проверки CI запускаются на Windows runner и Ubuntu 24.04. Это проверка C#-ядра; она не заменяет испытания GUI на Windows 10/11, X11 или работу DSM.

## Что осталось до закрытия M0

1. Довести bootstrap qBittorrent до воспроизводимого успешного входа с cookies на чистом контейнере и проверить смену credentials, preferences и категории. Неудачный результат текущей пробы не скрывать.
2. Проверить изменение аутентификации Arr, root folders, download clients и Prowlarr applications через API на закреплённых версиях, включая повтор после потери ответа. Пока проверены status/schema.
3. Проверить библиотеки/пользователей Jellyfin и связи Seerr с обоими Arr. Первичная авторизация Jellyfin/Seerr проверена отдельно.
4. Проверить WS HTTP configuration, panel save/load, обновление реестра HA, доставку Monitor Docker и исключение зависимостей облачных модулей. Наличие методов в source не считается runtime-проверкой.
5. Определить минимальные сборки Windows 10/11 по совместимости выбранного SDK/Avalonia и реальным клиентским испытаниям.
6. На согласованном NAS подтвердить DSM build, архитектуру, Docker/Compose, права и ACL; затем проверить flows DSM/LG/GX1. Доступа к NAS и аппаратных испытаний в этой работе не было.

Не нужно передавать реальные passwords/API keys в issue или PR. Инженерные пункты 1–5 выполняются на disposable стенде; аппаратура нужна для пункта 6. Мутации настоящего NAS относятся к отдельному явно авторизованному сценарию.

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
