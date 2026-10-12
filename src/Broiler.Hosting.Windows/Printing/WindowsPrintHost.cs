using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Paging;
using Broiler.Native.Windows;
using Broiler.Native.Windows.Printing;
using Broiler.UI;

namespace Broiler.Hosting.Windows;

/// <summary>
/// Printing through the Windows spooler: the printers the user can reach, what each can do, and
/// jobs sent to a GDI printer device context as bands of the rasterized page.
/// </summary>
/// <remarks>
/// <para>
/// Every printer Windows can print to takes a device context, from a 1990s laser to a PDF or
/// XPS writer, so a page rasterized at the printer's resolution and drawn with StretchDIBits is
/// the path that prints everywhere. A page is rasterized in bands (see
/// <see cref="BPageRasterizer"/>), so a page at 600 dpi is never one bitmap of the whole sheet.
/// A vector path - Direct2D's print control over an XPS package - is the next step, and slots in
/// behind the same <see cref="IUiPrintHost"/>.
/// </para>
/// <para>
/// The job's choices are written into the printer's own device mode and handed back to the
/// driver to validate, so a paper or a resolution the driver adjusts is the one printed on.
/// Copies are printed by this host, collated or not, rather than asked of the driver, because
/// several drivers - the PDF writers among them - ignore the copy count.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows5.0")]
public sealed class WindowsPrintHost : IUiPrintHost
{
    /// <summary>The finest resolution a page is rasterized at when the job names none.</summary>
    public const double DefaultMaximumDpi = 600;

    private const int ErrorCancelled = 1223;
    private const short CustomPaperSize = 0;
    private const int MinimumDevModeBytes = 102; // through dmCollate

    /// <summary>The finest resolution a page is rasterized at when the job names none; a finer printer is scaled up to.</summary>
    public double MaximumDpi { get; init; } = DefaultMaximumDpi;

    /// <summary>Rows rasterized and sent to the device at a time.</summary>
    public int BandHeight { get; init; } = BPageRasterizer.DefaultBandHeight;

    public bool ShowsSystemPrintDialog => false;

    public IReadOnlyList<UiPrinterInfo> GetPrinters()
    {
        string? defaultPrinter = GetDefaultPrinterName();
        const uint flags = PrintSpoolerNative.PRINTER_ENUM_LOCAL | PrintSpoolerNative.PRINTER_ENUM_CONNECTIONS;

        PrintSpoolerNative.EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out uint needed, out _);
        if (needed == 0)
            return [];

        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!PrintSpoolerNative.EnumPrinters(flags, null, 4, buffer, needed, out _, out uint returned))
                return [];

            var printers = new List<UiPrinterInfo>((int)returned);
            int stride = Marshal.SizeOf<PRINTER_INFO_4W>();
            for (int i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<PRINTER_INFO_4W>(buffer + (i * stride));
                string? name = Marshal.PtrToStringUni(info.PrinterName);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string? server = Marshal.PtrToStringUni(info.ServerName);
                printers.Add(new UiPrinterInfo(
                    name,
                    IsDefault: string.Equals(name, defaultPrinter, StringComparison.OrdinalIgnoreCase),
                    Description: string.IsNullOrEmpty(server) ? null : "On " + server.TrimStart('\\')));
            }

            return printers;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public UiPrinterCapabilities GetCapabilities(string printerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);

        List<DriverPaper> papers = GetDriverPapers(printerName);
        var offered = new List<BPaperSize>();
        foreach (DriverPaper paper in papers)
        {
            if (!offered.Exists(existing => existing.Matches(paper.Size.Size)))
                offered.Add(paper.Size);
        }

        BPaperSize? defaultPaper = null;
        using (DevMode? devMode = DevMode.Open(printerName))
        {
            if (devMode is not null && devMode.Public.PaperSize != CustomPaperSize)
            {
                short id = devMode.Public.PaperSize;
                DriverPaper? match = papers.Find(paper => paper.Id == id);
                if (match is not null)
                    defaultPaper = match.Size;
            }
        }

        return new UiPrinterCapabilities
        {
            Papers = offered.Count > 0 ? offered : BPaperSize.Standard,
            DefaultPaper = defaultPaper,
            SupportsColor = PrintSpoolerNative.DeviceCapabilities(printerName, null, PrintSpoolerNative.DC_COLORDEVICE, IntPtr.Zero, IntPtr.Zero) == 1,
            SupportsDuplex = PrintSpoolerNative.DeviceCapabilities(printerName, null, PrintSpoolerNative.DC_DUPLEX, IntPtr.Zero, IntPtr.Zero) == 1,
            Resolutions = GetResolutions(printerName),
        };
    }

    public Task<UiPrintResult> PrintAsync(UiPrintJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Task.Run(() => Print(job, outputFile: null, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Prints <paramref name="job"/> to <paramref name="outputFile"/> instead of the printer's port:
    /// what the driver would have sent, written to a file. For a PDF or XPS writer that is the
    /// document itself, with no Save As dialog.
    /// </summary>
    public Task<UiPrintResult> PrintToFileAsync(UiPrintJob job, string outputFile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        return Task.Run(() => Print(job, outputFile, cancellationToken), cancellationToken);
    }

    private UiPrintResult Print(UiPrintJob job, string? outputFile, CancellationToken cancellationToken)
    {
        string? printer = job.Settings.PrinterName ?? GetDefaultPrinterName();
        if (string.IsNullOrWhiteSpace(printer))
            return UiPrintResult.Failed("No printer is installed.");

        IBPageSource pages = job.ResolvePages(cancellationToken);
        IReadOnlyList<int> selected = job.Settings.SelectPages(pages.PageCount);
        if (selected.Count == 0)
            return UiPrintResult.Failed("The page range selects no pages.");

        using DevMode? devMode = DevMode.Open(printer);
        devMode?.Apply(job.Settings, GetDriverPapers(printer));

        IntPtr hdc = PrintDeviceNative.CreateDC(null, printer, null, devMode?.Buffer ?? IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return UiPrintResult.Failed($"Windows could not open the printer \"{printer}\" ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");

        try
        {
            int jobId = StartDocument(hdc, job.Title, outputFile, out int error);
            if (jobId <= 0)
            {
                return error == ErrorCancelled
                    ? UiPrintResult.Cancelled
                    : UiPrintResult.Failed($"The print job could not start ({new Win32Exception(error).Message}).");
            }

            int printed = 0;
            bool finished = false;
            try
            {
                foreach (int index in Order(selected, job.Settings.Copies, job.Settings.Collate))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PrintPage(hdc, pages.GetPage(index), job.Settings.Dpi, cancellationToken);
                    printed++;
                }

                if (PrintDeviceNative.EndDoc(hdc) <= 0)
                    return UiPrintResult.Failed($"The print job could not finish ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");

                finished = true;
                return UiPrintResult.Printed(printed);
            }
            catch (OperationCanceledException)
            {
                return UiPrintResult.Cancelled;
            }
            catch (Win32Exception exception)
            {
                return UiPrintResult.Failed(exception.Message);
            }
            finally
            {
                if (!finished)
                    PrintDeviceNative.AbortDoc(hdc);
            }
        }
        finally
        {
            PrintDeviceNative.DeleteDC(hdc);
        }
    }

    /// <summary>
    /// The pages in the order they go to the device: each copy whole before the next when
    /// collated, every copy of a page together when not.
    /// </summary>
    internal static IEnumerable<int> Order(IReadOnlyList<int> pages, int copies, bool collate)
    {
        if (collate)
        {
            for (int copy = 0; copy < copies; copy++)
            {
                foreach (int page in pages)
                    yield return page;
            }

            yield break;
        }

        foreach (int page in pages)
        {
            for (int copy = 0; copy < copies; copy++)
                yield return page;
        }
    }

    /// <summary>
    /// Where a page of <paramref name="pageSize"/> lands on a device sheet: the scale from page
    /// pixels at <paramref name="rasterDpi"/> to device pixels, which also shrinks a page larger
    /// than the sheet the driver chose so that none of it is cut off.
    /// </summary>
    internal static (double ScaleX, double ScaleY) Placement(BSize pageSize, double rasterDpi, int deviceDpiX, int deviceDpiY,
        int physicalWidth, int physicalHeight)
    {
        double pageWidth = pageSize.Width * deviceDpiX / BPaperSize.PointsPerInch;
        double pageHeight = pageSize.Height * deviceDpiY / BPaperSize.PointsPerInch;
        double fit = 1;
        if (physicalWidth > 0 && physicalHeight > 0 && (pageWidth > physicalWidth + 2 || pageHeight > physicalHeight + 2))
            fit = Math.Min(physicalWidth / pageWidth, physicalHeight / pageHeight);

        return (deviceDpiX / rasterDpi * fit, deviceDpiY / rasterDpi * fit);
    }

    private unsafe void PrintPage(IntPtr hdc, BPage page, double? requestedDpi, CancellationToken cancellationToken)
    {
        if (PrintDeviceNative.StartPage(hdc) <= 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        int dpiX = Math.Max(1, WindowNative.GetDeviceCaps(hdc, PrintDeviceNative.LOGPIXELSX));
        int dpiY = Math.Max(1, WindowNative.GetDeviceCaps(hdc, PrintDeviceNative.LOGPIXELSY));
        int physicalWidth = WindowNative.GetDeviceCaps(hdc, PrintDeviceNative.PHYSICALWIDTH);
        int physicalHeight = WindowNative.GetDeviceCaps(hdc, PrintDeviceNative.PHYSICALHEIGHT);
        int offsetX = WindowNative.GetDeviceCaps(hdc, PrintDeviceNative.PHYSICALOFFSETX);
        int offsetY = WindowNative.GetDeviceCaps(hdc, PrintDeviceNative.PHYSICALOFFSETY);

        double rasterDpi = Math.Clamp(requestedDpi ?? Math.Min(Math.Min(dpiX, dpiY), MaximumDpi), 72, BPageRasterizer.MaximumDpi);
        (double scaleX, double scaleY) = Placement(page.Size, rasterDpi, dpiX, dpiY, physicalWidth, physicalHeight);

        PrintDeviceNative.SetStretchBltMode(hdc, PrintDeviceNative.HALFTONE);
        PrintDeviceNative.SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);

        byte[]? bgra = null;
        BPageRasterizer.RenderBands(page, rasterDpi, BColor.White, BandHeight, band =>
        {
            int width = band.PageWidth;
            int rows = band.Pixels.Height;
            int length = width * rows * 4;
            if (bgra is null || bgra.Length < length)
                bgra = new byte[length];

            ReadOnlySpan<byte> rgba = band.Pixels.Rgba;
            for (int i = 0; i < length; i += 4)
            {
                bgra[i] = rgba[i + 2];
                bgra[i + 1] = rgba[i + 1];
                bgra[i + 2] = rgba[i];
                bgra[i + 3] = 0;
            }

            // Device rows for this band from where the band's top and bottom land, so adjacent
            // bands meet exactly however the scale rounds.
            int top = (int)Math.Round(band.Top * scaleY);
            int bottom = (int)Math.Round((band.Top + rows) * scaleY);
            var header = new WindowNative.BITMAPINFOHEADER
            {
                BiSize = (uint)Marshal.SizeOf<WindowNative.BITMAPINFOHEADER>(),
                BiWidth = width,
                BiHeight = -rows,
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = PrintDeviceNative.BI_RGB,
            };

            fixed (byte* bits = bgra)
            {
                int copied = PrintDeviceNative.StretchDIBits(hdc, -offsetX, top - offsetY, (int)Math.Round(width * scaleX), bottom - top,
                    0, 0, width, rows, bits, header, PrintDeviceNative.DIB_RGB_COLORS, PrintDeviceNative.SRCCOPY);
                if (copied == 0 || copied == PrintDeviceNative.GDI_ERROR)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The printer did not take the page image.");
            }
        }, cancellationToken: cancellationToken);

        if (PrintDeviceNative.EndPage(hdc) <= 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static int StartDocument(IntPtr hdc, string title, string? outputFile, out int error)
    {
        IntPtr name = Marshal.StringToHGlobalUni(string.IsNullOrWhiteSpace(title) ? "Document" : title);
        IntPtr output = outputFile is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(outputFile);
        try
        {
            var info = new DOCINFOW { Size = Marshal.SizeOf<DOCINFOW>(), DocumentName = name, Output = output };
            int job = PrintDeviceNative.StartDoc(hdc, info);
            error = job > 0 ? 0 : Marshal.GetLastWin32Error();
            return job;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            if (output != IntPtr.Zero)
                Marshal.FreeHGlobal(output);
        }
    }

    private static unsafe string? GetDefaultPrinterName()
    {
        uint length = 0;
        PrintSpoolerNative.GetDefaultPrinter(null, ref length);
        if (length == 0 || length > 4096)
            return null;

        char[] buffer = new char[length];
        fixed (char* chars = buffer)
        {
            if (!PrintSpoolerNative.GetDefaultPrinter(chars, ref length) || length == 0)
                return null;

            // The count includes the terminator.
            return new string(chars, 0, (int)Math.Min(length - 1, (uint)buffer.Length));
        }
    }

    private static List<DriverPaper> GetDriverPapers(string printer)
    {
        int count = PrintSpoolerNative.DeviceCapabilities(printer, null, PrintSpoolerNative.DC_PAPERS, IntPtr.Zero, IntPtr.Zero);
        var papers = new List<DriverPaper>();
        if (count <= 0 || count > 4096)
            return papers;

        IntPtr ids = Marshal.AllocHGlobal(count * sizeof(short));
        IntPtr sizes = Marshal.AllocHGlobal(count * 8);
        IntPtr names = Marshal.AllocHGlobal(count * PrintSpoolerNative.PaperNameLength * sizeof(char));
        try
        {
            if (PrintSpoolerNative.DeviceCapabilities(printer, null, PrintSpoolerNative.DC_PAPERS, ids, IntPtr.Zero) != count ||
                PrintSpoolerNative.DeviceCapabilities(printer, null, PrintSpoolerNative.DC_PAPERSIZE, sizes, IntPtr.Zero) != count)
            {
                return papers;
            }

            bool named = PrintSpoolerNative.DeviceCapabilities(printer, null, PrintSpoolerNative.DC_PAPERNAMES, names, IntPtr.Zero) == count;
            for (int i = 0; i < count; i++)
            {
                short id = Marshal.ReadInt16(ids, i * sizeof(short));
                int width = Marshal.ReadInt32(sizes, i * 8);
                int height = Marshal.ReadInt32(sizes, (i * 8) + 4);
                if (width <= 0 || height <= 0)
                    continue;

                // Tenths of a millimetre, portrait.
                var size = new BSize(BPaperSize.MillimetresToPoints(width / 10.0), BPaperSize.MillimetresToPoints(height / 10.0));
                BPaperSize paper;
                if (!BPaperSize.TryMatch(size, out paper))
                {
                    string name = named ? ReadPaperName(names, i) : string.Empty;
                    paper = name.Length > 0 ? new BPaperSize(name, size.Width, size.Height) : BPaperSize.Custom(size.Width, size.Height);
                }

                papers.Add(new DriverPaper(id, paper, width, height));
            }

            return papers;
        }
        finally
        {
            Marshal.FreeHGlobal(ids);
            Marshal.FreeHGlobal(sizes);
            Marshal.FreeHGlobal(names);
        }
    }

    private static string ReadPaperName(IntPtr names, int index)
    {
        // Each entry is 64 characters, NUL-terminated only when shorter.
        string raw = Marshal.PtrToStringUni(names + (index * PrintSpoolerNative.PaperNameLength * sizeof(char)), PrintSpoolerNative.PaperNameLength);
        int end = raw.IndexOf('\0');
        return (end < 0 ? raw : raw[..end]).Trim();
    }

    private static IReadOnlyList<int> GetResolutions(string printer)
    {
        int count = PrintSpoolerNative.DeviceCapabilities(printer, null, PrintSpoolerNative.DC_ENUMRESOLUTIONS, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0 || count > 256)
            return [];

        IntPtr buffer = Marshal.AllocHGlobal(count * 8);
        try
        {
            if (PrintSpoolerNative.DeviceCapabilities(printer, null, PrintSpoolerNative.DC_ENUMRESOLUTIONS, buffer, IntPtr.Zero) != count)
                return [];

            var resolutions = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
            for (int i = 0; i < count; i++)
            {
                int dpi = Math.Min(Marshal.ReadInt32(buffer, i * 8), Marshal.ReadInt32(buffer, (i * 8) + 4));
                if (dpi > 0)
                    resolutions.Add(dpi);
            }

            return [.. resolutions];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed record DriverPaper(short Id, BPaperSize Size, int WidthTenthsMm, int HeightTenthsMm);

    /// <summary>A printer's device mode in the driver's own buffer, public part and private bytes together.</summary>
    private sealed class DevMode : IDisposable
    {
        private readonly string _printer;
        private readonly int _size;

        private DevMode(string printer, IntPtr buffer, int size)
        {
            _printer = printer;
            Buffer = buffer;
            _size = size;
        }

        public IntPtr Buffer { get; private set; }

        public unsafe ref DEVMODEW Public => ref *(DEVMODEW*)Buffer;

        /// <summary>The printer's default device mode, or null when its driver will not give one.</summary>
        public static DevMode? Open(string printer)
        {
            if (!PrintSpoolerNative.OpenPrinter(printer, out IntPtr handle, IntPtr.Zero))
                return null;

            try
            {
                int size = PrintSpoolerNative.DocumentProperties(IntPtr.Zero, handle, printer, IntPtr.Zero, IntPtr.Zero, 0);
                if (size < MinimumDevModeBytes)
                    return null;

                // The public DEVMODEW is read and written through an overlay of its full size, so the
                // buffer is never smaller than that even when the driver's device mode is.
                int allocated = Math.Max(size, Marshal.SizeOf<DEVMODEW>());
                IntPtr buffer = Marshal.AllocHGlobal(allocated);
                unsafe
                {
                    new Span<byte>((void*)buffer, allocated).Clear();
                }

                if (PrintSpoolerNative.DocumentProperties(IntPtr.Zero, handle, printer, buffer, IntPtr.Zero, PrintSpoolerNative.DM_OUT_BUFFER) !=
                    PrintSpoolerNative.IDOK)
                {
                    Marshal.FreeHGlobal(buffer);
                    return null;
                }

                return new DevMode(printer, buffer, size);
            }
            finally
            {
                PrintSpoolerNative.ClosePrinter(handle);
            }
        }

        /// <summary>
        /// Writes the job's choices into the device mode and has the driver merge and validate
        /// them, so what the device context is created with is what the driver will print.
        /// </summary>
        public void Apply(UiPrintSettings settings, List<DriverPaper> papers)
        {
            ref DEVMODEW mode = ref Public;
            if (mode.Size < MinimumDevModeBytes)
                return;

            BPageSetup setup = settings.PageSetup;
            mode.Fields |= DEVMODEW.DM_ORIENTATION | DEVMODEW.DM_COPIES | DEVMODEW.DM_COLLATE | DEVMODEW.DM_COLOR | DEVMODEW.DM_DUPLEX;
            mode.Orientation = setup.Orientation == BPageOrientation.Landscape ? DEVMODEW.DMORIENT_LANDSCAPE : DEVMODEW.DMORIENT_PORTRAIT;
            mode.Copies = 1;
            mode.Collate = DEVMODEW.DMCOLLATE_FALSE;
            mode.Color = settings.ColorMode == UiPrintColorMode.Monochrome ? DEVMODEW.DMCOLOR_MONOCHROME : DEVMODEW.DMCOLOR_COLOR;
            mode.Duplex = settings.Duplex switch
            {
                UiPrintDuplex.LongEdge => DEVMODEW.DMDUP_VERTICAL,
                UiPrintDuplex.ShortEdge => DEVMODEW.DMDUP_HORIZONTAL,
                _ => DEVMODEW.DMDUP_SIMPLEX,
            };

            DriverPaper? paper = papers.Find(candidate => candidate.Size.Matches(setup.Paper.Size));
            if (paper is not null)
            {
                mode.Fields = (mode.Fields | DEVMODEW.DM_PAPERSIZE) & ~(DEVMODEW.DM_PAPERWIDTH | DEVMODEW.DM_PAPERLENGTH);
                mode.PaperSize = paper.Id;
            }
            else
            {
                mode.Fields |= DEVMODEW.DM_PAPERSIZE | DEVMODEW.DM_PAPERWIDTH | DEVMODEW.DM_PAPERLENGTH;
                mode.PaperSize = CustomPaperSize;
                mode.PaperWidth = (short)Math.Round(BPaperSize.PointsToMillimetres(setup.Paper.Width) * 10);
                mode.PaperLength = (short)Math.Round(BPaperSize.PointsToMillimetres(setup.Paper.Height) * 10);
            }

            if (settings.Dpi is double dpi)
            {
                mode.Fields |= DEVMODEW.DM_PRINTQUALITY | DEVMODEW.DM_YRESOLUTION;
                mode.PrintQuality = (short)Math.Round(dpi);
                mode.YResolution = (short)Math.Round(dpi);
            }

            if (!PrintSpoolerNative.OpenPrinter(_printer, out IntPtr handle, IntPtr.Zero))
                return;

            try
            {
                PrintSpoolerNative.DocumentProperties(IntPtr.Zero, handle, _printer, Buffer, Buffer,
                    PrintSpoolerNative.DM_IN_BUFFER | PrintSpoolerNative.DM_OUT_BUFFER);
            }
            finally
            {
                PrintSpoolerNative.ClosePrinter(handle);
            }
        }

        public void Dispose()
        {
            if (Buffer == IntPtr.Zero)
                return;

            Marshal.FreeHGlobal(Buffer);
            Buffer = IntPtr.Zero;
        }

        public override string ToString() => $"DEVMODE for {_printer} ({_size} bytes)";
    }
}
