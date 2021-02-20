﻿module Garnet.Benchmarks.PingPong

open System.Collections.Generic
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open Garnet.Composition

type RecordedMessage = {
    sourceId : ActorId 
    destId : ActorId 
    sequence : int
    payload : int64
    dispatcher : string
    timestamp : int64
    }

module RecordedMessage =
    let formatPair (s, r) =
        sprintf "%d from [%d] a%d (d%s) to [%d] a%d (d%s) in %d" 
            r.payload 
            s.sequence s.sourceId.value s.dispatcher 
            r.sequence r.destId.value r.dispatcher 
            (r.timestamp - s.timestamp)
        
type SynchronizedQueue<'a>() =
    let incoming = Queue<'a>()
    let sync = obj()
    member c.Enqueue x =
        Monitor.Enter sync
        incoming.Enqueue x
        Monitor.Exit sync
    member c.TryDequeue([<Out>] item : byref<_>) =
        Monitor.Enter sync
        let r = incoming.Count > 0
        if r then item <- incoming.Dequeue()
        Monitor.Exit sync
        r        
    member c.Flush() = seq {
        let mutable item = Unchecked.defaultof<'a>
        while c.TryDequeue(&item) do
            yield item
        }

module Tests =
    let runLogging log onSend onReceive actorCount workerCount duration initCount maxCount batchSize =
        let count = ref 0
        use a = new ActorSystem(workerCount)
        a.Register <| fun actorId ->
            let inbox = Mailbox()
            inbox.OnAll<int64> <| fun e ->
                let c = Interlocked.Increment count
                if log then
                    onReceive { 
                        sourceId = ActorId.undefined
                        destId = actorId
                        sequence = c
                        payload = e.[0]
                        timestamp = Stopwatch.GetTimestamp()
                        dispatcher = inbox.ToString()
                        }
                if c <= maxCount then
                    let rand = uint64 c * 2862933555777941757UL + 3037000493UL
                    let destId = (abs (int rand) % actorCount) + 1 |> ActorId
                    if duration > 0 then
                        Thread.Sleep duration
                    if log  then
                        let nextItem = e.[0] + 1L
                        onSend { 
                            sourceId = actorId
                            destId = destId
                            sequence = c
                            payload = nextItem
                            timestamp = Stopwatch.GetTimestamp()
                            dispatcher = inbox.ToString()
                            }
                    use batch = inbox.BeginSend(destId)
                    for i = 0 to e.Count - 1 do
                        batch.Write(e.[i] + 1L)
            Actor.inbox inbox
        for i = 0 to initCount - 1 do
            let destId = (i % actorCount) + 1 |> ActorId
            let payload = (i + 1) * 10000000
            use batch = a.BeginSend(destId)
            for i = 0 to batchSize - 1 do
                batch.Write (payload + i * 10000 |> int64)
            if log  then
                onSend { 
                    sourceId = ActorId.undefined
                    destId = destId
                    sequence = 0
                    payload = payload |> int64
                    timestamp = Stopwatch.GetTimestamp()
                    dispatcher = ""
                    }
        a.RunAll()
        let expected = maxCount + initCount
        let actual = count.Value
        if actual <> expected then
            printfn "Expected count: %d, actual: %d" expected actual
        if log then
            printfn "%s" (a.ToString())

    let run = runLogging false ignore ignore

    let runHistory log actorCount workerCount duration initCount maxCount batchSize =
        let sent = SynchronizedQueue<_>()
        let received = SynchronizedQueue<_>()
        runLogging log sent.Enqueue received.Enqueue actorCount workerCount duration initCount maxCount batchSize
        let sent = sent.Flush() |> Seq.toArray
        let received = received.Flush() |> Seq.toArray
        let sentSet = sent |> Seq.map (fun x -> (x.destId, x.payload), x) |> Map.ofSeq
        received |> Seq.map (fun x -> sentSet.[x.destId, x.payload], x) |> Seq.toArray

    let runMain log useMain workerCount initCount maxCount =
        let maxActorCount = maxCount * 2 - initCount
        let count = ref 0
        let createInbox id =
            let inbox = Mailbox()
            inbox.OnAll<int64> <| fun e ->
                if Interlocked.Increment count <= maxActorCount then
                    use m = inbox.BeginRespond()
                    for i = 0 to e.Count - 1 do
                        m.Write e.[i]
            inbox
        use a = new ActorSystem(workerCount)
        a.Register(ActorFactory.init (ActorId 1) (fun () ->
            createInbox()
            |> Actor.inbox))
        a.Register(ActorFactory.init (ActorId 2) (fun () ->
            createInbox()
            |> Actor.inbox
            |> (if useMain then Actor.execMain else id)))
        for i = 1 to initCount do
            a.Send(ActorId 1, int64 i, ActorId 2)
        a.RunAll()
        if log then
            printfn "%s\n%d" (a.ToString()) count.Value
