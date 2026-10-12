using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Broiler.Graphics.Paging;
using Broiler.Hosting.Linux;
using Broiler.Native.Linux.Printing;
using Broiler.UI;
using Xunit;

namespace Broiler.Hosting.Linux.Tests;

/// <summary>
/// The CUPS host's own decisions - the options a job is submitted with and the PWG names of
/// the papers - which hold whether or not libcups is on the machine running the tests.
/// </summary>
public sealed class LinuxCupsPrintHostTests
{
    [Fact]
    public void A_Job_Asks_CUPS_For_Its_Copies_Medium_Sides_And_Colour()
    {
        var settings = new UiPrintSettings
        {
            Copies = 3,
            Collate = false,
            PageSetup = BPageSetup.Default with { Paper = BPaperSize.Letter },
            Duplex = UiPrintDuplex.ShortEdge,
            ColorMode = UiPrintColorMode.Monochrome,
            Dpi = 600,
        };

        Dictionary<string, string> options = LinuxCupsPrintHost.Options(settings).ToDictionary(option => option.Key, option => option.Value);

        Assert.Equal("3", options["copies"]);
        Assert.Equal("separate-documents-uncollated-copies", options["multiple-document-handling"]);
        Assert.Equal("na_letter_8.5x11in", options["media"]);
        Assert.Equal("two-sided-short-edge", options["sides"]);
        Assert.Equal("monochrome", options["print-color-mode"]);
        Assert.Equal("600dpi", options["printer-resolution"]);
        Assert.False(options.ContainsKey("page-ranges"), "Only the selected pages are written, so CUPS is not given a range.");
    }

    [Fact]
    public void The_Defaults_Ask_For_One_Collated_Colour_Copy_On_One_Side()
    {
        Dictionary<string, string> options = LinuxCupsPrintHost.Options(new UiPrintSettings()).ToDictionary(option => option.Key, option => option.Value);

        Assert.Equal("1", options["copies"]);
        Assert.Equal("separate-documents-collated-copies", options["multiple-document-handling"]);
        Assert.Equal("iso_a4_210x297mm", options["media"]);
        Assert.Equal("one-sided", options["sides"]);
        Assert.Equal("color", options["print-color-mode"]);
        Assert.False(options.ContainsKey("printer-resolution"));
    }

    [Theory]
    [InlineData("A3", "iso_a3_297x420mm")]
    [InlineData("JIS B5", "jis_b5_182x257mm")]
    [InlineData("Legal", "na_legal_8.5x14in")]
    [InlineData("Tabloid", "na_ledger_11x17in")]
    public void A_Catalogue_Paper_Has_Its_PWG_Name(string paper, string media)
    {
        Assert.True(BPaperSize.TryFind(paper, out BPaperSize size));
        Assert.Equal(media, LinuxCupsPrintHost.MediaName(size));
    }

    [Fact]
    public void A_Paper_Outside_The_Catalogue_Gets_A_Custom_PWG_Name()
    {
        BPaperSize card = BPaperSize.FromMillimetres("Card", 100, 150);

        Assert.Equal("custom_100x150mm_100x150mm", LinuxCupsPrintHost.MediaName(card));
    }

    [Fact]
    public unsafe void A_CUPS_Medium_Is_Read_From_Hundredths_Of_A_Millimetre()
    {
        var a4 = new CupsSize { Width = 21000, Length = 29700 };
        Assert.Equal(BPaperSize.A4, LinuxCupsPrintHost.ToPaper(a4));

        var odd = new CupsSize { Width = 10000, Length = 15000 };
        byte[] name = Encoding.UTF8.GetBytes("om_card_100x150mm");
        for (int i = 0; i < name.Length; i++)
            odd.Media[i] = name[i];
        BPaperSize? card = LinuxCupsPrintHost.ToPaper(odd);
        Assert.NotNull(card);
        Assert.Equal("om_card_100x150mm", card!.Value.Name);
        Assert.Equal(BPaperSize.MillimetresToPoints(100), card.Value.Width, 6);

        Assert.Null(LinuxCupsPrintHost.ToPaper(default));
    }

    [Fact]
    public void Without_CUPS_There_Are_No_Printers_And_Nothing_Throws()
    {
        if (LinuxCupsPrintHost.IsAvailable)
            return;

        var host = new LinuxCupsPrintHost(new PdfLikeWriter());

        Assert.Empty(host.GetPrinters());
        Assert.Same(UiPrinterCapabilities.Unknown, host.GetCapabilities("anything"));
        Assert.False(host.ShowsSystemPrintDialog);
    }

    [Fact]
    public void Only_A_PDF_Spool_Writer_Is_Accepted()
    {
        Assert.Throws<ArgumentException>(() => new LinuxCupsPrintHost(new PdfLikeWriter("image/png")));
        Assert.Throws<ArgumentNullException>(() => new LinuxCupsPrintHost(null!));
    }

    private sealed class PdfLikeWriter(string mediaType = "application/pdf") : IBPageDocumentWriter
    {
        public string MediaType => mediaType;

        public string FileExtension => ".pdf";

        public void Write(IEnumerable<BPage> pages, string title, Stream output, CancellationToken cancellationToken = default)
        {
        }
    }
}
