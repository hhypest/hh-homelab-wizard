# ADR 0007: Windows 11 и Linux X11/Wayland

Статус: принято по прямому указанию владельца 2026-10-11, в том же PR первого инкремента M1.

## Решение владельца

«Отказ от Windows 10. Остаются только Windows 11 и Linux X11/Wayland».

Windows 10 исключена из целевых платформ, матрицы испытаний и критериев M1/релиза. Проект не обещает поддержку или проверку этой ОС. Установка OS-blocker в исполняемый файл не требуется: цель этого решения — объём поддержки, а не искусственное ограничение запуска.

Текущие сборки — `win-x64` для Windows 11 и `linux-x64` для Linux с X11 и Wayland. Базовый Linux для обязательных испытаний сохраняется: Ubuntu 24.04 LTS x64, теперь в обоих сеансах. Поддержка всех дистрибутивов или compositor не считается доказанной испытанием одного стенда. Минимальная сборка Windows 11 и окончательная матрица Linux подтверждаются клиентской приёмкой.

## Linux startup

В Avalonia 12.1.4 `UsePlatformDetect()` на Linux выбирает X11. Запуск такого приложения в Wayland-сеансе через XWayland не доказывает работу нативного Wayland. Поэтому добавлен стабильный пакет **Avalonia.Wayland 12.1.4**, версия закреплена централизованно и в lock-файлах; NWayland 0.11.0 приходит транзитивно. Лицензии включены в уведомления пакета.

Приложение и DesktopProbe используют общий `DesktopPlatform.Configure`:

- Windows — Win32.
- Linux без непустого `WAYLAND_DISPLAY` — X11.
- Linux с `WAYLAND_DISPLAY` — `UseWaylandWithFallback()`: нативный Wayland при доступном compositor, иначе прежний X11 backend. Fallback требует доступного X11/XWayland display; если обоих display нет, запуск не обещается.

Application/Domain не получают знаний о графическом backend. Сценарии установки и MVVM остаются общими. Ресурс `.NET runtime` включается в обе сборки, системные графические библиотеки Linux — отдельно.

## Проверки

Ubuntu CI выполняет три независимых запуска одного опубликованного native probe: X11 под Xvfb, недоступный Wayland с fallback на X11, нативный Wayland под headless Weston без `DISPLAY`. Probe сверяет ожидаемый backend с assembly фактической реализации окна; X11 fallback не засчитывается как нативный Wayland. Проверка закрытия опирается на `Closed` и освобождение `PlatformImpl`, поскольку Wayland не предоставляет публичного Win32/X11-style handle.

Все пробы проходят успех, ошибку, отмену, неизвестный исход и закрытие активной операции. Результат CI читается из отчёта. Weston/Xvfb — автоматические виртуальные стенды. Интерактивная клиентская приёмка **полностью подтверждена владельцем для PR #110 / cac10f0**: Windows 11 26H1 x64 и Ubuntu 24.04.4 LTS x64 / GNOME в X11 и Wayland, см. [протокол](../m1/2026-10-18-owner-check.md). Это проверенная матрица, без утверждения о приёмке всех сборок ОС и compositor. Частные версии вспомогательных компонентов в сообщении владельца не перечислены.

## Источники

- [Avalonia: Desktop Linux / Wayland](https://docs.avaloniaui.net/docs/platform-specific-guides/linux#wayland), проверено 2026-10-11: отдельный opt-in backend и текущие ограничения.
- [UsePlatformDetect 12.1.4](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Desktop/AppBuilderDesktopExtensions.cs), [UseWaylandWithFallback 12.1.4](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Wayland/AvaloniaWaylandPlatformExtensions.cs).
- [Wayland WindowBaseImpl 12.1.4](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Wayland/WindowImplBase.cs): отсутствие публичного native handle.
- [Weston: запуск и тестовые backends](https://wayland.pages.freedesktop.org/weston/toc/running-weston.html).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
