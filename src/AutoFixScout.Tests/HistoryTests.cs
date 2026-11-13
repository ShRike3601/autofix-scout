using Xunit;

namespace AutoFixScout.Tests;

public class HistoryTests
{
    [Fact]
    public void Recent_ReturnsNewestFirst_WithOutcome()
    {
        var path = Path.Combine(Path.GetTempPath(), $"afs-history-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new SessionLog(path);
            var a = db.Start("audio", DateTime.UtcNow.AddHours(-2));
            db.End(a, Outcome.Fixed);
            db.Start("other", DateTime.UtcNow);
            var r = db.Recent(5);
            Assert.Equal(new[] { "other", "audio" }, r.Select(x => x.Symptom));
            Assert.Equal("fixed", r[1].Outcome);
            var text = HistoryText.Build("1.2.3", r);
            Assert.Contains("Sound, resolved", text);
            Assert.Contains("Other check, not finished", text);
            Assert.DoesNotMatch(@"n't|'d |'ll |\bI\b|\bwe\b|!", text + HistoryText.Build("1", Array.Empty<SessionLog.SessionSummary>()));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
