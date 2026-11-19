// SPDX-FileCopyrightText: 2026 Broiler Platform contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Broiler.Graphics.Imaging;
using Broiler.Graphics.Paging;
using Broiler.Input;
using Broiler.Input.Scanner;
using Broiler.UI;
using Xunit;

namespace Broiler.Hosting.Linux.Tests;

public sealed class LinuxScanHostTests
{
    [Fact]
    public async Task Without_Sane_Default_Host_Reports_No_Scanners_And_Fails_Cleanly()
    {
        if (LinuxScanHost.IsAvailable)
            return;

        var host = new LinuxScanHost();

        Assert.Empty(host.GetScanners());
        Assert.Equal(UiScannerCapabilities.Unknown, host.GetCapabilities("any"));

        UiScanResult result = await host.ScanAsync(
            new UiScanJob(new UiScanSettings(), isPreview: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(UiScanOutcome.Failed, result.Outcome);
        Assert.Equal("SANE is not installed.", result.Message);
    }

    [Fact]
    public async Task ScanAsync_Enumerates_Devices_And_Acquires_Pages()
    {
        var host = new LinuxScanHost(new FakeScannerProvider());
        IReadOnlyList<UiScannerInfo> scanners = host.GetScanners();

        UiScannerInfo info = Assert.Single(scanners);
        Assert.Equal("Linux Test Flatbed", info.Name);
        Assert.True(info.IsDefault);
        Assert.Equal("sane", info.Description);

        var settings = new UiScanSettings
        {
            ScannerName = info.Name,
            Source = UiScanSource.Flatbed,
            ColorMode = UiScanColorMode.Color,
            Dpi = 300,
            PageSetup = new BPageSetup(BPaperSize.A4, BPageOrientation.Portrait, BPageMargins.Uniform(0)),
        };

        var reportedProgress = new List<UiScanProgress>();
        UiScanResult result = await host.ScanAsync(
            new UiScanJob(settings, isPreview: false, reportedProgress.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(UiScanOutcome.Scanned, result.Outcome);
        Assert.Equal(1, result.PagesScanned);
        Assert.NotNull(result.Pages);
        BPixelBuffer pixels = result.Pages.GetPage(0).Images.Values.First();
        Assert.Equal(4, pixels.Width);
        Assert.Equal(3, pixels.Height);
        Assert.Equal(20, pixels.Rgba[0]);
        Assert.Equal(40, pixels.Rgba[1]);
        Assert.Equal(200, pixels.Rgba[2]);
        Assert.Equal(255, pixels.Rgba[3]);
    }

    private sealed class FakeScannerProvider : IScannerInputProvider
    {
        private static readonly InputDeviceDescriptor Descriptor = new(
            InputDeviceId.FromOpaqueValue("linux:sane:scanner:0"),
            InputKind.Scanner,
            "Linux Test Flatbed",
            InputDeviceAvailability.Available,
            [new InputCapability("scanner.capture.source", "sane")]);

        public ValueTask<IReadOnlyList<InputDeviceDescriptor>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<InputDeviceDescriptor>>([Descriptor]);

        public ValueTask<ScannerInputDevice> OpenAsync(
            InputDeviceDescriptor descriptor,
            ScannerOpenOptions options,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ScannerInputDevice>(new FakeScannerDevice(descriptor, options));
    }

    private sealed class FakeScannerDevice(InputDeviceDescriptor descriptor, ScannerOpenOptions options)
        : ScannerInputDevice(descriptor)
    {
        public override async ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            TransitionCaptureTo(ScannerCaptureState.Starting);
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
            TransitionCaptureTo(ScannerCaptureState.Scanning);

            const int width = 4;
            const int height = 3;
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = 20;
                rgba[i + 1] = 40;
                rgba[i + 2] = 200;
                rgba[i + 3] = 255;
            }

            ScannerFormat format = options.PreferredFormat
                ?? new ScannerFormat(ScannerSourceKind.Flatbed, 300, 300, ScannerPixelFormat.Rgba32);
            SetNegotiatedFormat(format);
            RaiseProgressChanged(0, 0.5, rgba.Length / 2);
            var lease = new ScannerPageLease(
                rgba,
                width,
                height,
                format,
                [new ScannerImagePlane(0, rgba.Length, width * 4, width, height)],
                Clock.GetTimestamp(),
                pageNumber: 0,
                flags: ScannerPageFlags.EndOfBatch,
                rotation: ScannerRotation.None,
                colorSpace: ScannerColorSpace.SRgb);
            RaisePageReady(lease);
            MarkCaptureCompleted();
        }
    }
}
