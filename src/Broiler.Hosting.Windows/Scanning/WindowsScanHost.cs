// SPDX-FileCopyrightText: 2026 Broiler Platform contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Broiler.Input.Scanner;
using Broiler.Input.Scanner.Windows;
using Broiler.UI;

namespace Broiler.Hosting.Windows;

/// <summary>
/// Document and image scanning on Windows through WIA 2.0 (<see cref="WindowsScannerProvider"/>),
/// exposed as an <see cref="IUiScanHost"/> for Broiler applications.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScanHost : IUiScanHost
{
    private readonly ScannerInputScanHost _inner;

    /// <summary>Creates a scan host backed by the Windows WIA 2.0 scanner provider.</summary>
    public WindowsScanHost()
        : this(new WindowsScannerProvider())
    {
    }

    internal WindowsScanHost(IScannerInputProvider provider)
    {
        _inner = new ScannerInputScanHost(provider);
    }

    /// <inheritdoc />
    public bool ShowsSystemScanDialog => _inner.ShowsSystemScanDialog;

    /// <inheritdoc />
    public IReadOnlyList<UiScannerInfo> GetScanners() => _inner.GetScanners();

    /// <inheritdoc />
    public UiScannerCapabilities GetCapabilities(string scannerName) => _inner.GetCapabilities(scannerName);

    /// <inheritdoc />
    public Task<UiScanResult> ScanAsync(UiScanJob job, CancellationToken cancellationToken = default) =>
        _inner.ScanAsync(job, cancellationToken);
}
