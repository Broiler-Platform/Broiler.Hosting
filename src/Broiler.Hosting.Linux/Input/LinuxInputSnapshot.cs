namespace Broiler.Hosting.Linux;

/// <summary>
/// Diagnostic snapshot of Linux evdev input coordinator state.
/// </summary>
public readonly record struct LinuxInputSnapshot(
    bool Enabled,
    bool Initialized,
    bool Active,
    double PointerX,
    double PointerY,
    int KeyEvents,
    int TextEvents,
    int MouseMoveEvents,
    int MouseButtonEvents,
    int MouseWheelEvents,
    string? KeyboardDevice,
    string? MouseDevice);
