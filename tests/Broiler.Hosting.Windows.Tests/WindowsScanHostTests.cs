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

namespace Broiler.Hosting.Windows.Tests;

public sealed class WindowsScanHostTests
{
    [Fact]
    public void ShowsSystemScanDialog_Is_False()
    {
        var host = new WindowsScanHost(new FakeScannerProvider());
        Assert.False(host.ShowsSystemScanDialog);
    }

    [Fact]
    public void GetScanners_Enumerates_Devices_And_Disambiguates_Duplicate_DisplayNames()
    {
        var first = new InputDeviceDescriptor(
            InputDeviceId.FromOpaqueValue("fake:scanner:0"),
            InputKind.Scanner,
            "Office Scanner",
            InputDeviceAvailability.Available,
            [new InputCapability("scanner.capture.source", "wia-2.0")]);
        var second = new InputDeviceDescriptor(
            InputDeviceId.FromOpaqueValue("fake:scanner:1"),
            InputKind.Scanner,
            "Office Scanner",
            InputDeviceAvailability.Available,
            [new InputCapability("scanner.capture.source", "wia-2.0")]);

        var host = new WindowsScanHost(new FakeScannerProvider([first, second]));
        IReadOnlyList<UiScannerInfo> scanners = host.GetScanners();

        Assert.Equal(2, scanners.Count);
        Assert.Equal("Office Scanner", scanners[0].Name);
        Assert.True(scanners[0].IsDefault);
        Assert.Equal("wia-2.0", scanners[0].Description);
        Assert.Equal("Office Scanner (fake:scanner:1)", scanners[1].Name);
        Assert.False(scanners[1].IsDefault);
    }

    [Fact]
    public void GetCapabilities_Returns_Unknown_When_No_Scanners_Exist_And_Standard_When_Present()
    {
        var emptyHost = new WindowsScanHost(new FakeScannerProvider([]));
        Assert.Equal(UiScannerCapabilities.Unknown, emptyHost.GetCapabilities("Missing"));

        var host = new WindowsScanHost(new FakeScannerProvider());
        UiScannerCapabilities capabilities = host.GetCapabilities("Broiler Test Flatbed");
        Assert.Contains(UiScanSource.Flatbed, capabilities.Sources);
        Assert.Contains(UiScanColorMode.Color, capabilities.ColorModes);
        Assert.Contains(300, capabilities.Resolutions);
    }

    [Fact]
    public async Task ScanAsync_Acquires_Pages_And_Reports_Progress()
    {
        var host = new WindowsScanHost(new FakeScannerProvider());
        IReadOnlyList<UiScannerInfo> scanners = host.GetScanners();
        UiScannerInfo info = Assert.Single(scanners);

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
        Assert.Equal(1, result.Pages.PageCount);
        Assert.NotEmpty(reportedProgress);
        Assert.Contains(reportedProgress, static p => p.Fraction >= 0.5);
        Assert.Equal(1.0, reportedProgress[^1].Fraction, precision: 3);

        BPixelBuffer pixels = result.Pages.GetPage(0).Images.Values.First();
        Assert.Equal(4, pixels.Width);
        Assert.Equal(3, pixels.Height);
        ReadOnlySpan<byte> bytes = pixels.Rgba;
        Assert.Equal(20, bytes[0]);
        Assert.Equal(40, bytes[1]);
        Assert.Equal(200, bytes[2]);
        Assert.Equal(255, bytes[3]);
    }

    [Fact]
    public async Task ScanAsync_Returns_Failed_When_No_Scanners_Are_Available()
    {
        var host = new WindowsScanHost(new FakeScannerProvider([]));
        var job = new UiScanJob(new UiScanSettings(), isPreview: false);

        UiScanResult result = await host.ScanAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(UiScanOutcome.Failed, result.Outcome);
        Assert.Equal("No scanners found.", result.Message);
    }

    [Fact]
    public async Task ScanAsync_Returns_Failed_When_Device_Faults()
    {
        var host = new WindowsScanHost(new FakeScannerProvider(faultMessage: "Paper jam"));
        var job = new UiScanJob(new UiScanSettings(), isPreview: false);

        UiScanResult result = await host.ScanAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(UiScanOutcome.Failed, result.Outcome);
        Assert.Equal("Paper jam", result.Message);
    }

    [Theory]
    [InlineData(ScannerPixelFormat.Bgra32, new byte[] { 200, 40, 20, 255 }, new byte[] { 20, 40, 200, 255 })]
    [InlineData(ScannerPixelFormat.Rgb24, new byte[] { 20, 40, 200 }, new byte[] { 20, 40, 200, 255 })]
    [InlineData(ScannerPixelFormat.Grayscale8, new byte[] { 128 }, new byte[] { 128, 128, 128, 255 })]
    [InlineData(ScannerPixelFormat.Grayscale16, new byte[] { 0x10, 0x9A }, new byte[] { 0x9A, 0x9A, 0x9A, 255 })]
    [InlineData(ScannerPixelFormat.BlackAndWhite1Bit, new byte[] { 0x80 }, new byte[] { 255, 255, 255, 255 })]
    public void ConvertLeaseToRgbaPixelBuffer_Converts_Scanner_Pixel_Formats(
        ScannerPixelFormat pixelFormat,
        byte[] sampleBytes,
        byte[] expectedFirstPixelRgba)
    {
        var format = new ScannerFormat(ScannerSourceKind.Flatbed, 300, 300, pixelFormat);
        var lease = new ScannerPageLease(
            sampleBytes,
            width: 1,
            height: 1,
            format,
            [new ScannerImagePlane(0, sampleBytes.Length, sampleBytes.Length, 1, 1)],
            new InputTimestamp(1, 1000, "test"),
            pageNumber: 0);

        BPixelBuffer buffer = ScannerInputScanHost.ConvertLeaseToRgbaPixelBuffer(lease);

        Assert.Equal(expectedFirstPixelRgba, buffer.Rgba.ToArray());
    }

    private sealed class FakeScannerProvider(
        IReadOnlyList<InputDeviceDescriptor>? descriptors = null,
        string? faultMessage = null) : IScannerInputProvider
    {
        private static readonly InputDeviceDescriptor DefaultDescriptor = new(
            InputDeviceId.FromOpaqueValue("fake:scanner:0"),
            InputKind.Scanner,
            "Broiler Test Flatbed",
            InputDeviceAvailability.Available,
            [new InputCapability("scanner.capture.source", "fake")]);

        private readonly IReadOnlyList<InputDeviceDescriptor> _descriptors = descriptors ?? [DefaultDescriptor];

        public ValueTask<IReadOnlyList<InputDeviceDescriptor>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_descriptors);

        public ValueTask<ScannerInputDevice> OpenAsync(
            InputDeviceDescriptor descriptor,
            ScannerOpenOptions options,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ScannerInputDevice>(new FakeScannerDevice(descriptor, options, faultMessage));
    }

    private sealed class FakeScannerDevice(
        InputDeviceDescriptor descriptor,
        ScannerOpenOptions options,
        string? faultMessage)
        : ScannerInputDevice(descriptor)
    {
        public override async ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            TransitionCaptureTo(ScannerCaptureState.Starting);
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
            TransitionCaptureTo(ScannerCaptureState.Scanning);

            if (faultMessage is not null)
            {
                MarkCaptureFaulted(
                    new InputFault(InputErrorCategory.NativeFailure, faultMessage),
                    ScannerFaultCode.PaperJam);
                return;
            }

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
