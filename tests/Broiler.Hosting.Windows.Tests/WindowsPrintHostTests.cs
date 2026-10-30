using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using System.Text;
using Broiler.Graphics;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Paging;
using Broiler.Graphics.Text;
using Broiler.UI;
using Xunit;

namespace Broiler.Hosting.Windows.Tests;

/// <summary>
/// The Windows spooler host: the order copies print in, where a page lands on the device sheet,
/// and - against the "Microsoft Print to PDF" printer every Windows 10 and 11 install carries - a
/// real job from the spooler's printer list to a file.
/// </summary>
[SupportedOSPlatform("windows5.0")]
public sealed class WindowsPrintHostTests
{
    private const string PdfPrinter = "Microsoft Print to PDF";

    [Fact]
    public void Collated_Copies_Print_Each_Copy_Whole_And_Uncollated_Ones_Each_Page_Together()
    {
        Assert.Equal([0, 2, 0, 2], WindowsPrintHost.Order([0, 2], copies: 2, collate: true));
        Assert.Equal([0, 0, 2, 2], WindowsPrintHost.Order([0, 2], copies: 2, collate: false));
        Assert.Equal([1], WindowsPrintHost.Order([1], copies: 1, collate: false));
    }

    [Fact]
    public void A_Page_On_Its_Own_Sheet_Is_Drawn_At_The_Device_Scale()
    {
        // Letter at 300 dpi rasterized, on a 600 dpi device whose sheet is Letter.
        (double x, double y) = WindowsPrintHost.Placement(BPaperSize.Letter.Size, 300, 600, 600, 5100, 6600);

        Assert.Equal(2, x, 9);
        Assert.Equal(2, y, 9);
    }

    [Fact]
    public void A_Page_Larger_Than_The_Sheet_Is_Shrunk_To_Fit_It()
    {
        // A3 on a device that kept A4: the page is scaled by the sheet's ratio, the same on both axes.
        (double x, double y) = WindowsPrintHost.Placement(BPaperSize.A3.Size, 300, 300, 300, 2480, 3508);

        Assert.Equal(x, y, 9);
        Assert.InRange(x, 0.70, 0.71);
    }

    [Fact]
    public void The_Printer_List_Names_The_Default_At_Most_Once()
    {
        if (!OperatingSystem.IsWindows())
            return;

        IReadOnlyList<UiPrinterInfo> printers = new WindowsPrintHost().GetPrinters();

        Assert.True(printers.Count(printer => printer.IsDefault) <= 1);
        Assert.All(printers, printer => Assert.False(string.IsNullOrWhiteSpace(printer.Name)));
    }

    [Fact]
    public void The_Pdf_Printer_Reports_The_Common_Papers()
    {
        if (!OperatingSystem.IsWindows() || !HasPdfPrinter())
            return;

        UiPrinterCapabilities capabilities = new WindowsPrintHost().GetCapabilities(PdfPrinter);

        Assert.Contains(capabilities.Papers, paper => paper.Matches(BPaperSize.A4.Size));
        Assert.Contains(capabilities.Papers, paper => paper.Matches(BPaperSize.Letter.Size));
    }

    [Fact]
    public async Task A_Job_Reaches_The_Pdf_Printer_With_Every_Copy_On_The_Paper_Asked_For()
    {
        if (!OperatingSystem.IsWindows() || !HasPdfPrinter())
            return;

        string output = Path.Combine(Path.GetTempPath(), "broiler-print-test-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var setup = new BPageSetup(BPaperSize.Letter, BPageOrientation.Landscape, BPageMargins.Uniform(36));
            var job = new UiPrintJob(new TestDocument(2), new UiPrintSettings
            {
                PrinterName = PdfPrinter,
                PageSetup = setup,
                Copies = 2,
                Dpi = 150,
            });

            UiPrintResult result = await new WindowsPrintHost().PrintToFileAsync(job, output, TestContext.Current.CancellationToken);

            Assert.Equal(UiPrintOutcome.Printed, result.Outcome);
            Assert.Equal(4, result.PagesPrinted);
            Assert.True(File.Exists(output), "The PDF printer wrote nothing.");

            string pdf = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
            Assert.StartsWith("%PDF", pdf, StringComparison.Ordinal);
            Assert.Equal(4, CountPages(pdf));
            Assert.Contains("792", pdf, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task A_Range_That_Selects_Nothing_Fails_Without_Starting_A_Job()
    {
        var job = new UiPrintJob(new TestDocument(1), new UiPrintSettings
        {
            PrinterName = "Does not matter",
            PageRanges = [new UiPageRange(5, 6)],
        });

        UiPrintResult result = await new WindowsPrintHost().PrintAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(UiPrintOutcome.Failed, result.Outcome);
    }

    private static bool HasPdfPrinter() =>
        new WindowsPrintHost().GetPrinters().Any(printer => string.Equals(printer.Name, PdfPrinter, StringComparison.OrdinalIgnoreCase));

    private static int CountPages(string pdf) =>
        System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type\s*/Page(?![A-Za-z])").Count;

    private sealed class TestDocument(int pages) : IBPagedDocument
    {
        public string Title => "Broiler print test";

        public BPageSetup PreferredSetup => BPageSetup.Default;

        public IBPageSource Paginate(BPageSetup setup, CancellationToken cancellationToken = default)
        {
            var laidOut = new List<BPage>();
            for (int i = 0; i < pages; i++)
            {
                var builder = new BPageBuilder(setup.PageSize);
                builder.Content.FillRect(new BRect(setup.Margins.Left, setup.Margins.Top, 200, 100), BColor.Red);
                builder.Content.DrawText(new BTextRun($"Page {i + 1}", BFontStyle.Default with { Size = 24 }, BColor.Black),
                    new BPoint(setup.Margins.Left, setup.Margins.Top + 120));
                laidOut.Add(builder.Build());
            }

            return new BPageCollection(laidOut);
        }
    }
}
