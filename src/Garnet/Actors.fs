﻿namespace Garnet.Actors

open System
open System.Threading
open System.Collections.Generic
open System.Collections.Concurrent
open Garnet.Comparisons

[<AutoOpen>]
module private Utility =
    let inline isNull x = obj.ReferenceEquals(x, null)
    let inline isNotNull x = not (isNull x)

    let dispose (d : IDisposable) = d.Dispose()

    let private getNonGenericTypeName (name : string) =
        let last = name.LastIndexOf('`')
        if last < 0 then name else name.Substring(0, last)

    let rec typeToString (t : Type) =
        let name = getNonGenericTypeName t.Name
        let args = t.GetGenericArguments()
        if args.Length = 0 then name
        else sprintf "%s<%s>" name (String.Join(",", args
            |> Seq.map typeToString |> Seq.toArray))
    
    let addIndent (str : string) =
        str.Replace("\n", "\n  ")

    let formatList name count (str : string) =
        sprintf "%s (%d)%s" name count
            (if count > 0 then ":\n  " + addIndent str else "")

type ActorOptions = {
    threadCount : int
    processLimit : int
    }

module ActorOptions =
    // Allow at least one background thread by default
    let defaultOptions = {
        threadCount = Environment.ProcessorCount - 1 |> max 1
        processLimit = Int32.MaxValue
    }

    let noBackgroundThreads = {
        threadCount = 0
        processLimit = Int32.MaxValue
    }

[<AutoOpen>]
module Queueing =
    let defaultBufferSize = 32

    /// Single producer, single consumer
    type RingBuffer<'a>(size) =
        let buffer = Array.zeroCreate<'a> (size)
        [<VolatileField>]
        let mutable readPos = 0
        [<VolatileField>]
        let mutable writePos = 0
        new() = new RingBuffer<'a>(defaultBufferSize)
        /// Assumes single thread access
        member c.TryEnqueue(item) =
            if writePos - readPos = buffer.Length then false
            else
                let index = writePos &&& (buffer.Length - 1)
                buffer.[index] <- item
                writePos <- writePos + 1
                true
        /// Assumes single thread access
        member c.TryDequeue(item : byref<'a>) =
            if readPos = writePos then false
            else
                let index = readPos &&& (buffer.Length - 1)
                item <- buffer.[index]
                readPos <- readPos + 1
                true

    /// Single producer, single consumer
    [<AllowNullLiteral>]
    type RingBufferNode<'a>(size) =
        let buffer = RingBuffer(size)
        [<VolatileField>]
        let mutable next = null
        new() = new RingBufferNode<'a>(defaultBufferSize)
        member c.Enqueue(item) =
            // first try to add to current
            if buffer.TryEnqueue(item) then c
            else
                // if full, create a new node
                // next will only ever be set to non-null
                // need to guarantee this value will be written AFTER ring buffer
                // increment, otherwise consumer could miss an item
                next <- RingBufferNode(size * 2)
                next.Enqueue(item)
        /// Returns the node item was obtained from, or null if no item available
        member c.TryDequeue(item : byref<'a>) =
            // first look in current
            if buffer.TryDequeue(&item) then c
            // if empty, then either no items or writer moved onto another buffer
            // if another buffer, we can safely discard current buffer
            elif isNotNull next then next.TryDequeue(&item)
            else null
        member c.NodeCount = 
            if isNull next then 1 else 1 + next.NodeCount
            
    /// Single producer, single consumer
    type RingBufferQueue<'a>(initialSize) =
        let mutable count = 0
        let mutable enqueueCount = 0
        let mutable dequeueCount = 0
        let mutable allocatedCount = 1
        [<VolatileField>]
        let mutable readNode = RingBufferNode<'a>(initialSize)
        [<VolatileField>]
        let mutable writeNode = readNode
        new() = new RingBufferQueue<'a>(defaultBufferSize)
        member c.Count = count
        member c.Enqueue(item) =
            writeNode <- writeNode.Enqueue(item)
            Interlocked.Increment(&count) |> ignore
            enqueueCount <- enqueueCount + 1
        member c.TryDequeue(item : byref<'a>) =
            let newReadNode = readNode.TryDequeue(&item)
            let isDequeued = isNotNull newReadNode
            if isDequeued then 
                if not (obj.ReferenceEquals(readNode, newReadNode)) then
                    readNode <- newReadNode
                    allocatedCount <- allocatedCount + 1
                Interlocked.Decrement(&count) |> ignore
                dequeueCount <- dequeueCount + 1
            isDequeued
        member c.DequeueAll(action : Action<_>) =
            let mutable item = Unchecked.defaultof<'a>
            while c.TryDequeue(&item) do
                action.Invoke item
        override c.ToString() =
            sprintf "%d items, %d/%d nodes, %d enqueued, %d dequeued" 
                count readNode.NodeCount allocatedCount enqueueCount dequeueCount
                
    type RingBufferPool<'a>(create) =
        let pool = RingBufferQueue<'a>()
        let onDispose = Action<_>(pool.Enqueue)
        member c.Get() =
            let mutable item = Unchecked.defaultof<'a>
            if pool.TryDequeue(&item) then item
            else create onDispose
        override c.ToString() =
            pool.ToString()

[<AutoOpen>]        
module private Internal =
    type IMessage =
        inherit IDisposable
        abstract member Handle : IOutbox -> ActorId -> IMessageHandler -> unit

    type NullMessage() =
        interface IMessage with
            member c.Handle outbox destId handler = ()
        interface IDisposable with
            member c.Dispose() = ()
        
    [<Struct>]     
    type MessageResult = {
        message : IMessage
        ex : Exception
        }
            
    [<Struct>]
    type MessageInfo = {
        message : IMessage
        destinationId : ActorId }
    
    type MessageState<'a> = {
        mutable sourceId : ActorId
        mutable channelId : int
        recipients : List<ActorId>
        messages : List<'a>
        } with
        member c.CopyTo (dest : IMessageWriter<'a>) =
            dest.SetChannel c.channelId
            dest.SetSource c.sourceId
            for id in c.recipients do dest.AddRecipient id
            for msg in c.messages do dest.AddMessage msg
        member c.CopyFrom (src : MessageState<'a>) =
            c.channelId <- src.channelId
            c.sourceId <- src.sourceId
            for id in src.recipients do c.recipients.Add id
            for msg in src.messages do c.messages.Add msg
        member c.Clear() =
            c.channelId <- 0
            c.sourceId <- ActorId.undefined
            c.recipients.Clear()
            c.messages.Clear()
        override c.ToString() =
            let sb = System.Text.StringBuilder()
            sb.AppendLine(sprintf "Source: %A" c.sourceId) |> ignore
            sb.AppendLine(sprintf "Recipients (%d): %s" c.recipients.Count 
                (String.Join(", ", c.recipients))) |> ignore
            sb.Append(sprintf "%s batch (%d%d):" (typeof<'a> |> typeToString) 
                c.messages.Count c.messages.Capacity) |> ignore
            if c.messages.Count > 0 then
                for msg in c.messages do
                    sb.AppendLine().Append(sprintf "%A" msg) |> ignore
            sb.ToString()

    /// Supports multiple messages and multiple recipients
    type MessageBatch<'a>(capacity : int, onDispose : Action<_>) =
        let mutable disposeCount = 0
        let state = {
            sourceId = ActorId.undefined
            channelId = 0
            recipients = List<ActorId>()
            messages = List<'a>(capacity)
        }
        member c.Send(newState, send : Action<_>) = 
            state.CopyFrom newState
            if state.messages.Count > 0 then
                // Iterate over original recipients, not copied value.
                // This is because we are passing write control as soon as
                // we invoke send, which could clear recipient list.
                for recipientId in newState.recipients do
                    send.Invoke { 
                        destinationId = recipientId
                        message = c :> IMessage }
            newState.Clear()
        interface IMessage with
            member c.Handle outbox destId handler =
                handler.Handle {
                    outbox = outbox
                    sourceId = state.sourceId
                    channelId = state.channelId
                    destinationId = destId 
                    message = state.messages }
        interface IDisposable with
            member c.Dispose() =
                // This is called once receiver has handled message or message
                // cannot be delivered and is discarded. Multiple recipients will cause 
                // this to be called multiple times, so we only do real disposal on
                // last call.
                disposeCount <- disposeCount + 1
                if disposeCount = state.recipients.Count then
                    disposeCount <- 0
                    state.Clear()
                    onDispose.Invoke c
            
    type MessageWriter<'a>(onDispose : Action<_>) =
        let state = {
            sourceId = ActorId.undefined
            channelId = 0
            recipients = List<ActorId>()
            messages = List<'a>()
        }
        member c.State = state
        interface IMessageWriter<'a> with
            member c.SetChannel id = state.channelId <- id
            member c.SetSource id = state.sourceId <- id
            member c.AddRecipient id = state.recipients.Add(id)
            member c.AddMessage x = state.messages.Add(x)
            member c.Dispose() = onDispose.Invoke c

    let log2 x =
        let mutable log = 0
        let mutable y = x
        while y > 1 do
            y <- y >>> 1
            log <- log + 1;
        log

    let nextLog2 x =
        let log = log2 x
        if x - (1 <<< log) > 0 then 1 + log else log
        
    /// Owns message wrapper pool (thread-safe as single producer single consumer)
    type Outbox<'a>(send) =
        // accessed from sender and then from main on disposal
        let pools = Array.init 30 (fun i -> RingBufferPool(fun onDispose -> 
            new MessageBatch<'a>(1 <<< i, onDispose)))
        let mutable poolMask = 0
        let mutable sentCount = 0
        // accessed only from thread building batch
        let builders = Stack<_>()
        let sendBatch = Action<MessageWriter<'a>>(fun builder ->
            let state = builder.State
            let pow2Size = nextLog2 state.messages.Count
            sentCount <- sentCount + state.messages.Count
            // mark as used
            poolMask <- poolMask ||| (1 <<< pow2Size)
            let pool = pools.[pow2Size]
            let batch = pool.Get()
            batch.Send(state, send)
            builders.Push(builder))
        member c.StartBatch(sourceId) = 
            let b =
                if builders.Count = 0 then new MessageWriter<'a>(sendBatch)
                else builders.Pop()
            b.State.sourceId <- sourceId
            b :> IMessageWriter<'a>
        override c.ToString() =
            let usedPools = 
                [ 0..pools.Length - 1] 
                |> Seq.filter (fun i -> poolMask &&& (1 <<< i) <> 0)
                |> Seq.map (fun i -> sprintf "%d: %s" i (pools.[i].ToString()))
                |> Seq.toArray
            let size = sizeof<'a>
            let total = sentCount * size
            let detail = sprintf ": %d sent * %d = %d bytes, pools" sentCount size total
            (formatList detail usedPools.Length (String.Join("\n", usedPools)))

    type IActorOutbox =
        inherit IOutbox
        abstract member SetSource : ActorId -> unit

    /// Not thread-safe        
    type Outbox(send) =
        let mutable sourceId = ActorId.undefined
        let dict = Dictionary<Type, obj>()
        member c.Get<'a>() =
            let t = typeof<'a>
            let pool =
                match dict.TryGetValue(t) with
                | true, x -> x :?> Outbox<'a>
                | false, _ ->
                    let pool = Outbox<'a>(send)
                    dict.Add(t, pool)
                    pool
            pool
        member c.StartBatch() =
            c.Get<'a>().StartBatch(sourceId)
        // Should be called on worker thread
        interface IActorOutbox with
            member c.SetSource id = sourceId <- id
            member c.StartBatch() =
                c.StartBatch()
        override c.ToString() =
            formatList "Outboxes" dict.Count
                (String.Join("\n", dict |> Seq.map (fun kvp -> 
                    sprintf "%s%s" (typeToString kvp.Key) (kvp.Value.ToString()))))
            
    type QueueOutbox() =
        let sendQueue = RingBufferQueue<MessageInfo>()
        let send = Action<_>(sendQueue.Enqueue)
        let outbox = Outbox(send)
        member c.SendAll(send : Action<_>) =
            let mutable count = 0
            let mutable msg = Unchecked.defaultof<_>
            while sendQueue.TryDequeue(&msg) do
                send.Invoke msg
                count <- count + 1
            count
        // Should be called on worker thread
        interface IActorOutbox with
            member c.SetSource id = 
                (outbox :> IActorOutbox).SetSource id
            member c.StartBatch() =
                outbox.StartBatch()
        override c.ToString() =
            sprintf "Queue: %s\n%s" (sendQueue.ToString()) (outbox.ToString())
 
    [<Struct>]
    type QueuedMessage = {
        message : IMessage
        destId : ActorId
        }

    type Actor(actorId : ActorId, definition : ActorDefinition, onProcess : Action) =
        let mutable lock = 0
        let mutable total = 0
        let receiveQueue = RingBufferQueue<QueuedMessage>()
        // Called by main thread when distributing messages
        member c.Enqueue(msg, destId) =
            receiveQueue.Enqueue {
                message = msg
                destId = destId
                }
        /// Single thread runs actor at a time, writing to send queue and returning
        /// number of incoming messages processed.
        member c.Run(outbox : IActorOutbox, disposeMsg : Action<_>, options) =
            let mutable processed = 0
            let isLocked = Interlocked.Increment(&lock) = 1
            if isLocked then
                outbox.SetSource actorId
                let mutable msg = Unchecked.defaultof<QueuedMessage>
                while processed < options.processLimit && receiveQueue.TryDequeue(&msg) do
                    let ex =
                        try
                            msg.message.Handle outbox msg.destId definition.handler
                            processed <- processed + 1
                            onProcess.Invoke()
                            null
                        with
                        | ex -> exn(sprintf "Error running actor %d message:\n%s" actorId.value (msg.ToString()), ex) 
                    let result = { message = msg.message; ex = ex }
                    disposeMsg.Invoke result
                outbox.SetSource ActorId.undefined
            // if done, keep locked so we don't do any further processing
            Interlocked.Decrement(&lock) |> ignore
            total <- total + processed
            processed
        override c.ToString() =
            sprintf "[%d] %d pending, %d processed, %s" actorId.value receiveQueue.Count total (receiveQueue.ToString())
        interface IDisposable with
            member c.Dispose() =
                definition.dispose()
        
    /// Not thread-safe
    /// All methods should be called from main thread
    type BackgroundWorker(options, name) =
        [<VolatileField>]
        let mutable isRunning = true
        let wait = new AutoResetEvent(false)
        let outbox = QueueOutbox()
        let disposalQueue = RingBufferQueue<MessageResult>()
        let actorQueue = RingBufferQueue<Actor>()
        let actors = List<Actor>()
        let addActor = Action<Actor>(actors.Add)
        let enqueueDisposal = Action<_>(disposalQueue.Enqueue)
        let runOnce() =
            let count = actors.Count
            let mutable processed = 0
            for i = 0 to count - 1 do
                let actor = actors.[i]
                processed <- processed + actor.Run(outbox, enqueueDisposal, options)
            processed
        let run() =
            while isRunning do
                actorQueue.DequeueAll addActor |> ignore
                /// Keep trying to run until no more messages processed
                while runOnce() > 0 do ()
                wait.WaitOne() |> ignore
        let thread =
            let t = Thread(run)
            t.Name <- name
            t.Start()
            t
        member c.Run() =        
            wait.Set() |> ignore
        // Called from main thread to distribute messages to other actors/workers
        // This includes disposals, which are owned by other workers
        member c.SendAll(send, disposeMsg) =
            disposalQueue.DequeueAll disposeMsg
            outbox.SendAll send
        member c.Add(actor) =
            actorQueue.Enqueue(actor)
        member c.NotifyStopping() =
            isRunning <- false
            wait.Set() |> ignore
        interface IDisposable with
            member c.Dispose() =
                isRunning <- false
                thread.Join()
                wait.Dispose()
        override c.ToString() =
            sprintf "%s\nDisposals: %s" (outbox.ToString()) (disposalQueue.ToString())

    /// Not thread-safe
    /// All methods should be called from main thread
    type BackgroundWorkers(options) =
        let procs = Array.init options.threadCount (fun i -> 
            new BackgroundWorker(options, sprintf "Worker%d" i))
        member c.Add(actor) =
            for proc in procs do
                proc.Add(actor)
        member c.SendAll(send, disposeMsg) =
            let mutable count = 0
            for proc in procs do
                count <- count + proc.SendAll(send, disposeMsg)
            count
        member c.Run() =
            for proc in procs do
                proc.Run()
        interface IDisposable with
            member c.Dispose() =
                for proc in procs do
                    proc.NotifyStopping()
                for proc in procs do
                    (proc :> IDisposable).Dispose()
        override c.ToString() =
            (formatList "Workers" procs.Length (String.Join("\n", procs)))

    // Not thread-safe
    type DescriptorLookup() =
        let factories = List<_>()
        member c.Add(desc) =
            factories.Add(desc)
        member c.GetDescriptor(id : ActorId) =
            // find most specific factory for id
            //let mutable minMask = Int32.MaxValue
            let mutable minDesc = ActorRule.empty
            for desc in factories do
                if desc.canCreate id then
                    //minMask <- desc.idMask
                    minDesc <- desc
            minDesc

    type Redirector() =
        let rules = List<_>()
        member c.Add rule =
            rules.Add rule
        member c.MapId id =
            let mutable mappedId = id
            for map in rules do
                mappedId <- map mappedId
            mappedId

    type Actors(options, onProcess) =
        let actorLookup = Dictionary<int, Actor>()
        let bgActors = List<Actor>()
        let fgActors = List<Actor>()
        let workers = new BackgroundWorkers(options)
        let descLookup = DescriptorLookup()
        let redirector = Redirector()
        member c.Count = actorLookup.Count
        member c.ForegroundCount = fgActors.Count
        member c.Register rule =
            match rule with
            | ActorFactory f -> descLookup.Add f
            | ActorRedirect map -> redirector.Add map
        member c.GetActor (destId : ActorId) =
            let destId = redirector.MapId destId
            match actorLookup.TryGetValue(destId.value) with
            | true, x -> x
            | false, _ ->
                let desc = descLookup.GetDescriptor destId
                let actor = new Actor(destId, desc.create destId, onProcess)
                actorLookup.Add(destId.value, actor) |> ignore
                if desc.isBackground && options.threadCount > 0 
                    then 
                        bgActors.Add actor
                        workers.Add(actor) 
                    else fgActors.Add(actor)
                actor
        member c.SendAll(send, disposeMsg) =
            // Send messages from each worker outbox to destination inbox and
            // count how many messages have been disposed
            workers.SendAll(send, disposeMsg)
        member c.Run(fgOutbox, disposeMsg) =
            workers.Run()
            // Run foreground worker, which directly sends to inboxes
            let count = fgActors.Count
            let mutable processed = 0
            for i = 0 to count - 1 do
                let actor = fgActors.[i]
                processed <- processed + actor.Run(fgOutbox, disposeMsg, options)
            processed
        member c.Dispose() =
            dispose workers
            for actor in bgActors do dispose actor
            for actor in fgActors do dispose actor
        interface IDisposable with
            member c.Dispose() = c.Dispose()
        override c.ToString() =
            sprintf "%s\n%s\n%s"
                (formatList "FG" fgActors.Count (String.Join("\n", fgActors)))
                (formatList "BG" bgActors.Count (String.Join("\n", bgActors)))
                (workers.ToString())

    type ActorSender(actors : Actors) =
        let mutable sentCount = 0
        member c.SentCount = sentCount
        member c.Send (info : MessageInfo) =
            // should be called on main thread
            let actor = actors.GetActor info.destinationId
            actor.Enqueue(info.message, info.destinationId)
            sentCount <- sentCount + 1

    type Disposer() =
        let mutable receivedCount = 0
        member c.DisposedCount = receivedCount
        member c.Receive (result : MessageResult) =
            // should be called on main thread
            result.message.Dispose()
            receivedCount <- receivedCount + 1
            if isNotNull result.ex then 
                exn("Error running actor", result.ex) |> raise

type IMessagePump =
    inherit IOutbox
    inherit IDisposable
    abstract member Run : unit -> unit
    abstract member RunAll : unit -> unit

/// Not thread-safe, specify zero threads to disable BG workers
type ActorSystem(options) =
    let mutable receivedCount = 0
    let onProcess = Action(fun () -> 
        Interlocked.Increment &receivedCount |> ignore)
    let nullMessage = new NullMessage()
    let actors = new Actors(options, onProcess)
    let receiver = Disposer()
    let sender = ActorSender(actors)
    let disposeMsg = Action<_>(receiver.Receive)
    let send = Action<_>(sender.Send)
    let fgOutbox = Outbox(send)
    new() = new ActorSystem(ActorOptions.defaultOptions)
    new(threadCount) = new ActorSystem({ ActorOptions.defaultOptions with threadCount = threadCount })
    member private c.PendingCount = 
        sender.SentCount - receiver.DisposedCount
    member c.Register desc =
        actors.Register desc
    member c.StartBatch() =
        fgOutbox.StartBatch()
    member c.Create(destId) =
        // Send an empty message just to force creation of actor
        send.Invoke { destinationId = destId; message = nullMessage }
    member c.RunOnce() =
        let sent = actors.SendAll(send, disposeMsg)
        // If there are outstanding messages in system, run workers
        let processed = if c.PendingCount > 0 then actors.Run(fgOutbox, disposeMsg) else 0
        sent + processed > 0
    /// Runs until all foreground work is done
    member c.Run() =
        while c.RunOnce() do ()                
    /// Sleep/poll while background threads complete
    member c.RunAll() =
        c.Run()
        while c.PendingCount > 0 do
            c.Run()
            Thread.Sleep(1)
    member c.Dispose() =
        actors.Dispose()
    interface IMessagePump with
        member c.Run() = c.Run()
        member c.RunAll() = c.RunAll()
        member c.StartBatch() =
            c.StartBatch()
    interface IDisposable with
        member c.Dispose() = c.Dispose()
    override c.ToString() =
        sprintf "Messages: %d pending, %d sent, %d received, %d disposed\nActors\n  %s\n  %s"
            c.PendingCount sender.SentCount receivedCount receiver.DisposedCount
            (addIndent (actors.ToString()))
            (addIndent (fgOutbox.ToString()))

[<Struct>]
type ActorReference = {
    actorId : ActorId
    pump : IMessagePump
    } with
    override c.ToString() = c.actorId.ToString()

[<AutoOpen>]
module Extensions =
    type IOutbox with
        member c.StartBatch<'a>(destId) =
            let batch = c.StartBatch<'a>()
            batch.AddRecipient destId
            batch
        member c.Send<'a>(destId, msg) =
            c.Send<'a>(destId, msg, ActorId.undefined)
        member c.Send<'a>(destId, msg, sourceId) =
            c.Send<'a>(destId, msg, sourceId, 0)
        member c.Send<'a>(destId, msg, sourceId, channelId) =
            use batch = c.StartBatch<'a>(destId)
            batch.SetSource sourceId
            batch.SetChannel channelId
            batch.AddMessage msg
        member c.SendAll<'a>(destId, msgs : List<'a>) =
            c.SendAll<'a>(destId, msgs, ActorId.undefined)
        member c.SendAll<'a>(destId, msgs : List<'a>, sourceId) =
            c.SendAll<'a>(destId, msgs, sourceId, 0)
        member c.SendAll<'a>(destId, msgs : List<'a>, sourceId, channelId) =
            use batch = c.StartBatch<'a>(destId)
            batch.SetSource sourceId
            batch.SetChannel channelId
            for msg in msgs do
                batch.AddMessage msg

    type IMessagePump with
        member c.Get id = {
            actorId = id
            pump = c
            }

        member c.Run<'a>(destId, msg : 'a) =
            c.Send(destId, msg)
            c.Run()

    type ActorSystem with
        member c.RegisterAll entries =
            for desc in entries do
                c.Register(desc)

    type ActorReference with
        member c.Send msg =
            c.pump.Send(c.actorId, msg)

        member c.SendAll msg =
            c.pump.SendAll(c.actorId, msg)

        member c.Run msg =
            c.pump.Run(c.actorId, msg)
    
/// Not pooled, intended for sending messages to actor system
/// running on another thread
type QueueingMessagePump(enqueue) =
    interface IMessagePump with
        member c.Run() = ()
        member c.RunAll() = ()
        member c.Dispose() = ()
        member c.StartBatch<'a>() =
            new MessageWriter<'a>(fun writer ->
                enqueue (fun (c : IOutbox) -> 
                    use batch = c.StartBatch()
                    writer.State.CopyTo batch))
            :> IMessageWriter<'a>

/// Runs messages against pump in background
type BackgroundMessagePump(system : IMessagePump, update) =
    let mutable isRunning = true
    let nullCommand a = ()
    let queue = ConcurrentQueue<IOutbox -> unit>()
    let dispatcher = 
        new QueueingMessagePump(queue.Enqueue)
        :> IMessagePump
    let thread = 
        let t = System.Threading.Thread(fun () ->
            while isRunning do
                try 
                    let mutable run = nullCommand
                    while queue.TryDequeue(&run) do
                        run system
                    update system
                    System.Threading.Thread.Sleep(1)
                with ex -> printfn "%A" ex)    
        t.Start()
        t
    member c.Dispose() = 
        isRunning <- false
        thread.Join()
        dispatcher.Dispose()
    interface IMessagePump with
        member c.Run() = dispatcher.Run()
        member c.RunAll() = dispatcher.RunAll()
        member c.StartBatch<'a>() = dispatcher.StartBatch<'a>()
        member c.Dispose() = c.Dispose()
    
type Sender = IOutbox -> unit

module Sender =
    let send actorId msg (a : IOutbox) =
        a.Send(actorId, msg)

    let sendAll actorId msg (a : IOutbox) =
        a.SendAll(actorId, msg)

    let list list =
        fun (a : IOutbox) ->
            for send in list do
                send a
            