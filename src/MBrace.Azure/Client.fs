﻿namespace MBrace.Azure

#nowarn "40"
#nowarn "444"
#nowarn "445"

open System
open System.Diagnostics
open System.IO
open MBrace.Core
open MBrace.Core.Internals
open MBrace.Azure
open MBrace.Azure.Runtime
open MBrace.Azure.Runtime.Arguments
open MBrace.Runtime

type ConsoleLogger = MBrace.Runtime.ConsoleLogger

/// <summary>
/// Windows Azure Runtime client.
/// </summary>
[<AutoSerializable(false)>]
type MBraceAzure private (manager : RuntimeManager, defaultLogger : SystemLogger) =
    inherit MBraceClient(manager)

    static let lockObj = obj()
    static let mutable localWorkerExecutable : string option = None

    let mutable consoleLogger = None : IDisposable option

    /// Current client instance identifier.
    member this.ClientId = manager.RuntimeManagerId

    /// Sets whether logging in console is enabled for this client.
    member this.EnableClientConsoleLogger 
        with get () = consoleLogger.IsSome
        and set enable =
            lock lockObj (fun () ->
                match enable, consoleLogger with
                | true, None ->
                    match (manager :> IRuntimeManager).SystemLogger with
                    | :? AttacheableLogger as l -> 
                        consoleLogger <- Some(l.AttachLogger(new ConsoleLogger(true)))
                    | _ as l -> failwithf "Not supported client logger %A" l
                | false, Some(logger) -> 
                    logger.Dispose()
                    consoleLogger <- None
                | _ -> ())

    /// <summary>
    /// Get runtime logs.
    /// </summary>
    /// <param name="worker">Get logs from specific worker.</param>
    /// <param name="fromDate">Get logs from this date.</param>
    /// <param name="toDate">Get logs until this date.</param>
    member this.GetSystemLogs(?worker : IWorkerRef, ?fromDate : DateTimeOffset, ?toDate : DateTimeOffset) : seq<_> = 
        let loggerId = worker |> Option.map (fun w -> w.Id)
        defaultLogger.GetLogs(?loggerId = loggerId, ?fromDate = fromDate, ?toDate = toDate)

    /// <summary>
    /// Print runtime logs.
    /// </summary>
    /// <param name="worker">Get logs from specific worker.</param>
    /// <param name="fromDate">Get logs from this date.</param>
    /// <param name="toDate">Get logs until this date.</param>
    member this.ShowSystemLogs(?worker : IWorkerRef, ?fromDate : DateTimeOffset, ?toDate : DateTimeOffset) =
        let loggerId = worker |> Option.map (fun w -> w.Id)
        defaultLogger.ShowLogs(?loggerId = loggerId, ?fromDate = fromDate, ?toDate = toDate)

    /// <summary>
    /// Print runtime logs.
    /// </summary>
    /// <param name="worker">Get logs from specific worker.</param>
    /// <param name="n">Get logs written the last n seconds.</param>
    member this.ShowSystemLogs(n : float, ?worker : IWorkerRef) =
        let fromDate = DateTimeOffset.Now - (TimeSpan.FromSeconds n)
        this.ShowSystemLogs(?worker = worker, fromDate = fromDate)

    /// <summary>
    /// Kill all local worker processes.
    /// </summary>
    member this.KillLocalWorker() =
        let workers = this.Workers
        let exists id hostname =
            if Net.Dns.GetHostName() = hostname then
                let ps = try Some <| Process.GetProcessById(id) with :? ArgumentException -> None
                ps.IsSome
            else false
        workers |> Seq.filter (fun w -> exists w.ProcessId w.Hostname)    
                |> Seq.iter this.KillLocalWorker

    /// <summary>
    /// Kill local worker process.
    /// </summary>
    /// <param name="worker">Local worker to kill.</param>
    member this.KillLocalWorker(worker : WorkerRef) =
        let hostname = System.Net.Dns.GetHostName()
        if worker.Hostname <> hostname then
            failwith "WorkerRef hostname %A does not match local hostname %A" worker.Hostname hostname
        else
            let ps = try Some <| Process.GetProcessById(worker.ProcessId) with :? ArgumentException -> None
            match ps with
            | Some p ->
                (manager :> IRuntimeManager).WorkerManager.DeclareWorkerStatus(worker.WorkerId, WorkerJobExecutionStatus.Stopped)
                |> Async.RunSync
                p.Kill()
            | _ ->
                failwithf "No local process with Id = %d found." worker.ProcessId

    /// Gets or sets the path for a local standalone worker executable.
    static member LocalWorkerExecutable
        with get () = match localWorkerExecutable with None -> invalidOp "unset executable path." | Some e -> e
        and set path = 
            lock lockObj (fun () ->
                let path = Path.GetFullPath path
                if File.Exists path then localWorkerExecutable <- Some path
                else raise <| FileNotFoundException(path))

    /// Initialize a new local runtime instance with supplied worker count.
    static member SpawnLocal(config, workerCount, ?maxTasks : int, ?workerNameF : int -> string) =
        let exe = MBraceAzure.LocalWorkerExecutable
        if workerCount < 1 then invalidArg "workerCount" "must be positive."  
        let cfg = { Arguments.Configuration = config; Arguments.MaxTasks = defaultArg maxTasks Environment.ProcessorCount; Name = None}
        do Async.RunSync(Config.ActivateAsync(config, true))
        let _ = Array.Parallel.init workerCount (fun i -> 
            let conf =
                match workerNameF with
                | None -> cfg
                | Some f -> {cfg with Name = Some(f(i)) }
            let args = Config.ToBase64Pickle conf
            let psi = new ProcessStartInfo(exe, args)
            psi.WorkingDirectory <- Path.GetDirectoryName exe
            psi.UseShellExecute <- true
            Process.Start psi)
        ()

    /// Initialize a new local runtime instance with supplied worker count and return a handle.
    static member InitLocal(config, workerCount : int, ?maxTasks : int, ?workerNameF) : MBraceAzure =
        MBraceAzure.SpawnLocal(config, workerCount, ?maxTasks = maxTasks, ?workerNameF = workerNameF)
        MBraceAzure.GetHandle(config)

    /// Delete and reactivate runtime state (queues, containers, tables and logs but not user folders).
    /// Using 'Reset' may cause unexpected behavior in clients and workers.
    [<CompilerMessage("Using 'Reset' may cause unexpected behavior in clients and workers.", 445)>]
    member this.Reset () = 
        (manager :> IRuntimeManager).ResetClusterState()
        |> Async.RunSync

    /// <summary>
    /// Delete and re-activate runtime state.
    /// Using 'Reset' may cause unexpected behavior in clients and workers.
    /// Workers should be restarted manually.</summary>
    /// <param name="deleteQueues">Delete Configuration queue and topic.</param>
    /// <param name="deleteState">Delete Configuration table and containers.</param>
    /// <param name="deleteLogs">Delete Configuration logs table.</param>
    /// <param name="deleteUserData">Delete Configuration UserData table and container.</param>
    /// <param name="force">Ignore active workers.</param>
    /// <param name="reactivate">Reactivate configuration.</param>
    [<CompilerMessage("Using 'Reset' may cause unexpected behavior in clients and workers.", 445)>]
    member this.Reset(deleteQueues, deleteState, deleteLogs, deleteUserData, force, reactivate) =
        manager.ResetCluster(deleteQueues, deleteState, deleteLogs, deleteUserData, force, reactivate)
        |> Async.RunSync

    /// <summary>
    /// Gets a handle for a remote runtime.
    /// </summary>
    /// <param name="storageConnectionString">Azure Storage connection string.</param>
    /// <param name="serviceBusConnectionString">Azure Service Bus connection string.</param>
    static member GetHandle(storageConnectionString : string, serviceBusConnectionString : string) : MBraceAzure = 
        MBraceAzure.GetHandle(new Configuration(storageConnectionString, serviceBusConnectionString))

    /// <summary>
    /// Gets a handle for a remote runtime.
    /// </summary>
    /// <param name="config">Runtime configuration.</param>
    /// <param name="clientId">Client identifier.</param>
    static member GetHandle(config : Configuration, ?clientId : string, ?logger : ISystemLogger) : MBraceAzure = 
        let hostProc = Diagnostics.Process.GetCurrentProcess()
        let clientId = defaultArg clientId <| sprintf "%s-%s-%05d" (System.Net.Dns.GetHostName()) hostProc.ProcessName hostProc.Id
        let storageLogger = SystemLogger.Create(config.StorageConnectionString, config.GetConfigurationId().RuntimeLogsTable, clientId)
        let logger = match logger with Some l -> l | None -> new NullLogger() :> _
        let manager = RuntimeManager.CreateForClient(config, clientId, logger, ResourceRegistry.Empty)
        let runtime = new MBraceAzure(manager, storageLogger)
        runtime