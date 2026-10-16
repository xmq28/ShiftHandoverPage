using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using ShiftHandover.Web.Models;

namespace ShiftHandover.Web.Services;

public interface IShiftReportPdfGenerator
{
    byte[] Generate(
        Shift shift,
        IReadOnlyCollection<Accident> accidents,
        IReadOnlyCollection<Incident> incidents,
        IReadOnlyCollection<ManpowerRecord> manpowerRecords);
}

public sealed class ShiftReportPdfGenerator : IShiftReportPdfGenerator
{
    private const double Left = 42;
    private const double Right = 42;
    private const double Top = 42;
    private const double Footer = 38;
    private const double LabelColumn = 112;
    private const double CellPadding = 6;
    private const double LineHeight = 12;
    private const double HeaderHeight = 23;

    private static readonly XColor Ink = XColor.FromArgb(38, 54, 61);
    private static readonly XColor Accent = XColor.FromArgb(18, 105, 111);
    private static readonly XColor Grid = XColor.FromArgb(211, 221, 223);
    private static readonly XColor Stripe = XColor.FromArgb(243, 247, 247);

    static ShiftReportPdfGenerator()
    {
        if (OperatingSystem.IsWindows())
        {
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        }
    }

    public byte[] Generate(
        Shift shift,
        IReadOnlyCollection<Accident> accidents,
        IReadOnlyCollection<Incident> incidents,
        IReadOnlyCollection<ManpowerRecord> manpowerRecords)
    {
        using var document = new PdfDocument();
        document.Info.Title = $"Shift handover report - {shift.Title}";
        document.Info.Subject = $"Closed shift {shift.Id}";
        document.Info.Creator = "Shift Handover";

        using var report = new ReportCanvas(document, shift.Title);
        report.DrawTitle();
        report.DrawSection("Shift summary", [new ReportRow[]
        {
            new("Shift", shift.Title),
            new("Shift date", shift.ShiftDate.ToString("dddd, dd MMMM yyyy")),
            new("Shift hours", $"{shift.StartTime:HH:mm} - {shift.EndTime:HH:mm} Bahrain (UTC+03:00)"),
            new("Status", shift.Status.ToString()),
            new("Assigned supervisor", DisplayName(shift.ClaimedBySupervisor)),
            new("Claimed at", shift.ClaimedAt is { } claimedAt ? BahrainTime.Format(claimedAt) : "Not recorded"),
            new("Closed at", shift.ClosedAt is { } closedAt ? BahrainTime.Format(closedAt) : "Not recorded")
        }]);

        report.DrawSection("Accidents", accidents.Count == 0
            ? SingleGroup("No accident records.")
            : accidents.OrderBy(record => record.OccurredAt).Select((record, index) => new ReportRow[]
            {
                new("Record", $"Accident {index + 1}"),
                new("Date and time", BahrainTime.Format(record.OccurredAt)),
                new("Severity", record.Severity.ToString()),
                new("Location", record.Location),
                new("Description", record.Description),
                new("Injured person", Value(record.InjuredPerson)),
                new("Action taken", Value(record.ActionTaken)),
                new("Recorded by", DisplayName(record.RecordedBySupervisor))
            }).ToArray());

        report.DrawSection("Incidents", incidents.Count == 0
            ? SingleGroup("No incident records.")
            : incidents.OrderBy(record => record.OccurredAt).Select((record, index) => new ReportRow[]
            {
                new("Record", $"Incident {index + 1}"),
                new("Date and time", BahrainTime.Format(record.OccurredAt)),
                new("Category", record.Category),
                new("Description", record.Description),
                new("Resolved", YesNo(record.Resolved)),
                new("Follow-up required", YesNo(record.FollowUpRequired)),
                new("Recorded by", DisplayName(record.RecordedBySupervisor))
            }).ToArray());

        report.DrawSection("Manpower", manpowerRecords.Count == 0
            ? SingleGroup("No manpower records.")
            : manpowerRecords.OrderBy(record => record.Department).ThenBy(record => record.Role)
                .Select((record, index) => new ReportRow[]
                {
                    new("Record", $"Manpower entry {index + 1}"),
                    new("Department", record.Department),
                    new("Role", record.Role),
                    new("Required", record.Required.ToString()),
                    new("Present", record.Present.ToString()),
                    new("Absent", record.Absent.ToString()),
                    new("Notes", Value(record.Notes)),
                    new("Recorded by", DisplayName(record.RecordedBySupervisor))
                }).ToArray());

        report.AddPageNumbers();
        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    private static string DisplayName(ApplicationUser? user) =>
        string.IsNullOrWhiteSpace(user?.FullName)
            ? string.IsNullOrWhiteSpace(user?.Email) ? "Unknown supervisor" : user.Email
            : user.FullName;

    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "Not provided" : value;

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static ReportRow[][] SingleGroup(string message) => [[new("Records", message)]];

    private sealed record ReportRow(string Label, string Value);

    private sealed class ReportCanvas : IDisposable
    {
        private readonly PdfDocument _document;
        private readonly string _shiftTitle;
        private string _sectionTitle = string.Empty;
        private readonly XFont _titleFont = new("Arial", 21, XFontStyleEx.Bold);
        private readonly XFont _sectionFont = new("Arial", 12, XFontStyleEx.Bold);
        private readonly XFont _headerFont = new("Arial", 9, XFontStyleEx.Bold);
        private readonly XFont _bodyFont = new("Arial", 9, XFontStyleEx.Regular);
        private readonly XFont _smallFont = new("Arial", 8, XFontStyleEx.Regular);
        private PdfPage _page = null!;
        private XGraphics _graphics = null!;
        private double _y;
        private int _rowIndex;

        public ReportCanvas(PdfDocument document, string shiftTitle)
        {
            _document = document;
            _shiftTitle = shiftTitle;
            AddPage(firstPage: true);
        }

        public void DrawTitle()
        {
            _graphics.DrawString("SHIFT HANDOVER REPORT", _titleFont, new XSolidBrush(Ink), new XPoint(Left, _y));
            _y += 31;
            foreach (var line in Wrap(_shiftTitle, _bodyFont, _page.Width.Point - Left - Right))
            {
                _graphics.DrawString(line, _bodyFont, new XSolidBrush(Accent), new XPoint(Left, _y));
                _y += 13;
            }

            _y += 2;
            _graphics.DrawLine(new XPen(Accent, 2), Left, _y, _page.Width.Point - Right, _y);
            _y += 17;
        }

        public void DrawSection(string title, IReadOnlyList<ReportRow[]> groups)
        {
            EnsureSpace(25 + HeaderHeight + LineHeight + 2 * CellPadding);
            _sectionTitle = title;
            _graphics.DrawString(title, _sectionFont, new XSolidBrush(Accent), new XPoint(Left, _y + 13));
            _y += 24;
            DrawTableHeader();

            foreach (var group in groups)
            {
                EnsureGroupFits(group);
                foreach (var row in group)
                {
                    DrawRow(row);
                }

                if (_y + 7 <= ContentBottom)
                {
                    _graphics.DrawLine(new XPen(Grid, 0.7), Left, _y + 2, _page.Width.Point - Right, _y + 2);
                    _y += 7;
                }
            }

            _y += 14;
        }

        public void AddPageNumbers()
        {
            _graphics.Dispose();
            _graphics = null!;
            for (var index = 0; index < _document.Pages.Count; index++)
            {
                var page = _document.Pages[index];
                using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var footerY = page.Height.Point - Footer;
                graphics.DrawLine(new XPen(Grid, 0.7), Left, footerY, page.Width.Point - Right, footerY);
                graphics.DrawString(
                    $"Shift handover | {BahrainTime.Format(DateTimeOffset.UtcNow)}",
                    _smallFont,
                    new XSolidBrush(Ink),
                    new XRect(Left, footerY + 7, page.Width.Point - Left - Right - 100, 14),
                    XStringFormats.TopLeft);
                graphics.DrawString(
                    $"Page {index + 1} of {_document.Pages.Count}",
                    _smallFont,
                    new XSolidBrush(Ink),
                    new XRect(page.Width.Point - Right - 100, footerY + 7, 100, 14),
                    XStringFormats.TopRight);
            }
        }

        public void Dispose() => _graphics?.Dispose();

        private void DrawRow(ReportRow row)
        {
            var tableWidth = _page.Width.Point - Left - Right;
            var labelWidth = LabelColumn;
            var valueWidth = tableWidth - labelWidth;
            var labelLines = Wrap(row.Label, _headerFont, labelWidth - CellPadding * 2).ToArray();
            var valueLines = Wrap(row.Value, _bodyFont, valueWidth - CellPadding * 2).ToArray();
            var lineCount = Math.Max(labelLines.Length, valueLines.Length);
            var position = 0;

            while (position < lineCount)
            {
                var availableLines = (int)Math.Floor((ContentBottom - _y - CellPadding * 2) / LineHeight);
                if (availableLines <= 0)
                {
                    AddPage(firstPage: false);
                    DrawContinuationHeader();
                    continue;
                }

                var linesOnPage = Math.Min(lineCount - position, availableLines);
                var rowHeight = linesOnPage * LineHeight + CellPadding * 2;
                var background = _rowIndex++ % 2 == 0 ? XBrushes.White : new XSolidBrush(Stripe);
                var tableX = Left;
                var rowY = _y;

                _graphics.DrawRectangle(background, tableX, rowY, tableWidth, rowHeight);
                _graphics.DrawRectangle(new XPen(Grid, 0.45), tableX, rowY, labelWidth, rowHeight);
                _graphics.DrawRectangle(new XPen(Grid, 0.45), tableX + labelWidth, rowY, valueWidth, rowHeight);

                var label = position == 0 ? labelLines : Wrap($"{row.Label} (continued)", _headerFont, labelWidth - CellPadding * 2).ToArray();
                DrawLines(
                    label,
                    0,
                    Math.Min(linesOnPage, label.Length),
                    _headerFont,
                    tableX + CellPadding,
                    rowY + CellPadding,
                    labelWidth - CellPadding * 2);
                DrawLines(
                    valueLines,
                    position,
                    linesOnPage,
                    _bodyFont,
                    tableX + labelWidth + CellPadding,
                    rowY + CellPadding,
                    valueWidth - CellPadding * 2);

                _y += rowHeight;
                position += linesOnPage;
                if (position < lineCount)
                {
                    AddPage(firstPage: false);
                    DrawContinuationHeader();
                }
            }
        }

        private void EnsureGroupFits(IReadOnlyCollection<ReportRow> group)
        {
            var groupHeight = group.Sum(EstimateRowHeight) + 7;
            var remainingHeight = ContentBottom - _y;
            var freshPageHeight = ContentBottom - ContinuationContentTop - HeaderHeight;
            if (groupHeight <= freshPageHeight && groupHeight > remainingHeight)
            {
                AddPage(firstPage: false);
                DrawContinuationHeader();
            }
        }

        private double EstimateRowHeight(ReportRow row)
        {
            var labelWidth = LabelColumn - CellPadding * 2;
            var valueWidth = _page.Width.Point - Left - Right - LabelColumn - CellPadding * 2;
            var lineCount = Math.Max(
                Wrap(row.Label, _headerFont, labelWidth).Count(),
                Wrap(row.Value, _bodyFont, valueWidth).Count());
            return lineCount * LineHeight + CellPadding * 2;
        }

        private void DrawContinuationHeader()
        {
            _graphics.DrawString(_sectionTitle, _headerFont, new XSolidBrush(Accent), new XPoint(Left, _y + 11));
            _y += 15;
            DrawTableHeader();
        }

        private void DrawTableHeader()
        {
            var tableWidth = _page.Width.Point - Left - Right;
            _graphics.DrawRectangle(new XSolidBrush(Accent), Left, _y, tableWidth, HeaderHeight);
            _graphics.DrawString("Field", _headerFont, XBrushes.White,
                new XRect(Left + CellPadding, _y + 6, LabelColumn - CellPadding * 2, 13), XStringFormats.TopLeft);
            _graphics.DrawString("Details", _headerFont, XBrushes.White,
                new XRect(Left + LabelColumn + CellPadding, _y + 6, tableWidth - LabelColumn - CellPadding * 2, 13), XStringFormats.TopLeft);
            _y += HeaderHeight;
        }

        private void AddPage(bool firstPage)
        {
            _graphics?.Dispose();
            _page = _document.AddPage();
            _page.Size = PdfSharp.PageSize.A4;
            _graphics = XGraphics.FromPdfPage(_page);

            if (firstPage)
            {
                _y = Top;
                return;
            }

            _y = Top;
            _graphics.DrawString("SHIFT HANDOVER REPORT", _headerFont, new XSolidBrush(Accent), new XPoint(Left, _y));
            _y += 13;
            foreach (var line in Wrap(_shiftTitle, _smallFont, _page.Width.Point - Left - Right))
            {
                _graphics.DrawString(line, _smallFont, new XSolidBrush(Ink), new XPoint(Left, _y));
                _y += 11;
            }

            _y += 2;
            _graphics.DrawLine(new XPen(Grid, 0.7), Left, _y, _page.Width.Point - Right, _y);
            _y += 13;
        }

        private void EnsureSpace(double height)
        {
            if (_y + height > ContentBottom)
            {
                AddPage(firstPage: false);
            }
        }

        private double ContentBottom => _page.Height.Point - Footer - 8;

        private double ContinuationContentTop => Top + 13
            + Wrap(_shiftTitle, _smallFont, _page.Width.Point - Left - Right).Count() * 11 + 2 + 13;

        private void DrawLines(string[] lines, int start, int count, XFont font, double x, double y, double width)
        {
            for (var index = 0; index < count && start + index < lines.Length; index++)
            {
                var textBox = new XRect(x, y + index * LineHeight, width, LineHeight);
                _graphics.DrawString(lines[start + index], font, new XSolidBrush(Ink), textBox, XStringFormats.TopLeft);
            }
        }

        private IEnumerable<string> Wrap(string text, XFont font, double maxWidth)
        {
            foreach (var paragraph in text.Replace("\r", string.Empty).Split('\n'))
            {
                var current = string.Empty;
                foreach (var originalWord in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var word = originalWord;
                    var candidate = current.Length == 0 ? word : $"{current} {word}";
                    if (_graphics.MeasureString(candidate, font).Width <= maxWidth)
                    {
                        current = candidate;
                        continue;
                    }

                    if (current.Length > 0)
                    {
                        yield return current;
                        current = string.Empty;
                    }

                    while (_graphics.MeasureString(word, font).Width > maxWidth)
                    {
                        var splitAt = 1;
                        while (splitAt < word.Length && _graphics.MeasureString(word[..(splitAt + 1)], font).Width <= maxWidth)
                        {
                            splitAt++;
                        }

                        yield return word[..splitAt];
                        word = word[splitAt..];
                    }

                    current = word;
                }

                if (current.Length > 0 || paragraph.Length == 0)
                {
                    yield return current;
                }
            }
        }
    }
}