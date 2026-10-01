using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Broiler.Graphics.Linux;
using Broiler.Graphics.Linux.OpenGL;

namespace Broiler.Hosting.Linux;

/// <summary>
/// Preflight library and environment checks for Linux X11/EGL graphics backends.
/// Does not initialize a display connection or EGL context.
/// </summary>
public static class LinuxBackendDiagnostics
{
    public static bool Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("X11/EGL diagnostics require Linux.");

        output.WriteLine($"Backend: {nameof(LinuxOpenGlRenderer)} (X11/EGL)");
        output.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        bool supportedArchitecture = RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;
        if (!supportedArchitecture)
            output.WriteLine("Only x64 and ARM64 are planned targets.");

        var libraries = LinuxNativeLibraryProbe.Check([
            LinuxGraphicsDependencies.Egl with { LibraryNames = ["libEGL.so.1"] },
            LinuxGraphicsDependencies.OpenGl with { LibraryNames = ["libGL.so.1", "libOpenGL.so.0"] },
            LinuxGraphicsDependencies.X11 with { LibraryNames = ["libX11.so.6"] },
        ]);
        foreach (var library in libraries)
            output.WriteLine($"{(library.IsAvailable ? "Available" : "Missing")}: {library.Diagnostic}");

        bool displayConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
        output.WriteLine(displayConfigured ? "DISPLAY is set (connection not tested)." : "DISPLAY is missing; an X11 or XWayland session is required.");
        output.WriteLine("Native Wayland and Vulkan are not required for this backend.");
        output.WriteLine("These checks do not validate display access, the EGL driver, or window rendering.");
        return supportedArchitecture && displayConfigured && libraries.All(library => library.IsAvailable);
    }
}
