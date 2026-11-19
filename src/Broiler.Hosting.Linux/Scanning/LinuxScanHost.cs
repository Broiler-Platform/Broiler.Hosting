// SPDX-FileCopyrightText: 2026 Broiler Platform contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Broiler.Input.Scanner;
using Broiler.Input.Scanner.Linux;
using Broiler.UI;

namespace Broiler.Hosting.Linux;

/// <summary>
/// Document and image scanning on Linux through SANE (<see cref="LinuxScannerProvider"/>),
/// exposed as an <see cref="IUiScanHost"/> for Broiler applications.
/// </summary>
public sealed class LinuxScanHost : IUiScanHost
{
    private readonly ScannerInputScanHost _inner;
    private readonly bool _checkAvailability;

    /// <summary>True on Linux when SANE (<c>libsane.so.1</c>) is installed.</summary>
    public static bool IsAvailable =>
        OperatingSystem.IsLinux() && LinuxScannerProvider.CheckDependencies().IsAvailable;

    /// <summary>Creates a scan host backed by the Linux SANE scanner provider.</summary>
    public LinuxScanHost()
        : this(new LinuxScannerProvider(), checkAvailability: true)
    {
    }

    internal LinuxScanHost(IScannerInputProvider provider)
        : this(provider, checkAvailability: false)
    {
    }

    private LinuxScanHost(IScannerInputProvider provider, bool checkAvailability)
    {
        _inner = new ScannerInputScanHost(provider);
        _checkAvailability = checkAvailability;
    }

    /// <inheritdoc />
    public bool ShowsSystemScanDialog => _inner.ShowsSystemScanDialog;

    /// <inheritdoc />
    public IReadOnlyList<UiScannerInfo> GetScanners() =>
        _checkAvailability && !IsAvailable ? [] : _inner.GetScanners();

    /// <inheritdoc />
    public UiScannerCapabilities GetCapabilities(string scannerName) =>
        _checkAvailability && !IsAvailable ? UiScannerCapabilities.Unknown : _inner.GetCapabilities(scannerName);

    /// <inheritdoc />
    public Task<UiScanResult> ScanAsync(UiScanJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_checkAvailability && !IsAvailable)
            return Task.FromResult(UiScanResult.Failed("SANE is not installed."));

        return _inner.ScanAsync(job, cancellationToken);
    }
}
