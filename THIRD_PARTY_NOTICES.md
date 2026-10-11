# Сторонние компоненты пробной сборки M1

Лицензия MIT самого мастера не меняет лицензии зависимостей. Полные тексты и уведомления скопированы из закреплённых пакетов/исходных репозиториев в `LICENSES/third-party` и включаются в CI-артефакт вместе с этим файлом.

| Компонент | Версия / источник | Тексты |
| --- | --- | --- |
| Avalonia и Fluent theme | 12.1.4, AvaloniaUI/Avalonia tag 12.1.4 | Avalonia-LICENSE.md, Avalonia-NOTICE.md |
| CommunityToolkit.Mvvm | 8.4.2, NuGet | Toolkit-LICENSE.md, Toolkit-NOTICES.txt |
| .NET runtime и Microsoft.Extensions.DependencyInjection | 10.0.12, dotnet/runtime tag v10.0.12 | dotnet-LICENSE.txt, dotnet-NOTICES.txt |
| MicroCom.Runtime | 0.11.6, commit 76785efcafd91b5902fd19dd11145f6dd655b7b4 | MicroCom-LICENSE.txt |
| Tmds.DBus.Protocol | 0.94.1, commit b4a7fed0b878f74cb54f7cca84d2889af4e596ba | Tmds-DBus-LICENSE.txt |
| SkiaSharp и native assets | 3.119.4, NuGet | SkiaSharp-LICENSE.txt, SkiaSharp-NOTICES.txt |
| HarfBuzzSharp и native assets | 8.3.1.3, NuGet | HarfBuzzSharp-LICENSE.txt, HarfBuzzSharp-NOTICES.txt |
| ANGLE Windows native assets | 2.1.27548.20260419, NuGet | ANGLE-LICENSE.txt |

Точные транзитивные версии и хэши находятся в `src/HhHomelab.Wizard.Desktop/packages.lock.json`. BuildServices и тестовые зависимости используются при разработке, не являются частью пользовательского сценария. При изменении пакетов нужно сверять и обновлять эти уведомления. Материалы сторонних авторов сохраняют свои условия использования.

Авторский текст этого документа: CC BY 4.0, © 2026 hhypest; импортированные лицензии сохраняют собственные условия.
