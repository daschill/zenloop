using System.Diagnostics.Eventing.Reader;
using ZenLoop.Core;

namespace ZenLoop.App.Services;

public interface IFaultSource
{
    IReadOnlyList<FaultEvent> Since(DateTime utc);
}

public sealed class WindowsEventLogFaults : IFaultSource
{
    public IReadOnlyList<FaultEvent> Since(DateTime utc)
    {
        var list = new List<FaultEvent>();
        try
        {
            using var reader = new EventLogReader(
                new EventLogQuery("System", PathType.LogName, "*[System[TimeCreated[timediff(@SystemTime) <= 900000]]]"));
            for (var rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
            {
                using (rec)
                {
                    var created = rec.TimeCreated?.ToUniversalTime();
                    if (created is DateTime t && t < utc.ToUniversalTime())
                        continue;
                    string? msg = null;
                    try { msg = rec.FormatDescription(); } catch { /* ignore */ }
                    var ev = new FaultEvent(rec.ProviderName, (int)rec.Id, msg);
                    if (FaultClassifier.IsFault(ev))
                        list.Add(ev);
                }
            }
        }
        catch (EventLogException)
        {
            // No permission or log missing — treat as no extra faults.
        }
        catch (UnauthorizedAccessException)
        {
        }
        return list;
    }
}
