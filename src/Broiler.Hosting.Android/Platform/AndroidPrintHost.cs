using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Android.Print;
using Android.Print.Pdf;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Paging;
using Broiler.Graphics.RenderList;
using Broiler.UI;

namespace Broiler.Hosting.Android;

/// <summary>
/// Printing through Android's print framework. The system print UI picks the printer, the paper
/// and the pages, and previews them; it asks the document for each page through a
/// <see cref="PrintDocumentAdapter"/>, which lays the document out for the paper chosen and
/// draws every page as vector content into a <see cref="PrintedPdfDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// No raster step: the PDF page's canvas is an <c>android.graphics.Canvas</c> in points, and
/// <see cref="AndroidCanvasRenderer"/> already replays a page's commands onto that kind of
/// canvas. Text stays text and the PDF the print service receives is as sharp as the printer.
/// </para>
/// <para>
/// <see cref="ShowsSystemPrintDialog"/> is true, so <c>UiPrintDialog.PrintAsync</c> hands the
/// document straight here: Broiler's dialog would only ask what the system UI asks again.
/// </para>
/// </remarks>
public sealed class AndroidPrintHost : IUiPrintHost
{
    private const double MilsPerInch = 1000.0;

    private readonly Context _context;

    /// <param name="context">An activity: the print UI is shown over it.</param>
    public AndroidPrintHost(Context context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public bool ShowsSystemPrintDialog => true;

    /// <summary>The print service lists the printers in its own UI, so there are none to list here.</summary>
    public IReadOnlyList<UiPrinterInfo> GetPrinters() => [];

    public UiPrinterCapabilities GetCapabilities(string printerName) => UiPrinterCapabilities.Unknown;

    public Task<UiPrintResult> PrintAsync(UiPrintJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_context.GetSystemService(Context.PrintService) is not PrintManager manager)
            return Task.FromResult(UiPrintResult.Failed("This device has no print service."));

        var adapter = new BroilerPrintDocumentAdapter(_context, job, cancellationToken);
        PrintAttributes attributes = new PrintAttributes.Builder()
            .SetMediaSize(ToMediaSize(job.Settings.PageSetup))!
            .Build()!;

        manager.Print(job.Title, adapter, attributes);
        return adapter.Completion;
    }

    /// <summary>The media size the print UI starts on: the job's paper, turned the way its setup says.</summary>
    internal static PrintAttributes.MediaSize ToMediaSize(BPageSetup setup)
    {
        BPaperSize paper = setup.Paper;
        PrintAttributes.MediaSize? known =
            paper.Matches(BPaperSize.A3.Size) ? PrintAttributes.MediaSize.IsoA3 :
            paper.Matches(BPaperSize.A4.Size) ? PrintAttributes.MediaSize.IsoA4 :
            paper.Matches(BPaperSize.A5.Size) ? PrintAttributes.MediaSize.IsoA5 :
            paper.Matches(BPaperSize.A6.Size) ? PrintAttributes.MediaSize.IsoA6 :
            paper.Matches(BPaperSize.B4.Size) ? PrintAttributes.MediaSize.IsoB4 :
            paper.Matches(BPaperSize.B5.Size) ? PrintAttributes.MediaSize.IsoB5 :
            paper.Matches(BPaperSize.JisB4.Size) ? PrintAttributes.MediaSize.JisB4 :
            paper.Matches(BPaperSize.JisB5.Size) ? PrintAttributes.MediaSize.JisB5 :
            paper.Matches(BPaperSize.Letter.Size) ? PrintAttributes.MediaSize.NaLetter :
            paper.Matches(BPaperSize.Legal.Size) ? PrintAttributes.MediaSize.NaLegal :
            paper.Matches(BPaperSize.Tabloid.Size) ? PrintAttributes.MediaSize.NaTabloid :
            null;

        PrintAttributes.MediaSize size = known ?? new PrintAttributes.MediaSize(
            "broiler_" + paper.Name.Replace(' ', '_'),
            paper.Name,
            ToMils(paper.Width),
            ToMils(paper.Height));

        return setup.Orientation == BPageOrientation.Landscape ? size.AsLandscape()! : size.AsPortrait()!;
    }

    /// <summary>The page setup a print UI's attributes describe, keeping the document's margins where they are wider than the printer's.</summary>
    internal static BPageSetup ToSetup(PrintAttributes attributes, BPageMargins documentMargins)
    {
        PrintAttributes.MediaSize media = attributes.GetMediaSize() ?? PrintAttributes.MediaSize.IsoA4!;
        var size = new BSize(ToPoints(media.WidthMils), ToPoints(media.HeightMils));
        PrintAttributes.Margins? minimum = attributes.MinMargins;
        BPageMargins margins = minimum is null
            ? documentMargins
            : documentMargins.AtLeast(new BPageMargins(ToPoints(minimum.LeftMils), ToPoints(minimum.TopMils), ToPoints(minimum.RightMils),
                ToPoints(minimum.BottomMils)));

        return BPageSetup.ForPageSize(size, margins);
    }

    internal static BPageMargins MinimumMargins(PrintAttributes attributes)
    {
        PrintAttributes.Margins? minimum = attributes.MinMargins;
        return minimum is null
            ? BPageMargins.None
            : new BPageMargins(ToPoints(minimum.LeftMils), ToPoints(minimum.TopMils), ToPoints(minimum.RightMils), ToPoints(minimum.BottomMils));
    }

    private static double ToPoints(int mils) => mils * BPaperSize.PointsPerInch / MilsPerInch;

    private static int ToMils(double points) => (int)Math.Round(points * MilsPerInch / BPaperSize.PointsPerInch);

    /// <summary>Lays the document out for the paper the print UI chose and writes the pages it asks for.</summary>
    private sealed class BroilerPrintDocumentAdapter : PrintDocumentAdapter
    {
        private readonly Context _context;
        private readonly UiPrintJob _job;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<UiPrintResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private PrintAttributes? _attributes;
        private BPageSetup? _setup;
        private IBPageSource? _pages;
        private int _pagesWritten;
        private string? _failure;

        public BroilerPrintDocumentAdapter(Context context, UiPrintJob job, CancellationToken cancellationToken)
        {
            _context = context;
            _job = job;
            _cancellationToken = cancellationToken;
        }

        public Task<UiPrintResult> Completion => _completion.Task;

        public override void OnLayout(PrintAttributes? oldAttributes, PrintAttributes? newAttributes, CancellationSignal? cancellationSignal,
            LayoutResultCallback? callback, Bundle? extras)
        {
            if (callback is null || newAttributes is null)
                return;

            if (cancellationSignal?.IsCanceled == true || _cancellationToken.IsCancellationRequested)
            {
                callback.OnLayoutCancelled();
                return;
            }

            try
            {
                BPageSetup setup = ToSetup(newAttributes, _job.Settings.PageSetup.Margins);
                bool changed = _setup is null || _setup != setup;
                if (changed || _pages is null)
                {
                    // The pages a Broiler dialog previewed fit only the setup they were laid out for.
                    _pages = _job.Pages is not null && setup == _job.Settings.PageSetup
                        ? _job.Pages
                        : _job.Document.Paginate(setup, _cancellationToken);
                    _setup = setup;
                }

                _attributes = newAttributes;
                PrintDocumentInfo info = new PrintDocumentInfo.Builder(FileName(_job.Title))
                    .SetContentType(PrintContentType.Document)!
                    .SetPageCount(_pages.PageCount)!
                    .Build()!;
                callback.OnLayoutFinished(info, changed);
            }
            catch (System.OperationCanceledException)
            {
                callback.OnLayoutCancelled();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _failure = exception.Message;
                callback.OnLayoutFailed(exception.Message);
            }
        }

        public override void OnWrite(PageRange[]? pages, ParcelFileDescriptor? destination, CancellationSignal? cancellationSignal,
            WriteResultCallback? callback)
        {
            if (callback is null || destination is null || _pages is null || _attributes is null)
                return;

            try
            {
                using var document = new PrintedPdfDocument(_context, _attributes);
                using var renderer = new AndroidCanvasRenderer();
                using var images = BPageImageCache.For(renderer);
                BPageMargins offset = MinimumMargins(_attributes);
                var written = new List<PageRange>();
                int count = 0;

                for (int index = 0; index < _pages.PageCount; index++)
                {
                    if (cancellationSignal?.IsCanceled == true || _cancellationToken.IsCancellationRequested)
                    {
                        callback.OnWriteCancelled();
                        return;
                    }

                    if (!Contains(pages, index))
                        continue;

                    var page = document.StartPage(index)!;
                    try
                    {
                        // The page canvas starts at the printable area's corner; the page is laid out on the whole sheet.
                        var list = new BRenderList();
                        list.PushTransform(Broiler.Graphics.Geometry.BMatrix3x2.Translation(-offset.Left, -offset.Top));
                        images.AppendPage(_pages.GetPage(index), list);
                        list.PopTransform();
                        renderer.Draw(page.Canvas!, list);
                    }
                    finally
                    {
                        document.FinishPage(page);
                    }

                    written.Add(new PageRange(index, index));
                    count++;
                }

                using (var output = new Java.IO.FileOutputStream(destination.FileDescriptor))
                using (var stream = new global::Android.Runtime.OutputStreamInvoker(output))
                    document.WriteTo(stream);

                _pagesWritten = count;
                callback.OnWriteFinished([.. written]);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _failure = exception.Message;
                callback.OnWriteFailed(exception.Message);
            }
        }

        public override void OnFinish()
        {
            base.OnFinish();
            _completion.TrySetResult(_failure is not null
                ? UiPrintResult.Failed(_failure)
                : _pagesWritten > 0 ? UiPrintResult.Printed(_pagesWritten) : UiPrintResult.Cancelled);
        }

        private static bool Contains(PageRange[]? ranges, int index)
        {
            if (ranges is null || ranges.Length == 0)
                return true;

            foreach (PageRange range in ranges)
            {
                // PageRange.AllPages is 0 to int.MaxValue.
                if (index >= range.Start && index <= range.End)
                    return true;
            }

            return false;
        }

        private static string FileName(string title)
        {
            string name = string.IsNullOrWhiteSpace(title) ? "Document" : title;
            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name : name + ".pdf";
        }
    }
}
