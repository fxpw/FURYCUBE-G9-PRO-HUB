using System;

internal static class G9CommandProcessOwnerTests {
    sealed class Child : IG9CommandProcess {
        internal bool HasExitedNow, ExitOnKill;
        internal int Starts, Kills, Disposals;
        public void Start() { Starts++; }
        public bool HasExited { get { return HasExitedNow; } }
        public void Kill() { Kills++; if (ExitOnKill) HasExitedNow = true; }
        public bool WaitForExit(int milliseconds) { return HasExitedNow; }
        public void Dispose() { Disposals++; }
    }

    static void Require(bool passed, string reason) {
        if (!passed) throw new Exception(reason);
    }

    static void Reject(Action action) {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Operation ran before the device session recovered.");
    }

    static int Main() {
        var owner = new G9CommandProcessOwner();
        var hung = new Child();
        var lease = owner.Start("settings-set --pid fa60", hung);
        Require(!owner.Stop(lease), "Kill without exit must stay unconfirmed");
        owner.Finish(lease, false);
        Require(hung.Kills == 1 && hung.Disposals == 0, "Keep the exact child until exit");
        var rejected = new Child();
        Reject(() => owner.Start("caps", rejected));
        Require(rejected.Starts == 0 && rejected.Disposals == 1, "Never launch a second child");
        hung.HasExitedNow = true;
        Reject(() => owner.Start("settings-set --pid fa60", new Child()));
        Reject(owner.RequireReadyForOled);
        Require(hung.Disposals == 1, "Reap the completed child once");
        owner.Finish(owner.Start("caps", new Child { HasExitedNow = true }), true);
        owner.Finish(owner.Start("settings-read --pid fa60", new Child { HasExitedNow = true }), false);
        Reject(owner.ConfirmFreshRead);
        owner.Finish(owner.Start("caps", new Child { HasExitedNow = true }), true);
        owner.Finish(owner.Start("settings-read --pid fa60", new Child { HasExitedNow = true }), true);
        owner.ConfirmFreshRead();
        owner.RequireReadyForOled();

        var active = new Child { ExitOnKill = true };
        var closing = owner.Start("settings-read --pid fa60", active);
        owner.Close();
        Require(active.Kills == 1 && active.Disposals == 0, "Close stops the child and leaves the runner its handle");
        owner.Finish(closing, false);
        Require(active.Disposals == 1, "Runner releases its handle after close");
        Reject(() => owner.Start("caps", new Child()));
        Console.WriteLine("PASS command process timeout, recovery and close");
        return 0;
    }
}
