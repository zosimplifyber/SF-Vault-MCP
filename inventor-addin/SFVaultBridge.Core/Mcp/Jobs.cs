using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>Runs work on the UI thread with a caller-side timeout. When the wait times out before the UI thread picked the work up,
/// the work is cancelled and never starts. Once started it always runs to completion (its result is discarded).</summary>
public static class UiCall
{
    private sealed class Flag
    {
        public bool Started, Cancelled;
    }

    public const string NotStarted = "timed out before starting; nothing was changed";

    public static JsonNode? Run(Func<Func<JsonNode?>, JsonNode?> onUi, string name, Func<JsonNode?> work, TimeSpan limit)
    {
        var flag = new Flag();
        var task = Task.Run(() => onUi(() =>
        {
            lock (flag)
            {
                if (flag.Cancelled) throw new BridgeException("cancelled", NotStarted);
                flag.Started = true;
            }
            return work();
        }));
        if (Task.WaitAny(new Task[] { task }, limit) >= 0) return task.GetAwaiter().GetResult();
        bool started;
        lock (flag)
        {
            started = flag.Started;
            if (!started) flag.Cancelled = true;
        }
        task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        throw new BridgeException("timeout", started
            ? $"{name} did not finish within {(int)limit.TotalSeconds} seconds and is still running in Inventor; its result is discarded. Try again."
            : $"{name} {NotStarted}. Inventor may be busy (a dialog open?). Try again.");
    }
}

public enum JobState { Running, Done, Failed, Cancelled }

/// <summary>Write tools run as jobs: the caller waits a while, and if the work is not done gets a job id to poll with job_status.
/// A job is never abandoned; only a job still waiting for the UI thread can be cancelled. One job runs at a time. Jobs are kept for an hour after they end.</summary>
public sealed class JobManager
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan DefaultPollWait = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private sealed class Job
    {
        public string Id = "";
        public string Tool = "";
        public DateTime StartedAt;
        public DateTime? EndedAt;
        public JobState State = JobState.Running;
        public bool UiStarted;
        public JsonNode? Result;
        public string? ErrorCode, ErrorMessage;
        public Task Task = Task.CompletedTask;
    }

    private readonly Func<Func<JsonNode?>, JsonNode?> _onUi;
    private readonly TimeSpan _wait, _pollWait;
    private readonly Func<DateTime> _clock;
    private readonly Action<string> _log;
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public JobManager(Func<Func<JsonNode?>, JsonNode?> onUi, TimeSpan? wait = null, TimeSpan? pollWait = null,
                      Func<DateTime>? clock = null, Action<string>? log = null)
    {
        _onUi = onUi;
        _wait = wait ?? DefaultWait;
        _pollWait = pollWait ?? DefaultPollWait;
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log ?? (_ => { });
    }

    public int Count
    {
        get { lock (_gate) { Purge(); return _jobs.Count; } }
    }

    /// <summary>Starts the work. Returns its result when it finishes within the wait (or throws its failure); otherwise a "running" object with the job id.</summary>
    public JsonNode? RunOrDetach(string tool, Func<JsonNode?> work)
    {
        var job = new Job { Id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), Tool = tool, StartedAt = _clock() };
        lock (_gate)
        {
            Purge();
            var busy = _jobs.Values.FirstOrDefault(j => j.State == JobState.Running);
            if (busy is not null)
                throw new BridgeException("busy", $"{busy.Tool} is still running (job_id {busy.Id}). Wait for it with job_status before starting another write.");
            _jobs[job.Id] = job;
        }
        job.Task = Task.Run(() =>
        {
            try
            {
                var r = _onUi(() =>
                {
                    lock (job)
                    {
                        if (job.State == JobState.Cancelled) throw new BridgeException("cancelled", UiCall.NotStarted);
                        job.UiStarted = true;
                    }
                    return work();
                });
                var failed = r is JsonObject o && o["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var b) && !b;
                End(job, failed ? JobState.Failed : JobState.Done, r, null, null);
            }
            catch (BridgeException ex) when (ex.Code == "cancelled") { End(job, JobState.Cancelled, null, ex.Code, ex.Message); }
            catch (BridgeException ex) { End(job, JobState.Failed, null, ex.Code, ex.Message); }
            catch (Exception ex)
            {
                try { _log($"job {job.Id} {tool} crashed: {ex}"); } catch { }
                End(job, JobState.Failed, null, "internal", $"An unexpected error occurred ({ex.GetType().Name}); see bridge.log.");
            }
        });
        if (Task.WaitAny(new Task[] { job.Task }, _wait) < 0)
            return new JsonObject
            {
                ["status"] = "running", ["job_id"] = job.Id,
                ["message"] = "Still working in Inventor; call job_status with this job_id",
            };
        lock (job)
        {
            if (job.State == JobState.Failed && job.Result is null) throw new BridgeException(job.ErrorCode ?? "failed", job.ErrorMessage ?? "failed");
            if (job.State == JobState.Cancelled) throw new BridgeException("cancelled", job.ErrorMessage ?? UiCall.NotStarted);
            return job.Result;
        }
    }

    private void End(Job job, JobState state, JsonNode? result, string? code, string? message)
    {
        lock (job)
        {
            if (job.State != JobState.Running) return; // already cancelled
            job.State = state;
            job.Result = result;
            job.ErrorCode = code;
            job.ErrorMessage = message;
            job.EndedAt = _clock();
        }
    }

    /// <summary>The job's state; waits up to the poll wait for a running job to end. With cancel, a job that has not reached the UI thread is cancelled.</summary>
    public JsonNode Status(string jobId, bool cancel = false)
    {
        Job job;
        lock (_gate)
        {
            Purge();
            if (!_jobs.TryGetValue(jobId, out job!))
                throw new BridgeException("not_found", "Unknown job_id, or the job ended more than an hour ago.");
        }
        string? cancelNote = null;
        if (cancel)
        {
            lock (job)
            {
                if (job.State == JobState.Running && !job.UiStarted)
                {
                    job.State = JobState.Cancelled;
                    job.EndedAt = _clock();
                    job.ErrorCode = "cancelled";
                    job.ErrorMessage = "Cancelled before it started; nothing was changed.";
                }
                else if (job.State == JobState.Running) cancelNote = "Too late to cancel: the job already started and runs to completion.";
            }
        }
        else if (job.State == JobState.Running) Task.WaitAny(new Task[] { job.Task }, _pollWait);
        lock (job)
        {
            var o = new JsonObject
            {
                ["job_id"] = job.Id, ["tool"] = job.Tool,
                ["status"] = job.State switch { JobState.Running => "running", JobState.Done => "done", JobState.Failed => "failed", _ => "cancelled" },
                ["elapsed_seconds"] = (int)((job.EndedAt ?? _clock()) - job.StartedAt).TotalSeconds,
            };
            if (job.State == JobState.Running)
                o["message"] = cancelNote ?? "Still working in Inventor; call job_status again.";
            if (job.Result is not null) o["result"] = job.Result.DeepClone();
            if (job.ErrorCode is not null) o["error"] = new JsonObject { ["error"] = job.ErrorCode, ["message"] = job.ErrorMessage };
            if (job.State is JobState.Failed or JobState.Cancelled) o["ok"] = false;
            return o;
        }
    }

    private void Purge()
    {
        var now = _clock();
        foreach (var k in _jobs.Where(p => p.Value.EndedAt is { } e && now - e > Retention).Select(p => p.Key).ToList()) _jobs.Remove(k);
    }
}

public static class JobTools
{
    public static McpTool Build(JobManager jobs) => new("job_status", "Job status",
        "Check a write that was still running when the tool returned {status:\"running\", job_id}: fix_data_card_apply or mcmaster_intake_commit. " +
        "Returns running (call again), done with the final result, failed with the error, or cancelled. Waits up to 20 seconds for a running job. " +
        "Never start the write again while its job is running or after it is done; read the result here. Jobs are kept for 1 hour. " +
        "Set cancel true to cancel a job that has not started yet (rarely needed).",
        Schema.Obj(Schema.Props(
            ("job_id", Schema.Str("The job_id returned by the write tool.")),
            ("cancel", Schema.Bool("Cancel the job if it has not started yet.", false))), "job_id"),
        McpToolKind.Read, a => jobs.Status(Args.Str(a, "job_id"), Args.Bool(a, "cancel", false)), UiThread: false);
}
