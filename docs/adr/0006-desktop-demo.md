# ADR 0006: первый каркас M1 и закреплённые UI-пакеты

Статус: принято для демонстрационного инкремента, 2026-10-11. Новое задание владельца «Продолжи разработку, согласно намеченного плана» завершает паузу после выбора «Фокуса» в ADR 0005.

## Решение

Добавлены работающие Application и Desktop: сценарий зависит от порта искусственного адаптера, Infrastructure реализует его, ViewModel получает только интерфейс сценария. DI связывает конкретные реализации в App. Views используют compiled bindings; code-behind отвечает только за закрытие окна. Application/Domain не зависят от Avalonia.

| Пакет | Версия | Назначение |
| --- | --- | --- |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.1.4 | Окна Win32/X11, ресурсы тем, XAML |
| Avalonia.Wayland | 12.1.4 | Нативный Wayland; добавлен уточнением платформ в ADR 0007 |
| CommunityToolkit.Mvvm | 8.4.2 | ObservableObject, синхронные и асинхронные команды |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | Проверяемый composition root и lifetime singleton-сценария |
| Avalonia.Headless | 12.1.4 | UI-тесты представления и ввода; только тестовый проект |

Используются стабильные версии из NuGet, центральное управление и lock-файлы. .NET SDK 10.0.401 / net10.0 сохраняется из M0. Self-contained публикация включает runtime, без trimming и single-file: нативные библиотеки остаются отдельными файлами. Сторонние уведомления перечислены в [THIRD_PARTY_NOTICES](../../THIRD_PARTY_NOTICES.md).

## Граница демонстрации

Четыре экрана: пример параметров → план → выполнение → результат. Шесть групп операций искусственные; таймер не является проверкой реального сервиса. Варианты: успех, ошибка Media, потеря ответа при сопряжении. Ожидание требует отдельного подтверждения пользователя. Ошибка и неизвестный результат прекращают текущую сессию без повторной отправки. Новая демонстрация начинает новый искусственный сценарий, не восстанавливает прежнюю установку.

Сессия защищена от повторного запуска и на уровне Application, и внутри команды. Отмена останавливает дальнейшие действия; закрытие отменяет и ожидает работающую сессию. Observable-состояние доставляется в UI через awaitable dispatcher. Тексты исключений адаптера скрыты. Нет credentials, SSH, HTTP, файлов NAS, installation plan, durable journal, отката или восстановления после перезапуска. Эти возможности относятся к M2/M3 и далее.

## Совместимость и приёмка

Пробы и ограничения приведены в [M1 README](../m1/README.md). Headless тесты не являются проверкой графической системы ОС. Native probe открывает реальное окно и проходит сценарии, но программные команды в нём не заменяют проверку мыши, клавиатуры, экранного диктора и удобства пользователем.

Первоначальное требование Windows 10 отменено владельцем до слияния этого PR. Текущие платформы — Windows 11 x64 и Linux x64 с X11/Wayland, базовый дистрибутив Ubuntu 24.04 LTS; [ADR 0007](0007-client-platforms.md). Минимальная сборка Windows 11 и окончательная клиентская приёмка остаются открытыми до испытаний. Linux требует системных библиотек даже в self-contained пакете; X11 и нативный Wayland проверяются отдельно.

## Источники

- [Платформы Avalonia](https://docs.avaloniaui.net/docs/supported-platforms), [платформы .NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), проверено 2026-10-11.
- [Avalonia 12.1.4](https://www.nuget.org/packages/Avalonia/12.1.4), [Toolkit 8.4.2](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.2), [DI 10.0.12](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection/10.0.12).
- [AsyncRelayCommand](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/asyncrelaycommand), [HeadlessUnitTestSession 12.1.4](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Headless/Avalonia.Headless/HeadlessUnitTestSession.cs).

Документ: [CC BY 4.0](../../LICENSES/CC-BY-4.0.txt), © 2026 hhypest.
