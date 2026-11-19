// SPDX-FileCopyrightText: 2026 Broiler Platform contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Imaging;
using Broiler.Graphics.Paging;
using Broiler.Input;
using Broiler.Input.Scanner;
using Broiler.UI;

namespace Broiler.Hosting;

/// <summary>
/// Adapts an <see cref="IScannerInputProvider"/> (<c>Broiler.Input.Scanner</c>) to the UI toolkit's
/// <see cref="IUiScanHost"/> contract.
/// </summary>
internal sealed class ScannerInputScanHost : IUiScanHost
{
    private readonly IScannerInputProvider _provider;
    private readonly Lock _gate = new();
    private Dictionary<string, InputDeviceDescriptor> _devicesByName = new(StringComparer.Ordinal);

    public ScannerInputScanHost(IScannerInputProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public bool ShowsSystemScanDialog => false;

    public IReadOnlyList<UiScannerInfo> GetScanners()
    {
        IReadOnlyList<InputDeviceDescriptor> descriptors;
        try
        {
            descriptors = _provider.GetDevicesAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            descriptors = [];
        }

        var map = new Dictionary<string, InputDeviceDescriptor>(StringComparer.Ordinal);
        var scanners = new List<UiScannerInfo>(descriptors.Count);
        for (int i = 0; i < descriptors.Count; i++)
        {
            InputDeviceDescriptor descriptor = descriptors[i];
            string baseName = string.IsNullOrWhiteSpace(descriptor.DisplayName)
                ? descriptor.Id.Value
                : descriptor.DisplayName;
            string name = map.ContainsKey(baseName)
                ? $"{baseName} ({descriptor.Id.Value})"
                : baseName;

            map[name] = descriptor;
            string? sourceBackend = descriptor.Capabilities
                .FirstOrDefault(static c => c.Name == "scanner.capture.source")
                .Value;
            scanners.Add(new UiScannerInfo(
                name,
                IsDefault: i == 0,
                Description: sourceBackend));
        }

        lock (_gate)
        {
            _devicesByName = map;
        }

        return scanners;
    }

    public UiScannerCapabilities GetCapabilities(string scannerName)
    {
        if (!TryResolveDevice(scannerName, out InputDeviceDescriptor? descriptor) || descriptor is null)
            return UiScannerCapabilities.Unknown;

        return new UiScannerCapabilities
        {
            Sources =
            [
                UiScanSource.Flatbed,
                UiScanSource.FeederOneSided,
                UiScanSource.FeederTwoSided,
            ],
            ColorModes =
            [
                UiScanColorMode.Color,
                UiScanColorMode.Grayscale,
                UiScanColorMode.BlackAndWhite,
            ],
            Resolutions = [75, 100, 150, 200, 300, 600],
            DefaultResolution = 300,
            Papers = BPaperSize.Standard,
            MaximumPages = UiScanSettings.MaximumPages,
        };
    }

    public async Task<UiScanResult> ScanAsync(UiScanJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        UiScanSettings settings = job.Settings;

        if (!TryResolveDevice(settings.ScannerName, out InputDeviceDescriptor? descriptor) || descriptor is null)
            return UiScanResult.Failed("No scanners found.");

        ScannerSourceKind source = FromUiSource(settings.Source);
        ScannerPixelFormat pixelFormat = FromUiColorMode(settings.ColorMode);
        ScannerRegion? region = CreateRegion(settings.PageSetup.PageSize);
        var preferredFormat = new ScannerFormat(source, settings.Dpi, settings.Dpi, pixelFormat, region);
        var sessionOptions = new ScannerSessionOptions(
            deliveryMode: job.IsPreview
                ? ScannerDeliveryMode.LatestPagePreview
                : ScannerDeliveryMode.LossSensitiveBatch,
            maxPages: job.IsPreview ? 1 : settings.MaxPages,
            brightness: Math.Clamp(settings.Brightness / 100.0, -1.0, 1.0),
            contrast: Math.Clamp(settings.Contrast / 100.0, -1.0, 1.0));
        var openOptions = new ScannerOpenOptions(preferredFormat, sessionOptions);

        ScannerInputDevice scanner;
        try
        {
            scanner = await _provider.OpenAsync(descriptor, openOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return UiScanResult.Cancelled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return UiScanResult.Failed(exception.Message);
        }

        await using (scanner.ConfigureAwait(false))
        {
            var pages = new List<BPixelBuffer>();
            var pageGate = new Lock();
            ScannerFaultCode fault = ScannerFaultCode.None;
            string? faultMessage = null;
            bool captureTerminal = false;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void TryCompleteIfReadyLocked()
            {
                if (!captureTerminal)
                    return;

                long capturedCount = scanner.CaptureStatistics.PagesCapturedCount;
                if (pages.Count >= capturedCount && (capturedCount > 0 || fault != ScannerFaultCode.None || faultMessage is not null))
                    completion.TrySetResult(true);
            }

            void OnPageReady(ScannerPageReadyEvent e)
            {
                using ScannerPageLease lease = e.Page;
                BPixelBuffer buffer = ConvertLeaseToRgbaPixelBuffer(lease);
                lock (pageGate)
                {
                    pages.Add(buffer);
                    job.ReportProgress((int)e.Page.PageNumber, 1.0, e.Page.Memory.Length);
                    TryCompleteIfReadyLocked();
                }
            }

            void OnProgressChanged(ScannerProgressEvent e) =>
                job.ReportProgress((int)e.PageNumber, e.ProgressFraction, e.BytesTransferred);

            void OnStateChanged(ScannerCaptureStateChangedEvent e)
            {
                lock (pageGate)
                {
                    if (e.FaultCode != ScannerFaultCode.None)
                    {
                        fault = e.FaultCode;
                        faultMessage = e.Fault?.Message;
                    }
                    else if (e.Fault is not null && faultMessage is null)
                    {
                        faultMessage = e.Fault.Message;
                    }

                    if (e.CurrentState is ScannerCaptureState.Completed
                        or ScannerCaptureState.Faulted
                        or ScannerCaptureState.Invalidated
                        or ScannerCaptureState.Stopped)
                    {
                        captureTerminal = true;
                        TryCompleteIfReadyLocked();
                    }
                }
            }

            scanner.PageReady += OnPageReady;
            scanner.ProgressChanged += OnProgressChanged;
            scanner.CaptureStateChanged += OnStateChanged;
            try
            {
                await scanner.OpenAsync(cancellationToken).ConfigureAwait(false);
                await scanner.StartAsync(cancellationToken).ConfigureAwait(false);

                if (scanner.CaptureState is ScannerCaptureState.Completed
                    or ScannerCaptureState.Faulted
                    or ScannerCaptureState.Invalidated
                    or ScannerCaptureState.Stopped)
                {
                    lock (pageGate)
                    {
                        captureTerminal = true;
                        TryCompleteIfReadyLocked();
                    }
                }

                // Wait for completion, or once capture enters a terminal state, wait briefly for
                // the background delivery thread to flush any remaining queued pages.
                while (!completion.Task.IsCompleted)
                {
                    Task finished = await Task.WhenAny(
                        completion.Task,
                        Task.Delay(50, cancellationToken)).ConfigureAwait(false);
                    if (finished == completion.Task)
                        break;

                    bool isTerminal;
                    int delivered;
                    lock (pageGate)
                    {
                        isTerminal = captureTerminal;
                        delivered = pages.Count;
                    }

                    if (isTerminal && delivered >= scanner.CaptureStatistics.PagesCapturedCount)
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                return UiScanResult.Cancelled;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                lock (pageGate)
                {
                    if (pages.Count == 0)
                        return UiScanResult.Failed(exception.Message);
                }
            }
            finally
            {
                scanner.PageReady -= OnPageReady;
                scanner.ProgressChanged -= OnProgressChanged;
                scanner.CaptureStateChanged -= OnStateChanged;
            }

            List<BPixelBuffer> capturedPages;
            ScannerFaultCode finalFault;
            string? finalFaultMessage;
            lock (pageGate)
            {
                capturedPages = [.. pages];
                finalFault = fault;
                finalFaultMessage = faultMessage;
            }

            if (capturedPages.Count == 0)
            {
                if (finalFault != ScannerFaultCode.None || !string.IsNullOrWhiteSpace(finalFaultMessage))
                    return UiScanResult.Failed(finalFaultMessage ?? $"Scanner fault: {finalFault}.");

                return UiScanResult.Cancelled;
            }

            return UiScanResult.Scanned(capturedPages, settings.Dpi, settings.PageSetup.PageSize);
        }
    }

    internal static BPixelBuffer ConvertLeaseToRgbaPixelBuffer(ScannerPageLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        int width = lease.Width;
        int height = lease.Height;
        ReadOnlySpan<byte> src = lease.Memory.Span;
        byte[] rgba = new byte[checked(width * height * 4)];
        int stride = lease.Planes.Count > 0 && lease.Planes[0].Stride > 0
            ? lease.Planes[0].Stride
            : 0;

        switch (lease.Format.PixelFormat)
        {
            case ScannerPixelFormat.Rgba32:
            {
                int rowBytes = width * 4;
                int srcStride = stride >= rowBytes ? stride : rowBytes;
                for (int y = 0; y < height; y++)
                {
                    ReadOnlySpan<byte> row = src.Slice(y * srcStride, Math.Min(rowBytes, src.Length - y * srcStride));
                    row.CopyTo(rgba.AsSpan(y * rowBytes, row.Length));
                }
                break;
            }

            case ScannerPixelFormat.Bgra32:
            {
                int rowBytes = width * 4;
                int srcStride = stride >= rowBytes ? stride : rowBytes;
                for (int y = 0; y < height; y++)
                {
                    int srcRow = y * srcStride;
                    int dstRow = y * rowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        int s = srcRow + x * 4;
                        int d = dstRow + x * 4;
                        if (s + 3 >= src.Length)
                            break;
                        rgba[d] = src[s + 2];
                        rgba[d + 1] = src[s + 1];
                        rgba[d + 2] = src[s];
                        rgba[d + 3] = src[s + 3];
                    }
                }
                break;
            }

            case ScannerPixelFormat.Rgb24:
            {
                int rowBytes = width * 3;
                int srcStride = stride >= rowBytes ? stride : rowBytes;
                for (int y = 0; y < height; y++)
                {
                    int srcRow = y * srcStride;
                    int dstRow = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int s = srcRow + x * 3;
                        int d = dstRow + x * 4;
                        if (s + 2 >= src.Length)
                            break;
                        rgba[d] = src[s];
                        rgba[d + 1] = src[s + 1];
                        rgba[d + 2] = src[s + 2];
                        rgba[d + 3] = 255;
                    }
                }
                break;
            }

            case ScannerPixelFormat.Grayscale16:
            {
                int rowBytes = width * 2;
                int srcStride = stride >= rowBytes ? stride : rowBytes;
                for (int y = 0; y < height; y++)
                {
                    int srcRow = y * srcStride;
                    int dstRow = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int s = srcRow + x * 2;
                        int d = dstRow + x * 4;
                        if (s + 1 >= src.Length)
                            break;
                        byte lum = src[s + 1];
                        rgba[d] = lum;
                        rgba[d + 1] = lum;
                        rgba[d + 2] = lum;
                        rgba[d + 3] = 255;
                    }
                }
                break;
            }

            case ScannerPixelFormat.BlackAndWhite1Bit:
            {
                int minRowBytes = (width + 7) / 8;
                int srcStride = stride >= minRowBytes ? stride : minRowBytes;
                for (int y = 0; y < height; y++)
                {
                    int srcRow = y * srcStride;
                    int dstRow = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int s = srcRow + (x >> 3);
                        int d = dstRow + x * 4;
                        if (s >= src.Length)
                            break;
                        int bit = (src[s] >> (7 - (x & 7))) & 1;
                        byte lum = bit != 0 ? (byte)255 : (byte)0;
                        rgba[d] = lum;
                        rgba[d + 1] = lum;
                        rgba[d + 2] = lum;
                        rgba[d + 3] = 255;
                    }
                }
                break;
            }

            default:
            {
                int srcStride = stride >= width ? stride : width;
                for (int y = 0; y < height; y++)
                {
                    int srcRow = y * srcStride;
                    int dstRow = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int s = srcRow + x;
                        int d = dstRow + x * 4;
                        if (s >= src.Length)
                            break;
                        byte lum = src[s];
                        rgba[d] = lum;
                        rgba[d + 1] = lum;
                        rgba[d + 2] = lum;
                        rgba[d + 3] = 255;
                    }
                }
                break;
            }
        }

        return new BPixelBuffer(width, height, rgba);
    }

    private bool TryResolveDevice(string? scannerName, out InputDeviceDescriptor? descriptor)
    {
        lock (_gate)
        {
            if (_devicesByName.Count == 0)
            {
                GetScanners();
            }

            if (!string.IsNullOrEmpty(scannerName) && _devicesByName.TryGetValue(scannerName, out descriptor))
                return true;

            descriptor = _devicesByName.Values.FirstOrDefault();
            return descriptor is not null;
        }
    }

    private static ScannerSourceKind FromUiSource(UiScanSource source) =>
        source switch
        {
            UiScanSource.FeederOneSided => ScannerSourceKind.FeederSimplex,
            UiScanSource.FeederTwoSided => ScannerSourceKind.FeederDuplex,
            _ => ScannerSourceKind.Flatbed,
        };

    private static ScannerPixelFormat FromUiColorMode(UiScanColorMode mode) =>
        mode switch
        {
            UiScanColorMode.Grayscale => ScannerPixelFormat.Grayscale8,
            UiScanColorMode.BlackAndWhite => ScannerPixelFormat.BlackAndWhite1Bit,
            _ => ScannerPixelFormat.Rgba32,
        };

    private static ScannerRegion? CreateRegion(BSize pageSizeInPoints)
    {
        if (pageSizeInPoints.IsEmpty || pageSizeInPoints.Width <= 0 || pageSizeInPoints.Height <= 0)
            return null;

        double widthInches = Math.Max(0.1, pageSizeInPoints.Width / 72.0);
        double heightInches = Math.Max(0.1, pageSizeInPoints.Height / 72.0);
        return ScannerRegion.FromInches(0, 0, widthInches, heightInches);
    }
}
