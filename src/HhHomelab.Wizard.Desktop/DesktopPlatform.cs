using Avalonia;

namespace HhHomelab.Wizard.Desktop;

/// <summary>Shared startup policy for the application and its native developer probe.</summary>
public static class DesktopPlatform
{
    public static AppBuilder Configure(AppBuilder builder)
    {
        builder.UsePlatformDetect();
        // WAYLAND_DISPLAY is set by the compositor in a native Wayland session.
        // Windows and pure X11 sessions retain their platform-detected backend.
        if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            builder.UseWaylandWithFallback();
        return builder;
    }
}
