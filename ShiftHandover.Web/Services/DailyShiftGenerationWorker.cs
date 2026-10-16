using ShiftHandover.Web.Data;

namespace ShiftHandover.Web.Services;

public sealed class DailyShiftGenerationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<DailyShiftGenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var utcNow = DateTimeOffset.UtcNow;
            var bahrainDate = BahrainTime.GetLocalDate(utcNow);

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await DailyShiftGenerator.EnsureShiftsForDateAsync(db, bahrainDate, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Daily shift generation failed for Bahrain date {BahrainDate}.", bahrainDate);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }

            var nextBahrainMidnight = new DateTimeOffset(
                bahrainDate.AddDays(1).ToDateTime(TimeOnly.MinValue),
                BahrainTime.UtcOffset);
            var delay = nextBahrainMidnight - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, stoppingToken);
            }
        }
    }
}