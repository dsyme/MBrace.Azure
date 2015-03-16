﻿namespace MBrace.Azure.Runtime

#nowarn "444"

open MBrace
open MBrace.Runtime

open MBrace.Azure.Runtime
open MBrace.Azure.Runtime.Info
open MBrace.Azure.Runtime.Primitives
open System
open MBrace.Runtime.InMemory
        
/// Scheduling implementation provider
type RuntimeProvider private (state : RuntimeState, faultPolicy, jobId, psInfo, dependencies, isForcedLocalParallelism : bool) =

    let mkNestedCts (ct : ICloudCancellationToken) =
        let parentCts = ct :?> DistributedCancellationTokenSource
        let dcts = state.ResourceFactory.RequestCancellationTokenSource(psInfo.Id, parent = parentCts, elevate = false)
                   |> Async.RunSynchronously
        dcts :> ICloudCancellationTokenSource

    /// Creates a runtime provider instance for a provided job
    static member FromJob state dependencies (job : Job) =
        new RuntimeProvider(state, job.FaultPolicy, job.JobId, job.ProcessInfo, dependencies, false)

    interface IDistributionProvider with
        member __.CreateLinkedCancellationTokenSource(parents: ICloudCancellationToken []): Async<ICloudCancellationTokenSource> = 
            async {
                match parents with
                | [||] -> 
                    let! cts = state.ResourceFactory.RequestCancellationTokenSource(psInfo.Id, elevate = false) 
                    return cts :> ICloudCancellationTokenSource
                | [| ct |] -> return mkNestedCts ct
                | _ -> return raise <| new System.NotSupportedException("Linking multiple cancellation tokens not supported in this runtime.")
            }
        member __.ProcessId = psInfo.Id

        member __.JobId = jobId

        member __.FaultPolicy = faultPolicy
        member __.WithFaultPolicy newPolicy = 
            new RuntimeProvider(state, newPolicy, jobId, psInfo, dependencies, isForcedLocalParallelism) :> IDistributionProvider

        member __.IsForcedLocalParallelismEnabled = isForcedLocalParallelism
        member __.WithForcedLocalParallelismSetting setting =
            new RuntimeProvider(state, faultPolicy, jobId, psInfo, dependencies, setting) :> IDistributionProvider

        member __.IsTargetedWorkerSupported = true

        member __.ScheduleLocalParallel computations = ThreadPool.Parallel(mkNestedCts, computations)
        member __.ScheduleLocalChoice computations = ThreadPool.Choice(mkNestedCts, computations)

        member __.ScheduleParallel computations = cloud {
            if isForcedLocalParallelism then
                return! ThreadPool.Parallel(mkNestedCts, computations |> Seq.map fst)
            else
                return! Combinators.Parallel state psInfo jobId dependencies faultPolicy computations
        }

        member __.ScheduleChoice computations = cloud {
            if isForcedLocalParallelism then
                return! ThreadPool.Choice(mkNestedCts, (computations |> Seq.map fst))
            else
                return! Combinators.Choice state psInfo jobId dependencies faultPolicy computations
        }

        member __.ScheduleStartAsTask(workflow : Cloud<'T>, faultPolicy, cancellationToken, ?target:IWorkerRef) =
           Combinators.StartAsCloudTask state psInfo jobId dependencies cancellationToken faultPolicy workflow target

        member __.GetAvailableWorkers () = async { 
            let! ws = state.WorkerManager.GetWorkerRefs(showInactive = false)
            return ws |> Seq.map (fun w -> w :> IWorkerRef)
                      |> Seq.toArray 
            }
        member __.CurrentWorker = state.WorkerManager.Current.AsWorkerRef() :> IWorkerRef
        member __.Logger = state.ResourceFactory.RequestProcessLogger(psInfo.Id) 
