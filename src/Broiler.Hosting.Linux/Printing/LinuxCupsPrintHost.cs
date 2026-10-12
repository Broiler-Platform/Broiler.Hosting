using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Paging;
using Broiler.Native;
using Broiler.Native.Linux.Printing;
using Broiler.UI;

namespace Broiler.Hosting.Linux;

/// <summary>
/// Printing through CUPS: the destinations the user's scheduler offers, the media each takes,
/// and jobs submitted as a file in the format CUPS prints everywhere, PDF.
/// </summary>
/// <remarks>
/// <para>
/// The PDF is written by the <see cref="IBPageDocumentWriter"/> the application composes - in
/// practice <c>Broiler.Documents.Pdf</c>'s raster spool writer - so this host turns pages into a
/// file without depending on a document codec. The pages selected are the only pages written;
/// CUPS is told the copies, the medium, the sides and the colour, and nothing about ranges.
/// </para>
/// <para>
/// libcups is not on every image. <see cref="IsAvailable"/> says whether it is, and a host
/// built without it answers no printers rather than failing.
/// </para>
/// </remarks>
public sealed class LinuxCupsPrintHost : IUiPrintHost
{
    private readonly IBPageDocumentWriter _spoolWriter;

    /// <param name="spoolWriter">Writes the pages CUPS is handed; its media type must be <c>application/pdf</c>.</param>
    public LinuxCupsPrintHost(IBPageDocumentWriter spoolWriter)
    {
        ArgumentNullException.ThrowIfNull(spoolWriter);
        if (!string.Equals(spoolWriter.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CUPS is handed PDF; the spool writer must write application/pdf.", nameof(spoolWriter));

        _spoolWriter = spoolWriter;
    }

    /// <summary>True on Linux with the CUPS client library installed.</summary>
    public static bool IsAvailable => OperatingSystem.IsLinux() && NativeLibraryProbe.IsAvailable(CupsNative.Library);

    public bool ShowsSystemPrintDialog => false;

    public IReadOnlyList<UiPrinterInfo> GetPrinters()
    {
        if (!IsAvailable)
            return [];

        var printers = new List<UiPrinterInfo>();
        WithDestinations((count, destinations) =>
        {
            int stride = Marshal.SizeOf<CupsDest>();
            for (int i = 0; i < count; i++)
            {
                var dest = Marshal.PtrToStructure<CupsDest>(destinations + (i * stride));
                string? name = DestinationName(dest);
                if (name is null)
                    continue;

                printers.Add(new UiPrinterInfo(
                    name,
                    IsDefault: dest.IsDefault != 0,
                    Location: Option(dest, "printer-location"),
                    Description: Option(dest, "printer-info")));
            }
        });

        return printers;
    }

    public UiPrinterCapabilities GetCapabilities(string printerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        if (!IsAvailable)
            return UiPrinterCapabilities.Unknown;

        UiPrinterCapabilities capabilities = UiPrinterCapabilities.Unknown;
        WithDestinations((count, destinations) =>
        {
            IntPtr dest = Find(count, destinations, printerName);
            if (dest == IntPtr.Zero)
                return;

            IntPtr info = CupsNative.CopyDestInfo(IntPtr.Zero, dest);
            if (info == IntPtr.Zero)
                return;

            try
            {
                var papers = new List<BPaperSize>();
                int media = CupsNative.GetDestMediaCount(IntPtr.Zero, dest, info, CupsNative.CUPS_MEDIA_FLAGS_DEFAULT);
                for (int i = 0; i < Math.Min(media, 512); i++)
                {
                    if (CupsNative.GetDestMediaByIndex(IntPtr.Zero, dest, info, i, CupsNative.CUPS_MEDIA_FLAGS_DEFAULT, out CupsSize size) != 0 &&
                        ToPaper(size) is BPaperSize paper && !papers.Exists(existing => existing.Matches(paper.Size)))
                    {
                        papers.Add(paper);
                    }
                }

                BPaperSize? defaultPaper = CupsNative.GetDestMediaDefault(IntPtr.Zero, dest, info, CupsNative.CUPS_MEDIA_FLAGS_DEFAULT, out CupsSize defaultSize) != 0
                    ? ToPaper(defaultSize)
                    : null;

                capabilities = new UiPrinterCapabilities
                {
                    Papers = papers.Count > 0 ? papers : BPaperSize.Standard,
                    DefaultPaper = defaultPaper,
                    SupportsColor = CupsNative.CheckDestSupported(IntPtr.Zero, dest, info, "print-color-mode", "color") != 0,
                    SupportsDuplex = CupsNative.CheckDestSupported(IntPtr.Zero, dest, info, "sides", "two-sided-long-edge") != 0,
                };
            }
            finally
            {
                CupsNative.FreeDestInfo(info);
            }
        });

        return capabilities;
    }

    public Task<UiPrintResult> PrintAsync(UiPrintJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Task.Run(() => Print(job, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The CUPS options a job is submitted with. Page ranges are not among them: only the
    /// selected pages are written, so the file is already the range.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> Options(UiPrintSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = new List<KeyValuePair<string, string>>
        {
            new("copies", settings.Copies.ToString(CultureInfo.InvariantCulture)),
            new("multiple-document-handling", settings.Collate ? "separate-documents-collated-copies" : "separate-documents-uncollated-copies"),
            new("media", MediaName(settings.PageSetup.Paper)),
            new("sides", settings.Duplex switch
            {
                UiPrintDuplex.LongEdge => "two-sided-long-edge",
                UiPrintDuplex.ShortEdge => "two-sided-short-edge",
                _ => "one-sided",
            }),
            new("print-color-mode", settings.ColorMode == UiPrintColorMode.Monochrome ? "monochrome" : "color"),
        };

        if (settings.Dpi is double dpi)
            options.Add(new("printer-resolution", ((int)Math.Round(dpi)).ToString(CultureInfo.InvariantCulture) + "dpi"));

        return options;
    }

    /// <summary>
    /// The PWG 5101.1 self-describing name for a sheet: the standard name where the catalogue has
    /// one, and a <c>custom_</c> name in millimetres where it has not.
    /// </summary>
    internal static string MediaName(BPaperSize paper)
    {
        static string Mm(double points) => BPaperSize.PointsToMillimetres(points).ToString("0.##", CultureInfo.InvariantCulture);

        if (paper.Matches(BPaperSize.A3.Size)) return "iso_a3_297x420mm";
        if (paper.Matches(BPaperSize.A4.Size)) return "iso_a4_210x297mm";
        if (paper.Matches(BPaperSize.A5.Size)) return "iso_a5_148x210mm";
        if (paper.Matches(BPaperSize.A6.Size)) return "iso_a6_105x148mm";
        if (paper.Matches(BPaperSize.B4.Size)) return "iso_b4_250x353mm";
        if (paper.Matches(BPaperSize.B5.Size)) return "iso_b5_176x250mm";
        if (paper.Matches(BPaperSize.JisB4.Size)) return "jis_b4_257x364mm";
        if (paper.Matches(BPaperSize.JisB5.Size)) return "jis_b5_182x257mm";
        if (paper.Matches(BPaperSize.Letter.Size)) return "na_letter_8.5x11in";
        if (paper.Matches(BPaperSize.Legal.Size)) return "na_legal_8.5x14in";
        if (paper.Matches(BPaperSize.Tabloid.Size)) return "na_ledger_11x17in";
        if (paper.Matches(BPaperSize.Executive.Size)) return "na_executive_7.25x10.5in";
        if (paper.Matches(BPaperSize.Statement.Size)) return "na_invoice_5.5x8.5in";

        return $"custom_{Mm(paper.Width)}x{Mm(paper.Height)}mm_{Mm(paper.Width)}x{Mm(paper.Height)}mm";
    }

    /// <summary>A cups_size_t as a sheet, or null for one with no size.</summary>
    internal static BPaperSize? ToPaper(CupsSize size)
    {
        if (size.Width <= 0 || size.Length <= 0)
            return null;

        // Hundredths of a millimetre.
        var points = new BSize(BPaperSize.MillimetresToPoints(size.Width / 100.0), BPaperSize.MillimetresToPoints(size.Length / 100.0));
        if (BPaperSize.TryMatch(points, out BPaperSize standard))
            return standard;

        string name = size.GetMediaName();
        return string.IsNullOrWhiteSpace(name) ? BPaperSize.Custom(points.Width, points.Height) : new BPaperSize(name, points.Width, points.Height);
    }

    private UiPrintResult Print(UiPrintJob job, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return UiPrintResult.Failed("CUPS is not installed.");

        string? printer = job.Settings.PrinterName ?? DefaultPrinter();
        if (string.IsNullOrWhiteSpace(printer))
            return UiPrintResult.Failed("No printer is installed.");

        IBPageSource pages = job.ResolvePages(cancellationToken);
        IReadOnlyList<int> selected = job.Settings.SelectPages(pages.PageCount);
        if (selected.Count == 0)
            return UiPrintResult.Failed("The page range selects no pages.");

        string spool = Path.Combine(Path.GetTempPath(), "broiler-print-" + Guid.NewGuid().ToString("N") + _spoolWriter.FileExtension);
        try
        {
            using (var file = new FileStream(spool, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                _spoolWriter.Write(pages.Select(selected), job.Title, file, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            return Submit(printer, spool, job, selected.Count);
        }
        catch (OperationCanceledException)
        {
            return UiPrintResult.Cancelled;
        }
        catch (IOException exception)
        {
            return UiPrintResult.Failed("The print file could not be written: " + exception.Message);
        }
        finally
        {
            // cupsPrintFile2 has sent the file to the scheduler by the time it returns.
            try
            {
                File.Delete(spool);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static UiPrintResult Submit(string printer, string spool, UiPrintJob job, int pages)
    {
        // An instance is a saved set of options on a queue; the job goes to the queue.
        int slash = printer.IndexOf('/', StringComparison.Ordinal);
        string queue = slash < 0 ? printer : printer[..slash];

        IntPtr options = IntPtr.Zero;
        int count = 0;
        try
        {
            foreach (KeyValuePair<string, string> option in Options(job.Settings))
                count = CupsNative.AddOption(option.Key, option.Value, count, ref options);

            int jobId = CupsNative.PrintFile(IntPtr.Zero, queue, spool, job.Title, count, options);
            if (jobId == 0)
                return UiPrintResult.Failed("CUPS refused the job: " + (Marshal.PtrToStringUTF8(CupsNative.LastErrorString()) ?? "no reason given") + ".");

            return UiPrintResult.Printed(pages * job.Settings.Copies);
        }
        finally
        {
            if (options != IntPtr.Zero)
                CupsNative.FreeOptions(count, options);
        }
    }

    private static string? DefaultPrinter()
    {
        string? name = null;
        WithDestinations((count, destinations) =>
        {
            int stride = Marshal.SizeOf<CupsDest>();
            for (int i = 0; i < count && name is null; i++)
            {
                var dest = Marshal.PtrToStructure<CupsDest>(destinations + (i * stride));
                if (dest.IsDefault != 0)
                    name = DestinationName(dest);
            }
        });

        return name;
    }

    private static void WithDestinations(Action<int, IntPtr> use)
    {
        int count = CupsNative.GetDests(IntPtr.Zero, out IntPtr destinations);
        try
        {
            if (count > 0 && destinations != IntPtr.Zero)
                use(count, destinations);
        }
        finally
        {
            if (destinations != IntPtr.Zero)
                CupsNative.FreeDests(count, destinations);
        }
    }

    private static IntPtr Find(int count, IntPtr destinations, string printerName)
    {
        int stride = Marshal.SizeOf<CupsDest>();
        for (int i = 0; i < count; i++)
        {
            IntPtr pointer = destinations + (i * stride);
            if (string.Equals(DestinationName(Marshal.PtrToStructure<CupsDest>(pointer)), printerName, StringComparison.Ordinal))
                return pointer;
        }

        return IntPtr.Zero;
    }

    /// <summary>A destination's name as CUPS prints to it: <c>name</c>, or <c>name/instance</c>.</summary>
    private static string? DestinationName(CupsDest dest)
    {
        string? name = Marshal.PtrToStringUTF8(dest.Name);
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string? instance = Marshal.PtrToStringUTF8(dest.Instance);
        return string.IsNullOrEmpty(instance) ? name : name + "/" + instance;
    }

    private static string? Option(CupsDest dest, string name)
    {
        string? value = Marshal.PtrToStringUTF8(CupsNative.GetOption(name, dest.OptionCount, dest.Options));
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
