using ShiftHandover.Web.Models;
using ShiftHandover.Web.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ShiftHandover.Tests;

public class ShiftReportPdfLayoutTests
{
    [Fact]
    public void TableText_StaysWithinRowsAndLongDescriptionWraps()
    {
        var shift = CreateClosedShift("LayoutShiftTitleMarker");
        var incident = new Incident
        {
            OccurredAt = new DateTimeOffset(2026, 10, 16, 10, 15, 0, TimeSpan.FromHours(3)),
            Category = "LayoutCategoryMarker",
            Description = string.Join(' ', Enumerable.Repeat("wraptoken", 32)),
            Resolved = true,
            FollowUpRequired = true,
            RecordedBySupervisor = new ApplicationUser { FullName = "Layout Supervisor" }
        };

        var pdf = new ShiftReportPdfGenerator().Generate(shift, [], [incident], []);
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Single(document.GetPages());

        var words = document.GetPage(1).GetWords().ToArray();
        AssertBelowPreviousTableHeader(words, "LayoutShiftTitleMarker");
        AssertBelowPreviousTableHeader(words, "accident");
        AssertBelowPreviousTableHeader(words, "LayoutCategoryMarker");
        AssertBelowPreviousTableHeader(words, "manpower");

        var wrappedLines = words
            .Where(word => word.Text == "wraptoken")
            .GroupBy(word => Math.Round(word.BoundingBox.Bottom, 1))
            .OrderByDescending(line => line.Key)
            .ToArray();
        Assert.True(wrappedLines.Length >= 3, "The long incident description should wrap onto multiple lines.");

        var category = words.Single(word => word.Text == "LayoutCategoryMarker").BoundingBox;
        var firstDescriptionLine = wrappedLines[0].ToArray();
        Assert.True(
            firstDescriptionLine.Max(word => word.BoundingBox.Top) < category.Bottom,
            "The description text must not overlap the category row above it.");

        var lastDescriptionLine = wrappedLines[^1].ToArray();
        var resolved = words.Single(word => word.Text == "Resolved").BoundingBox;
        Assert.True(
            resolved.Top < lastDescriptionLine.Min(word => word.BoundingBox.Bottom),
            "The resolved row must start below the final wrapped description line.");

        Assert.Contains("Bahrain", string.Join(' ', words.Select(word => word.Text)));
        Assert.Contains("UTC+03:00", string.Join(' ', words.Select(word => word.Text)));
    }

    [Fact]
    public void LongRows_PaginateWithRepeatedHeadersAndCorrectPageNumbers()
    {
        var shift = CreateClosedShift("Pagination layout test shift");
        var incidents = Enumerable.Range(0, 4).Select(index => new Incident
        {
            OccurredAt = new DateTimeOffset(2026, 10, 16, 10, index, 0, TimeSpan.FromHours(3)),
            Category = $"Pagination category {index}",
            Description = string.Join(' ', Enumerable.Repeat("paginationmarker", 85)),
            RecordedBySupervisor = new ApplicationUser { FullName = "Layout Supervisor" }
        }).ToArray();

        var pdf = new ShiftReportPdfGenerator().Generate(shift, [], incidents, []);
        using var document = PdfDocument.Open(new MemoryStream(pdf));

        Assert.True(document.NumberOfPages > 1);
        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            var pageText = string.Join(' ', document.GetPage(pageNumber).GetWords().Select(word => word.Text));
            Assert.Contains("Field", pageText);
            Assert.Contains("Details", pageText);
            Assert.Contains($"Page {pageNumber} of {document.NumberOfPages}", pageText);
        }
    }

    private static Shift CreateClosedShift(string title)
    {
        var shift = new Shift
        {
            Title = title,
            ShiftDate = new DateOnly(2026, 10, 16),
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0)
        };
        var claimedAt = new DateTimeOffset(2026, 10, 16, 7, 0, 0, TimeSpan.FromHours(3));
        shift.Claim("layout-supervisor", claimedAt);
        shift.Close("layout-supervisor", claimedAt.AddHours(8));
        return shift;
    }

    private static void AssertBelowPreviousTableHeader(IReadOnlyList<Word> words, string valueText)
    {
        var value = words.Last(word => word.Text == valueText);
        var header = words
            .Where(word => word.Text == "Details" && word.BoundingBox.Bottom > value.BoundingBox.Top)
            .OrderBy(word => word.BoundingBox.Bottom)
            .First();
        var verticalGap = header.BoundingBox.Bottom - value.BoundingBox.Top;

        Assert.True(
            verticalGap >= 4,
            $"Table value '{valueText}' needs at least 4 points of separation from the Details header above it.");
    }
}