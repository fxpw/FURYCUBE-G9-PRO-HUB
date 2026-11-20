using System;
using System.Diagnostics;

internal interface IG9CommandProcess : IDisposable {
    void Start();
    bool HasExited { get; }
    void Kill();
    bool WaitForExit(int milliseconds);
}
internal sealed class G9CommandProcess : IG9CommandProcess {
    internal readonly Process Process;
    internal G9CommandProcess(Process process){Process=process;}
    public void Start(){if(!Process.Start())throw new InvalidOperationException("Device command did not start");}
    public bool HasExited {get{return Process.HasExited;}}
    public void Kill(){Process.Kill();}
    public bool WaitForExit(int milliseconds){return Process.WaitForExit(milliseconds);}
    public void Dispose(){Process.Dispose();}
}
// This lock protects ownership/state only. Feature waits and termination waits
// never hold it, so closing the form does not wait on the deviceGate lock.
internal sealed class G9CommandProcessOwner {
    internal sealed class Lease {
        internal readonly IG9CommandProcess Process;
        internal readonly string Action;
        internal bool ExitConfirmed,RunnerFinished,Disposed,StopRequested;
        internal Lease(string action,IG9CommandProcess process){Action=action;Process=process;}
    }
    readonly object gate=new object();
    readonly int stopWaitMilliseconds;
    Lease active;
    bool closed,recoveryRequired,recoveryCaps,recoverySettings;
    internal G9CommandProcessOwner(int stopWaitMilliseconds=1500){
        if(stopWaitMilliseconds<1||stopWaitMilliseconds>2000)throw new ArgumentOutOfRangeException("stopWaitMilliseconds");
        this.stopWaitMilliseconds=stopWaitMilliseconds;
    }
    static bool IsCaps(string action){return action=="caps"||action.StartsWith("caps ",StringComparison.Ordinal);}
    static bool IsSettingsRead(string action){return action=="settings-read"||action.StartsWith("settings-read ",StringComparison.Ordinal);}
    static bool Exited(Lease lease){try{return lease.Process.HasExited;}catch{return false;}}
    void DisposeFinished(Lease lease){
        bool dispose=false;
        lock(gate)if(lease.ExitConfirmed&&lease.RunnerFinished&&!lease.Disposed){lease.Disposed=true;dispose=true;}
        if(dispose)lease.Process.Dispose();
    }
    void ObserveExit(Lease lease){
        if(!Exited(lease))return;
        lock(gate){lease.ExitConfirmed=true;if(ReferenceEquals(active,lease))active=null;}
        DisposeFinished(lease);
    }
    void Reap(){Lease prior;lock(gate)prior=active;if(prior!=null)ObserveExit(prior);}
    internal Lease Start(string action,IG9CommandProcess process){
        if(action==null||process==null)throw new ArgumentNullException();
        Reap();
        try {
            lock(gate){
                if(closed)throw new ObjectDisposedException("HUB");
                if(active!=null)throw new InvalidOperationException("Предыдущий процесс устройства ещё не завершён; новые команды остановлены.");
                if(recoveryRequired&&!IsCaps(action)&&!(recoveryCaps&&IsSettingsRead(action)))
                    throw new InvalidOperationException("После остановки процесса сначала заново прочитайте подключение и настройки мыши.");
                // Launch and registration are atomic against Close. Only this
                // short OS launch is inside the state lock; never a child wait.
                process.Start();var lease=new Lease(action,process);active=lease;return lease;
            }
        }catch{process.Dispose();throw;}
    }
    internal bool Stop(Lease lease){
        if(lease==null)return true;
        bool kill;
        lock(gate){
            recoveryRequired=true;recoveryCaps=false;recoverySettings=false;
            kill=!lease.StopRequested;lease.StopRequested=true;
        }
        if(!Exited(lease)&&kill){try{lease.Process.Kill();}catch{}}
        // Keep the exact process object if termination cannot be confirmed.
        // Do not dispose it merely because Kill returned or threw.
        try{if(lease.Process.WaitForExit(stopWaitMilliseconds)){
            lock(gate){lease.ExitConfirmed=true;if(ReferenceEquals(active,lease))active=null;}
        }}catch{}
        ObserveExit(lease);
        lock(gate)return lease.ExitConfirmed;
    }
    internal void Finish(Lease lease,bool success){
        if(lease==null)return;
        ObserveExit(lease);
        bool confirmed;lock(gate)confirmed=lease.ExitConfirmed;
        if(!confirmed)Stop(lease);
        lock(gate){
            lease.RunnerFinished=true;
            if(recoveryRequired&&lease.ExitConfirmed&&!closed){
                if(success&&IsCaps(lease.Action)){recoveryCaps=true;recoverySettings=false;}
                else if(IsSettingsRead(lease.Action)){
                    recoverySettings=success&&recoveryCaps;
                    if(!success)recoveryCaps=false;
                }
            }
        }
        DisposeFinished(lease);
    }
    internal void ConfirmFreshRead(){
        Reap();lock(gate){
            if(closed)throw new ObjectDisposedException("HUB");
            if(active!=null)throw new InvalidOperationException("Процесс устройства ещё не завершён.");
            if(recoveryRequired&&(!recoveryCaps||!recoverySettings))
                throw new InvalidOperationException("Для продолжения нужны новое подключение и успешно прочитанные настройки.");
            recoveryRequired=false;recoveryCaps=false;recoverySettings=false;
        }
    }
    internal void RequireReadyForOled(){
        Reap();lock(gate){
            if(closed)throw new ObjectDisposedException("HUB");
            if(active!=null||recoveryRequired)throw new InvalidOperationException("OLED остановлен до завершения процесса и нового чтения настроек мыши.");
        }
    }
    internal void Close(){Lease prior;lock(gate){closed=true;prior=active;}if(prior!=null)Stop(prior);}
    internal bool RecoveryRequired {get{lock(gate)return recoveryRequired;}}
    internal bool HasActiveProcess {get{Reap();lock(gate)return active!=null;}}
}
