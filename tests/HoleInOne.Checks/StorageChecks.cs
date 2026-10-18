using Playnite.HoleInOne;

static class StorageChecks
{
    public static void Run(Action<bool, string> check)
    {
        var old = new StorageCandidate { Id = Guid.NewGuid(), Bytes = 30, LastPlayed = DateTime.UtcNow.AddDays(-20), OnTargetDrive = true };
        var recent = new StorageCandidate { Id = Guid.NewGuid(), Bytes = 40, LastPlayed = DateTime.UtcNow, OnTargetDrive = true };
        var never = new StorageCandidate { Id = Guid.NewGuid(), Bytes = 10, OnTargetDrive = false };
        var games = new[] { recent, old, never };
        check(StoragePlanner.Plan(80, 20, 100, 100, games).Count == 0, "exact quota fit deletes nothing");
        var plan = StoragePlanner.Plan(80, 60, 100, 100, games);
        check(plan.Count == 2 && plan[0] == never && plan[1] == old, "complete plan uses never played then least recently played");
        plan = StoragePlanner.Plan(80, 70, 100, 100, games);
        check(plan.Count == 3, "multiple removals are selected before execution");
        plan = StoragePlanner.Plan(80, 20, 100, 0, games);
        check(plan.Count == 1 && plan[0] == old, "physical space must be recovered on target drive");
        foreach (var action in new Action[] {
            () => StoragePlanner.Plan(80, 101, 100, 1000, games),
            () => StoragePlanner.Plan(100, 90, 100, 1000, games),
            () => StoragePlanner.Plan(0, 0, 100, 1000, games),
            () => StoragePlanner.Plan(0, 90, 100, 0, games) })
        {
            try { action(); throw new Exception("unsafe plan accepted"); }
            catch (InvalidOperationException) { check(true, "impossible/unknown capacity fails before returning any deletion list"); }
        }
        check(StoragePlanner.Plan(ulong.MaxValue, 1, ulong.MaxValue, 100, new[] { never }).Count == 1,
            "quota computation does not overflow");
        old.IsProtected = true;
        plan = StoragePlanner.Plan(80, 60, 100, 100, games);
        check(plan.Count == 2 && plan[0] == never && plan[1] == recent && !plan.Contains(old),
            "protected oldest game is skipped while its bytes remain in usage");
        recent.IsProtected = true;
        try { StoragePlanner.Plan(80, 60, 100, 100, games); throw new Exception("protected games selected"); }
        catch (InvalidOperationException) { check(true, "insufficient unprotected capacity rejects the entire removal plan"); }
        try { StoragePlanner.Plan(80, 20, 100, 0, games); throw new Exception("protected target drive game selected"); }
        catch (InvalidOperationException) { check(true, "physical disk shortage never overrides protection"); }
        old.IsProtected = false;
        check(StoragePlanner.Plan(80, 20, 100, 0, games).Single() == old, "unchecking protection makes game eligible again");
    }
}
