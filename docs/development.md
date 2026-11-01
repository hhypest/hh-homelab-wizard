# Разработка, запуск и сборка

Тип: действующая справка для разработчика; команды и среда текущей реализации.

[Документация](README.md) · [Правила изменений](../CONTRIBUTING.md) · [Архитектура](engineering/architecture.md) · [Проверки](engineering/testing.md)

## Среда

Нужен .NET SDK **10.0.401**, закреплённый в `global.json` (`rollForward: disable`). Central package versions — в `Directory.Packages.props`, lock-файлы — в каждом проекте. Locked restore обязателен; lock-файлы меняются только при намеренном обновлении зависимостей. Docker с Linux Engine нужен только для [инструментов разработчика](engineering/developer-probes.md), не для сборки и unit-тестов.

## Сборка, тесты и запуск

Из корня репозитория:

```text
dotnet restore HhHomelab.Wizard.slnx --locked-mode
dotnet build HhHomelab.Wizard.slnx --no-restore --configuration Release
dotnet test HhHomelab.Wizard.slnx --no-build --configuration Release
dotnet run --project tools/HhHomelab.Wizard.DocsCheck --no-build --configuration Release
dotnet run --project tools/HhHomelab.Wizard.ProfileCheck --no-build --configuration Release -- profiles/hh-homelab/1.3.0/research-profile.json
dotnet run --project src/HhHomelab.Wizard.Desktop --no-build --configuration Release
```

DocsCheck проверяет ссылки, якоря, каталог и объявленные типы документации ([правила](catalog.md#как-поддерживать-документацию)). ProfileCheck проверяет каталог исследовательского профиля. Для сравнения с имеющимся checkout hh-homelab v1.3.0 добавьте его путь вторым аргументом ProfileCheck. Инструмент проверяет содержимое перечисленных файлов, не весь репозиторий или runtime-совместимость. Путь checkout должен быть обычным каталогом без ссылок в проверяемом пути.

Демонстрация «Фокус» не подключается к NAS; что именно можно проверить — в [чек-листе демонстрации](verification/checklists/client-demo.md). DesktopProbe открывает настоящее окно и описан среди [инструментов разработчика](engineering/developer-probes.md#desktopprobe).

## Пробные пакеты

CI публикует папки `hh-homelab-wizard-m1-demo-win-x64` и `hh-homelab-wizard-m1-demo-linux-x64` как артефакты проверки PR на 14 дней, без объявления релиза. Скачайте соответствующий архив из GitHub Actions и распакуйте **всю папку**. Windows 11: запустите `HhHomelab.Wizard.Desktop.exe`. Ubuntu/X11/Wayland: сделайте `chmod +x HhHomelab.Wizard.Desktop`, затем `./HhHomelab.Wizard.Desktop`. .NET, Python и Node.js пользователю не нужны. Native probe и тестовые зависимости в пользовательский артефакт не включаются.

Команды самостоятельной публикации:

```text
dotnet publish src/HhHomelab.Wizard.Desktop -c Release -r win-x64 --self-contained true --no-restore -o artifacts/desktop-win
dotnet publish src/HhHomelab.Wizard.Desktop -c Release -r linux-x64 --self-contained true --no-restore -o artifacts/desktop-linux
```

После локальной публикации для передачи пакета включите `LICENSE`, `LICENSES`, `THIRD_PARTY_NOTICES.md` и [инструкцию пакета](guide/demo-package.md) под именем `DEMO.md`. CI копирует их автоматически. Инструкция пакета не зависит от относительных ссылок исходной документации.

## Зависимости Ubuntu

Ubuntu 24.04 x64 требует X11 либо Wayland-сеанс и системные библиотеки. Общие зависимости и X11:

```sh
sudo apt-get install libx11-6 libice6 libsm6 libfontconfig1 libgl1 fonts-dejavu-core libicu74 libssl3t64 zlib1g
```

Нативный Wayland дополнительно:

```sh
sudo apt-get install libwayland-client0 libwayland-egl1 libxkbcommon0 libegl1 libegl-mesa0 libgles2 libgl1-mesa-dri
```

При заданном `WAYLAND_DISPLAY` общий startup приложения и probe использует Wayland с fallback на X11. В X11-сеансе выбирается X11. Если оба display недоступны, графическое приложение не запускается. Xvfb и Weston нужны только автоматическим пробам CI. Другие дистрибутивы пока не проверены. Системные зависимости и ограничения backend заданы по [Avalonia Linux](https://docs.avaloniaui.net/docs/platform-specific-guides/linux) и [.NET на Ubuntu 24.04](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install#dependencies).

Документ: [CC BY 4.0](../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
