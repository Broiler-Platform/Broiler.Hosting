using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Broiler.Hosting.Android;

/// <summary>
/// Diagnostic probe for the Android native hosting environment.
/// Verifies operating system platform, architecture, and native graphic libraries (EGL, GLESv3, libandroid).
/// </summary>
public static class AndroidBackendDiagnostics
{
    private static readonly string[] RequiredLibraries =
    [
        "libEGL.so",
        "libGLESv3.so",
        "libandroid.so",
    ];

    public static AndroidDiagnosticsReport Capture()
    {
        bool isAndroid = OperatingSystem.IsAndroid();
        var libraryStatuses = new List<AndroidNativeLibraryStatus>();

        if (isAndroid)
        {
            foreach (string lib in RequiredLibraries)
            {
                bool loaded = NativeLibrary.TryLoad(lib, typeof(AndroidBackendDiagnostics).Assembly, null, out IntPtr handle);
                if (loaded && handle != IntPtr.Zero)
                {
                    NativeLibrary.Free(handle);
                }

                libraryStatuses.Add(new AndroidNativeLibraryStatus(lib, loaded));
            }
        }
        else
        {
            foreach (string lib in RequiredLibraries)
            {
                libraryStatuses.Add(new AndroidNativeLibraryStatus(lib, Available: false, Note: "Skipped (non-Android host)"));
            }
        }

        return new AndroidDiagnosticsReport(
            IsAndroid: isAndroid,
            OperatingSystemDescription: RuntimeInformation.OSDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            Libraries: libraryStatuses);
    }

    public static bool Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        AndroidDiagnosticsReport report = Capture();

        output.WriteLine("Broiler Android Backend Diagnostics");
        output.WriteLine($"  Operating system: {report.OperatingSystemDescription}");
        output.WriteLine($"  Architecture:     {report.Architecture}");
        output.WriteLine($"  Framework:        {report.FrameworkDescription}");
        output.WriteLine($"  IsAndroid:        {report.IsAndroid}");

        output.WriteLine("Native Libraries:");
        bool allRequiredPresent = true;
        foreach (AndroidNativeLibraryStatus status in report.Libraries)
        {
            string detail = status.Note is not null ? $" ({status.Note})" : string.Empty;
            output.WriteLine($"  {status.Name}: {(status.Available ? "Available" : "Missing")}{detail}");
            if (!status.Available && report.IsAndroid)
            {
                allRequiredPresent = false;
            }
        }

        return !report.IsAndroid || allRequiredPresent;
    }
}

public sealed record AndroidNativeLibraryStatus(string Name, bool Available, string? Note = null);

public sealed record AndroidDiagnosticsReport(
    bool IsAndroid,
    string OperatingSystemDescription,
    string Architecture,
    string FrameworkDescription,
    IReadOnlyList<AndroidNativeLibraryStatus> Libraries);
