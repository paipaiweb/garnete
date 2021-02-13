﻿namespace Garnet.Actors

open System
open System.Collections.Generic
open System.Threading
open Garnet
open Garnet.Comparisons
open Garnet.Formatting

/// Identifies an actor
[<Struct>]
type ActorId =
    val value : int
    new(id) = { value = id }
    override e.ToString() = e.value.ToString()
    
module ActorId =
    let undefined = ActorId 0
    let isAny (id : ActorId) = true
         
/// Provides methods for constructing a batch of messages, sending
/// them upon disposal
type IMessageWriter<'a> =
    inherit IDisposable
    /// Assigns the source actor ID, which is typically the actor
    /// sending the message
    abstract member SetSource : ActorId -> unit
    /// Adds a recipient actor ID
    abstract member AddRecipient : ActorId -> unit
    /// Adds a message to the current batch
    abstract member AddMessage : 'a -> unit

type private NullMessageWriter<'a>() =
    static let mutable instance = new NullMessageWriter<'a>() :> IMessageWriter<'a>
    static member Instance = instance
    interface IMessageWriter<'a> with
        member c.SetSource id = ()
        member c.AddRecipient id = ()
        member c.AddMessage msg = ()
        member c.Dispose() = ()                                          

type IOutbox =
    abstract member BeginSend<'a> : unit -> IMessageWriter<'a>

type NullOutbox() =
    static let mutable instance = NullOutbox()
    static member Instance = instance
    interface IOutbox with
        member c.BeginSend<'a>() =
            NullMessageWriter<'a>.Instance

[<AutoOpen>]
module Outbox =
    type IOutbox with
        member c.BeginSend<'a>(destId) =
            let batch = c.BeginSend<'a>()
            batch.AddRecipient destId
            batch

        member c.Send<'a>(addresses, msg : 'a) =
            use batch = c.BeginSend<'a>(addresses)
            batch.AddMessage msg

        member c.Send<'a>(destId, msg : 'a, sourceId) =
            use batch = c.BeginSend<'a>(destId)
            batch.SetSource sourceId
            batch.AddMessage msg
        
[<Struct>]
type Mail<'a> = {
    outbox : IOutbox
    sourceId : ActorId
    destinationId : ActorId
    message : 'a
    } with
    override c.ToString() =
        sprintf "%d->%d: %A" c.sourceId.value c.destinationId.value c.message
                
type Mail<'a> with
    member c.BeginSend() =
        c.outbox.BeginSend()
    member c.BeginSend(destId) =
        c.outbox.BeginSend(destId)
    member c.BeginRespond() =
        c.BeginSend(c.sourceId)
    member c.Send(destId, msg) =
        use batch = c.BeginSend destId
        batch.AddMessage msg
    member c.Respond(msg) =
        c.Send(c.sourceId, msg)
            
type IInbox =
    abstract member Receive<'a> : Mail<Buffer<'a>> -> unit

type ISubscribable =
    abstract OnAll<'a> : (Mail<Buffer<'a>> -> unit) -> unit

[<AutoOpen>]
module Subscribable =
    type ISubscribable with
        member c.On<'a>(handle : Mail<'a> -> unit) =
            c.OnAll<'a>(fun mail ->
                for i = 0 to mail.message.Count - 1 do
                    handle {
                        message = mail.message.[i]
                        sourceId = mail.sourceId
                        destinationId = mail.destinationId
                        outbox = mail.outbox
                        })

type Inbox() =
    let dict = Dictionary<Type, obj>()
    member c.OnAll<'a>(action : Mail<Buffer<'a>> -> unit) =
        let t = typeof<'a>
        let combined =
            match dict.TryGetValue t with
            | false, _ -> action
            | true, existing -> 
                let existing = existing :?> (Mail<Buffer<'a>> -> unit)
                fun e -> 
                    existing e
                    action e        
        dict.[t] <- combined
    interface ISubscribable with
        member c.OnAll action =
            c.OnAll action
    member c.TryReceive<'a> e =
        match dict.TryGetValue(typeof<'a>) with
        | true, x -> 
            let handle = x :?> (Mail<Buffer<'a>> -> unit)
            handle e
            true
        | false, _ -> false
    interface IInbox with
        member c.Receive e =
            c.TryReceive e |> ignore
    override c.ToString() =
        let str = String.Join(", ", dict.Keys |> Seq.map typeToString |> Seq.sort)
        sprintf "Inbox: %s" str
    
module Mail =
    let private nullOutbox = NullOutbox()

    let empty msg = {
        outbox = nullOutbox
        sourceId = ActorId.undefined
        destinationId = ActorId.undefined
        message = msg
        }

    let map f mail = {
        outbox = mail.outbox
        sourceId = mail.sourceId
        destinationId = mail.destinationId
        message = f mail.message
        }

    let withMessage newMsg mail = {
        outbox = mail.outbox
        sourceId = mail.sourceId
        destinationId = mail.destinationId
        message = newMsg
        }

type IDisposableInbox =
    inherit IDisposable
    inherit IInbox
    
type private NullInbox() =
    interface IInbox with
        member c.Receive e = ()
    interface IDisposable with
        member c.Dispose() = ()
        
module private NullInbox =
    let handler = new NullInbox() :> IInbox

type private InboxCollection(handlers : IInbox[]) =
    interface IInbox with
        member c.Receive<'a> e =
            for handler in handlers do
                handler.Receive<'a> e
    override c.ToString() =
         formatList "Inboxes" handlers.Length (String.Join("\n", handlers))
     
/// Defines how an actor is executed or run
type Execution =
    /// Null endpoint which ignores all messages
    | None = 0
    /// Routes incoming messages to another actor ID
    | Route = 1
    /// Actor which can be run on a background thread
    | Default = 2
    /// Actor which must be run on the main thread
    | Main = 3

[<Struct>]
type Actor = {
    routedId : ActorId
    execution : Execution
    inbox : IInbox
    dispose : unit -> unit
    }

module Actor =
    let init exec inbox dispose = {
        routedId = ActorId.undefined
        execution = exec
        inbox = inbox
        dispose = dispose
        }

    let none = init Execution.None NullInbox.handler ignore

    let route routedId = { 
        none with 
            routedId = routedId 
            execution = Execution.Route
            }

    let disposable inbox dispose =
        init Execution.Default inbox dispose

    let inbox inbox = disposable inbox ignore

    let handler register = 
        let inbox = Inbox()
        register inbox
        disposable inbox ignore

    let execMain a =
        { a with execution = Execution.Main }

    let combine actors =
        let actors = actors |> Seq.toArray
        if actors.Length = 0 then none
        else
            let inboxes = actors |> Array.map (fun d -> d.inbox)
            let exec = 
                actors 
                |> Seq.map (fun a -> a.execution) 
                |> Seq.reduce max
            let inbox = InboxCollection(inboxes) :> IInbox
            let dispose = fun () -> for d in actors do d.dispose() 
            init exec inbox dispose

type internal ActorFactoryCollection() =
    let factories = List<_>()
    member c.Add(desc : ActorId -> Actor) =
        factories.Add(desc)
    member c.Create actorId =
        // first priority is execution type, then order where last wins
        let mutable result = Actor.none
        let mutable i = factories.Count - 1
        while int result.execution <> int Execution.Default && i >= 0 do
            let actor = factories.[i] (ActorId actorId)
            if int actor.execution > int result.execution then
                result <- actor
            i <- i - 1
        result

module ActorFactory =
    let route map =
        fun (id : ActorId) -> Actor.route (map id)

    let filter canCreate create =
        fun id -> if canCreate id then create id else Actor.none

    let any create =
        filter (fun id -> true) create

    let init actorId create =
        filter ((=)actorId) (fun id -> create())

    let filterHandler canCreate register =
        filter canCreate (fun id -> Actor.handler (register id))

    let handler actorId register =
        init actorId (fun () -> Actor.handler register)

    let map (f : Actor -> Actor) create =
        fun (id : ActorId) -> create id |> f

    let combine factories =
        let collection = ActorFactoryCollection()
        for f in factories do collection.Add f
        fun (id : ActorId) -> collection.Create id.value

[<AutoOpen>]
module Inbox =
    type Inbox with
        member c.On<'a>(handler) =
            c.OnAll<'a> <| fun e -> 
                for i = 0 to e.message.Count - 1 do
                    handler (e.message.[i])

        member c.OnMail<'a>(action) =
            c.OnAll<'a> <| fun e -> 
                for msg in e.message do
                    action { 
                        sourceId = e.sourceId
                        destinationId = e.destinationId
                        outbox = e.outbox
                        message = msg }

    type IOutbox with
        member c.BeginSend<'a>(destId) =
            let batch = c.BeginSend<'a>()
            batch.AddRecipient destId
            batch
        member c.Send<'a>(destId, msg) =
            use batch = c.BeginSend<'a>(destId)
            batch.AddMessage msg
        member c.Send<'a>(destId, msg, sourceId) =
            use batch = c.BeginSend<'a>(destId)
            batch.SetSource sourceId
            batch.AddMessage msg
        member c.SendAll<'a>(destId, msgs : Buffer<'a>) =
            use batch = c.BeginSend<'a>(destId)
            for msg in msgs do
                batch.AddMessage msg
        member c.SendAll<'a>(destId, msgs : Buffer<'a>, sourceId) =
            use batch = c.BeginSend<'a>(destId)
            batch.SetSource sourceId
            for msg in msgs do
                batch.AddMessage msg
    
[<Struct>]
type Disposable =
    val onDispose : unit -> unit
    new(onDispose) = { onDispose = onDispose }
    interface IDisposable with
        member c.Dispose() = c.onDispose()

/// Need sender member indirection because container registrations
/// need permanent reference while incoming messages have varying sender
type internal Outbox() =
    let nullOutbox = NullOutbox() :> IOutbox
    let mutable batchCount = 0
    let mutable pushCount = 0
    let mutable popCount = 0
    let outboxStack = 
        let s = Stack<_>()
        s.Push(nullOutbox)
        s
    let popOutbox() = 
        popCount <- popCount + 1
        outboxStack.Pop() |> ignore
    let scope = new Disposable(popOutbox)
    /// Set temporary outbox for a scope such as handling incoming message
    member c.PushOutbox outbox = 
        outboxStack.Push outbox
        pushCount <- pushCount + 1
        scope
    /// Create an outgoing message batch which is sent on batch disposal
    member c.BeginSend() =             
        // get current outbox
        let batch = outboxStack.Peek().BeginSend()
        batchCount <- batchCount + 1
        batch
    interface IOutbox with
        member c.BeginSend() = c.BeginSend()            
    override c.ToString() =
        sprintf "Outbox: %d outboxes, %d batches, %d/%d push/pop" outboxStack.Count batchCount pushCount popCount
        