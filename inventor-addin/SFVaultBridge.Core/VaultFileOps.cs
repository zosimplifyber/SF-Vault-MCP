namespace SFVaultBridge.Core;

/// <summary>What Vault and the workspace say about the latest version of one file. LocalDiffers: the working copy exists and is not byte-identical to
/// Vault's latest version (edited, or an older copy). State is empty when the file has no lifecycle.</summary>
public sealed record FileFacts(string VaultPath, string Name, long MasterId, long Version, string Category, string Lifecycle, string State, bool StateReleased,
                               string Revision, bool CheckedOut, string CheckedOutBy, bool CheckedOutByMe, string LocalPath, bool LocalExists, bool LocalDiffers,
                               bool OpenInInventor, bool OpenDirty, bool IsInventorFile);

/// <summary>Every latest-version match of one file reference (a bare name can match files in several folders).</summary>
public sealed record FileLookup(string Ref, IReadOnlyList<FileFacts> Matches);

/// <summary>What a check-in would do to the file's associations and whether there is anything to check in. Problem is a blocker (for example a child that
/// matches no Vault version); NewReferences are references the open Inventor document has that the checked-out version does not.</summary>
public sealed record CheckinFacts(bool Unchanged, string? Problem, IReadOnlyList<string> NewReferences, int Associations, IReadOnlyList<string> Notes);

/// <summary>What the lifecycle says about moving one file to a target state. Target is the state's display name when found.</summary>
public sealed record StateFacts(string Lifecycle, string Current, bool CurrentReleased, string? Target, bool TargetFound, bool TargetReleased, bool SameState,
                                bool TransitionExists, bool Allowed, bool ChildCheck, string RevisionAfter, IReadOnlyList<string> AvailableTargets, string? Problem);

/// <summary>Outcome of one write on one file. Notes carry non-fatal remarks (for example the Inventor document is still read-only).</summary>
public sealed record OpResult(bool Ok, string Message, long Version = 0, string State = "", string Revision = "", IReadOnlyList<string>? Notes = null);

/// <summary>Vault and Inventor work for check-out, check-in, undo and state change, on Inventor's signed-in session. Reads never change anything.
/// Members throw BridgeException("not_signed_in") when the Vault add-in is not signed in.</summary>
public interface IVaultFileOps
{
    FileLookup Lookup(string fileRef);

    /// <summary>Fresh facts for the file's latest version (drift check at apply).</summary>
    FileFacts Refresh(long masterId);

    CheckinFacts CheckinPreflight(FileFacts f);

    StateFacts PlanState(FileFacts f, string targetState);

    /// <summary>Latest version of each direct Dependency child, cheap facts only (no local comparison).</summary>
    IReadOnlyList<FileFacts> Children(FileFacts f);

    OpResult Checkout(FileFacts f, string comment, bool replaceLocal, bool reopenInInventor);

    OpResult Checkin(FileFacts f, string comment, bool keepCheckedOut);

    OpResult UndoCheckout(FileFacts f);

    OpResult ChangeState(FileFacts f, string targetState, string comment);
}

public enum LocalCopyChoice { None, Keep, Replace }

/// <summary>The decisions behind the four tools. Pure: facts in, blockers and warnings out.</summary>
public static class FileOpRules
{
    public const int MaxFiles = 50;
    public const int MaxChildren = 200;
    public const int MaxDepth = 8;
    public const string DefaultCheckoutComment = "Checked out via SF Vault Bridge";

    public static string Shown(FileFacts f) => f.Revision.Length > 0 ? $"{f.Name} rev {f.Revision}" : f.Name;

    public static string Holder(FileFacts f) => f.CheckedOutByMe ? "you" : string.IsNullOrWhiteSpace(f.CheckedOutBy) ? "someone else" : f.CheckedOutBy;

    public static LocalCopyChoice ParseLocalCopy(string text) => text.Trim().ToLowerInvariant() switch
    {
        "" => LocalCopyChoice.None,
        "keep" => LocalCopyChoice.Keep,
        "replace" => LocalCopyChoice.Replace,
        _ => throw new BridgeException("bad_request", "'local_copy' must be \"keep\" or \"replace\"."),
    };

    public static (List<string> Blockers, List<string> Warnings) Checkout(FileFacts f, LocalCopyChoice choice, bool reopen)
    {
        var b = new List<string>();
        var w = new List<string>();
        if (f.CheckedOut)
            b.Add(f.CheckedOutByMe ? "already checked out to you; nothing to do"
                                   : $"checked out by {Holder(f)}; ask them to check it in (only a Vault administrator can undo someone else's checkout)");
        if (f.StateReleased)
            w.Add($"in {f.State}, a released state; Vault normally refuses a checkout here. Change the state to an editable one first (vault_change_state_preview).");
        if (f.LocalExists && f.LocalDiffers)
        {
            if (choice == LocalCopyChoice.None)
                b.Add("local copy differs from the Vault version (edited, or an older version). Nothing is overwritten by default: pass local_copy \"keep\" to check out and keep your local file, or \"replace\" to overwrite it with Vault's copy.");
            else if (choice == LocalCopyChoice.Replace && f.OpenInInventor)
                b.Add("local copy differs and the file is open in Inventor, so it cannot be replaced. Close it first, or use local_copy \"keep\".");
            else if (choice == LocalCopyChoice.Replace)
                w.Add("local copy differs from Vault and will be REPLACED with Vault's copy; local edits are lost.");
            else
                w.Add("local copy differs from Vault and is kept as it is; check-in will upload it as the new version.");
        }
        if (f.OpenInInventor)
            w.Add(reopen ? "open in Inventor: after the checkout the add-in closes and reopens it so Inventor sees it writable; if an open assembly references it, you may need to reopen the assembly."
                         : "open in Inventor: it stays read-only in Inventor until you close and reopen it.");
        return (b, w);
    }

    public static (List<string> Blockers, List<string> Warnings) Checkin(FileFacts f, CheckinFacts? pre)
    {
        var b = new List<string>();
        var w = new List<string>();
        if (!f.CheckedOut) b.Add("not checked out; check it out first");
        else if (!f.CheckedOutByMe) b.Add($"checked out by {Holder(f)}, not you");
        if (f.OpenDirty) b.Add("open in Inventor with unsaved changes; save it in Inventor first, then preview again");
        if (!f.LocalExists) b.Add("the local file is missing from the working folder");
        if (pre is not null)
        {
            if (pre.Problem is not null) b.Add(pre.Problem);
            if (pre.NewReferences.Count > 0)
                b.Add("it now references " + string.Join(", ", pre.NewReferences.Take(5)) + (pre.NewReferences.Count > 5 ? $" and {pre.NewReferences.Count - 5} more" : "")
                      + ", which the checked-out version does not; carrying the old associations forward would drop them. Use Vault's Check In inside Inventor for this file.");
            if (pre.Unchanged) b.Add("the local file is identical to the checked-out version; nothing to check in. Use vault_undo_checkout_preview to release the checkout.");
            foreach (var n in pre.Notes) w.Add(n);
        }
        else if (f.IsInventorFile && !f.OpenInInventor)
            w.Add("closed in Inventor: references added since the checkout cannot be detected; the previous version's associations are carried forward.");
        return (b, w);
    }

    public static (List<string> Blockers, List<string> Warnings) Undo(FileFacts f, bool discardLocal)
    {
        var b = new List<string>();
        var w = new List<string>();
        if (!f.CheckedOut) b.Add("not checked out; nothing to undo");
        else if (!f.CheckedOutByMe) b.Add($"checked out by {Holder(f)}, not you; only they or a Vault administrator can undo it");
        if (f.CheckedOutByMe && (f.LocalDiffers || f.OpenDirty) && !discardLocal)
            b.Add("local edits (or unsaved changes in Inventor) would be lost. Check in instead, or pass discard_local_changes true to throw them away.");
        else if (f.CheckedOutByMe && (f.LocalDiffers || f.OpenDirty))
            w.Add("local edits will be discarded; the working copy returns to the Vault version.");
        if (f.OpenInInventor) w.Add("open in Inventor: close it without saving after the undo; Inventor's copy is stale.");
        return (b, w);
    }

    /// <summary>True when the file sits in a released state and the target is not one: the move takes it out of Released.</summary>
    public static bool LeavesReleased(FileFacts f, StateFacts s) =>
        s.TargetFound && !s.SameState && f.StateReleased && !s.TargetReleased;

    public static string LeavingLine(FileFacts f, StateFacts s) =>
        $"LEAVING RELEASED: {Shown(f)} moves from {f.State} to {s.Target}. It will no longer be a released document.";

    /// <summary>Blockers for one state change. unreleasedChildren: children with a lifecycle that are not released and are not moving to a released
    /// state in the same plan (only passed when the transition enforces a child check).</summary>
    public static (List<string> Blockers, List<string> Warnings) State(FileFacts f, StateFacts s, string requested, IReadOnlyList<string> unreleasedChildren)
    {
        var b = new List<string>();
        var w = new List<string>();
        if (s.Problem is not null) b.Add(s.Problem);
        else if (f.State.Length == 0) b.Add("has no lifecycle, so it has no state to change");
        else if (!s.TargetFound)
            b.Add($"{s.Lifecycle.Trim()} has no state '{requested}'. Its states: {string.Join(", ", s.AvailableTargets)}.");
        else if (s.SameState) b.Add($"already in {s.Target}");
        else if (!s.TransitionExists)
            b.Add($"the lifecycle has no transition from {f.State} to {s.Target}. From {f.State} it can go to: {(s.AvailableTargets.Count == 0 ? "nowhere" : string.Join(", ", s.AvailableTargets))}.");
        else if (!s.Allowed) b.Add($"you are not allowed to change the state from {f.State} to {s.Target}");
        if (f.CheckedOut) b.Add($"checked out by {Holder(f)}; check it in or undo the checkout first");
        if (unreleasedChildren.Count > 0)
            b.Add("the lifecycle requires its children to be released first, and these are not: " + string.Join(", ", unreleasedChildren.Take(8))
                  + (unreleasedChildren.Count > 8 ? $" and {unreleasedChildren.Count - 8} more" : "") + ". Release them first, or set include_children true.");
        if (f.OpenDirty) w.Add("open in Inventor with unsaved changes; the state change does not touch them.");
        return (b, w);
    }
}
