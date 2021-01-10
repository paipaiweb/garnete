# Garnet

[![Build status](https://ci.appveyor.com/api/projects/status/g82kak7btxp48rnd?svg=true)](https://ci.appveyor.com/project/bcarruthers/garnet)

Garnet is a lightweight game composition library for F# with entity-component-system (ECS) and actor-like messaging features.

```fsharp
open Garnet.Ecs

// events
[<Struct>] type Update = { dt : float32 }

// components
[<Struct>] type Position = { x : float32; y : float32 }
[<Struct>] type Velocity = { vx : float32; vy : float32 }

// create a world
let world = Container()

// register a system that updates position
let system =
    world.On<Update> (
        fun e struct(p : Position, v : Velocity) -> {
            x = p.x + v.vx * e.dt
            y = p.y + v.vy * e.dt
            }
        |> Iter.update2
        |> Iter.over world)

// add an entity to world
let entity = 
    world.Create()
        .With({ x = 10.0f; y = 5.0f })
        .With({ vx = 1.0f; vy = 2.0f })

// run updates and print world state
for i = 1 to 10 do
    world.Run <| { dt = 0.1f }
    printfn "%O\n\n%O\n\n" world entity
```

## Table of contents
* [Background](#background)
* [Goals](#goals)
* [Building](#building)
* Guide
    * [Containers](#containers)
    * [Entities](#entities)
    * [Components](#components)
    * [Systems](#systems)
    * [Actors](#actors)
    * [Integration](#integration)
* [Roadmap](#roadmap)
* [FAQ](#faq)
* [License](#license)
* [Maintainers](#maintainers)

## Background

Garnet emerged from [Triverse](http://cragwind.com/blog/posts/grid-projectiles/), a 2D game under development where players build and command drone fleets in a large tri-grid world. The game serves as a performance testbed and ensures the library meets the actual needs of at least one moderately complex game.

ECS is a common architecture for games, often contrasted with OOP inheritance. It focuses on separation of data and behavior and is typically implemented in a data-oriented way to achieve high performance. It's similar to a database, where component tables are related using a common entity ID, allowing systems to query and iterate over entities with specific combinations of components present. EC (entity-component) is a related approach that attaches behavior to components and avoids systems.

While ECS focuses on managing shared state, the actor model isolates state into separate actors which communicate only through messages. Actors can send and receive messages, change their behavior as a result of messages, and create new actors. This approach offers scaleability and an abstraction layer over message delivery, and games can use it at a high level to model independent processes, worlds, or agents.

## Goals

- **Lightweight**: Garnet is essentially a simplified in-memory database and messaging system suitable for games. No inheritance, attributes, or interface implementations are required in your code. It's more of a library than a framework or engine, and most of your code shouldn't depend on it.

- **Fast**: Garbage collection spikes can cause dropped frames and inconsistent performance, so Garnet minimizes allocations and helps library users do so too. Component storage is data-oriented for fast iteration.

- **Minimal**: The core library focuses on events, scheduling, and storage, and anything game-specific like physics, rendering, or update loops should be implemented separately.

- **Complete**: In addition to traditional ECS, Garnet provides actor-like messaging for scenarios where multiple ECS worlds are beneficial, such as AI agents or networking.

## Building

1. Install [.NET Core SDK](https://dotnet.microsoft.com/download) (2.1 or later)
2. Install [VS or build tools](https://visualstudio.microsoft.com/downloads) (2017 or later)
2. Run build.cmd

## Containers

ECS containers provide a useful bundle of functionality for working with shared game state, including event handling, component storage, entity ID generation, coroutine scheduling, and resource resolution.

### Resources

Containers store resources such as component lists, ID pools, settings, and any other arbitrary type. You can access resources by type with some limited dependency resolution.

```fsharp
c.RegisterResource(fun () -> defaultWorldSettings)
let settings = c.GetResource<WorldSettings>()
```

This kind of service locator is useful for extensibility, but it also introduces new kinds of runtime errors that could not occur with a hardwired approach, plus it hides dependencies within implementation code rather than part of a public signature.

### Object pooling

Avoiding GC generally amounts to use of structs, pooling, and avoiding closures. Almost all objects are either pooled within a container or on the stack, so there's little or no GC impact or allocation once maximum load is reached. If needed, warming up or provisioning buffers ahead of time is possible for avoiding GC entirely during gameplay.

### Commits

Certain operations on containers, such as sending events or adding/removing components, are staged until a commit occurs, allowing any running event handlers to observe the original state. Commits occur automatically after all processors have completed handling a list of events, so you typically shouldn't need to explicitly commit.

```fsharp
// create an entity
let e = c.Create().With("test")
// not yet visible
c.Commit()
// now visible
```

## Entities

An entity is any identifiable thing in your game which you can attach components to. At minimum, an entity consists only of an entity ID.

### Entity ID

Entity IDs are 32 bits and stored in a component list. This means they can be accessed and iterated over like any other component type without special handling. IDs use a special Eid type rather than a raw int32, which offers better type safety but means you need a direct dependency on Garnet if you want to define types with an Eid (or you can manage converting to your own ID type if this is an issue). 

```fsharp
let entity = c.Create()
printfn "%A" entity.id
```

### Generations

A portion of an ID is dedicated to its generation number. The purpose of a generation is to avoid reusing IDs while still allowing buffer slots to be reused, keeping components stored as densely as possible.

### Partitioning

Component storage could become inefficient if it grows too sparse (i.e. the average number of occupied elements per segment becomes low). If this is a concern (or you just want to organize your entities), you can optionally use partitions to specify a high bit mask in ID generation. For example, if ship and bullet entities shared the same ID space, they may become mixed over time and the ship components would become sparse. Instead, with separate partitions, both entities would remain dense. Note: this will likely be replaced with groups in the future.

### Generic storage

Storage should work well for both sequential and sparse data and support generic key types. Entity IDs are typically used as keys, but other types like grid location should be possible as well.

## Components

Components are any arbitrary data type associated with an entity.

### Data-oriented

Entities and components are stored as a struct of arrays rather than an array of structs, sorted and in blocks of memory, making them suitable for fast iteration and batch operations.

### Data types

Components should ideally be pure data rather than classes with behavior and dependencies. They should typically be structs to avoid jumping around in memory or incurring allocations and garbage collection. Structs should almost always be immutable, but mutable structs (with their gotchas) are possible too.

```fsharp
[<Struct>] type Position = { x : float32; y : float32 }
[<Struct>] type Velocity = { vx : float32; vy : float32 }
```

### Storage

Components are stored separately from each other in 64-element segments with a mask, ordered by ID. This provides CPU-friendly iteration over densely stored data while retaining some benefits of sparse storage. Some ECS implementations provide a variety of specialized data structures, but Garnet attempts a middle ground that works moderately well for both sequential entity IDs and sparse keys such as world grid locations.

Only a single component of a type is allowed per entity, but there is no hard limit on the total number of different component types used (i.e. there is no fixed-size mask defining which components an entity has).

### Iteration

You can iterate over entities with specific combinations of components using joins/queries. In this way you could define a system that updates all entities with a position and velocity, but iteration would skip over any entities with only a position and not velocity. Currently, only a fixed set of predefined joins are provided rather than allowing arbitrary queries.

```fsharp
let runIter =
    // first define an iteration callback:
    // (1) param can be any type or just ignored
    // (2) use struct record for component types
    fun param struct(eid : Eid, p : Position, h : Health) ->
        if h.hp <= 0 then 
            // [start animation at position]
            // destroy entity
            c.Destroy(eid)
    // iterate over all entities with all components
    // present (inner join)
    |> Iter.join3
    // iterate over container
    |> Iter.over c
let healthSub =
    c.On<DestroyZeroHealth> <| fun e ->
        runIter()
```

### Adding/removing

Additions or removals are deferred until a commit occurs. Any code dependent on those operations completing needs to be implemented as a coroutine. Note that you can repeatedly add and remove components for the same entity ID before a commit if needed.

```fsharp
let e = c.Get(Eid 100)
e.Add<Position> { x = 1.0f; y = 2.0f }
e.Remove<Velocity>()
// change not yet visible
```

### Updating

Unlike additions and removals, updating/replacing an existing component can be done directly at the risk of affecting subsequent subscribers. This way is convenient if the changes are incremental/commutative or there are no other subscribers writing to the component type during the same event. You can alternately just use addition if you don't know whether a component is already present.

```fsharp
let e = c.Get(Eid 100)
e.Set<Position> { x = 1.0f; y = 2.0f }
// change immediately visible
```

### Markers

You can define empty types for use as flags or markers, in which case only 64-bit masks need to be stored per segment. Markers are an efficient way to define static groups for querying.

```fsharp
type PowerupMarker = struct end
```

## Systems

Systems are essentially event subscribers with an optional name. System event handlers often iterate over entities, such as updating position based on velocity, but they can do any other kind of processing too. Giving a system a name allows hot reloading.

```fsharp
module MovementSystem =     
    // separate methods as needed
    let registerUpdate (c : Container) =
        c.On<UpdatePositions> <| fun e ->
            printfn "%A" e

    // combine all together
    let definition =
        // give a name so we can hot reload
        Registration.listNamed "Movement" [
            registerUpdate
            ]
```

### Events

Events can be arbitrary types, but preferably structs. Subscribers such as systems receive batches of events with no guaranteed ordering among the subscribers. Any additional events raised during event handling are run after all the original event handlers complete, thereby avoiding any possibility of reentrancy but complicating synchronous behavior. 

```fsharp
[<Struct>] type UpdateTime = { dt : float32 }

// call sub.Dispose() to unsubscribe
let sub =
    c.On<UpdateTime> <| fun e ->
        // [do update here]
        printfn "%A" e

// send event        
c.Send { dt = 0.1f }
```

Also note that events intentionally decouple publishers and subscribers, and since dispatching events is typically not synchronous within the ECS, it can be difficult to trace the source of events when something goes wrong (no callstack).

### Composing systems

Since systems are just named event subscriptions, you can compose them into larger systems. This allows for bundling related functionality.

```fsharp
module CoreSystems =        
    let definition =
        Registration.combine [
            MovementSystem.definition
            HashSpaceSystem.definition
        ]
```

### Coroutines

Coroutines allow capturing state and continuing processing for longer than the handling of a single event. They are implemented as sequences and can be used to achieve synchronous behavior despite the asynchronous nature of event handling. This is one of the few parts of the code which incurs allocation.

Coroutines run until they encounter a yield statement, which can tell the coroutine scheduler to either wait for a time duration or to wait until all nested processing has completed. Nested processing refers to any coroutines created as a result of events sent by the current coroutine, allowing a stack-like flow and ordering of events.

```fsharp
let system =
    c.On<Msg> <| fun e ->
        printf "2 "

// start a coroutine
c.Start <| seq {
    printf "1 "
    // send message and defer execution until all messages and
    // coroutines created as a result of this have completed
    c.Send <| Msg()
    yield Wait.defer
    printf "3 "
    }

// run until completion
// output: 1 2 3
c.Run()
```

Time-based coroutines are useful for animations or delayed effects. You can use any unit of time as long as it's consistent.

```fsharp
// start a coroutine
c.Start <| seq {
    for i = 1 to 5 do
        printf "[%d] " i
        // yield execution until time units pass
        yield Wait.time 3
    }

// run update loop
// output: [1] 1 2 3 [2] 4 5 6 [3] 7 8 9
for i = 1 to 9 do
    // increment time units and run pending coroutines
    c.Step 1
    c.Run()
    printf "%d " i
```

### Multithreading

It's often useful to run physics in parallel with other processing that doesn't depend on its output, but the event system currently has no built-in features to facilitate multiple threads reading or writing. Instead, parallel execution is implemented at a higher level actor system, or you can implement your own multithreaded systems.

### Event ordering

For systems that subscribe to the same event and access the same resources or components, you need to consider whether one is dependent on the other and should run first.

One way to guarantee ordering is to define individual sub-events for the systems and publish those events in the desired order as part of a coroutine started from the original event (with waits following each event to ensure all subscribers are run before proceeding).

```fsharp
// events
type Update = struct end
type UpdatePhysicsBodies = struct end
type UpdateHashSpace = struct end

// systems
let updateSystem =
    c.On<Update> <| fun e -> 
        c.Start <| seq {
            // using shorthand 'send and defer' to suspend
            // execution here to achieve ordering of 
            // sub-updates
            yield c.Wait <| UpdatePhysicsBodies()
            yield c.Wait <| UpdateHashSpace()
        }
let system1 = 
    c.On<UpdatePhysicsBodies> <| fun e ->
        // [update positions]
        printfn "%A" e
let system2 = 
    c.On<UpdateHashSpace> <| fun e ->
        // [update hash space from positions]
        printfn "%A" e
```

## Actors

While ECS containers provide a simple and fast means of storing and updating shared memory state using a single thread, actors share no common state and communicate only through messages, making them suitable for parallel processing.

### Definitions

Actors are identified by an actor ID. They are statically defined and created on demand when a message is sent to a nonexistent actor ID. At that point, an actor consisting of a message handler is created based on any definitions registered in the actor system that match the actor ID. It's closer to a mailbox processor than a complete actor model since these actors can't dynamically create arbitrary actors or control actor lifetimes.

```fsharp
// message types
type Ping = struct end
type Pong = struct end

// actor definitions
let a = new ActorSystem()
a.Register(ActorId 1, fun c ->
    c.On<Ping> <| fun e -> 
        printf "ping "
        e.Respond(Pong())
    )
a.Register(ActorId 2, fun c ->
    c.On<Pong> <| fun e -> 
        printf "pong "
    )
    
// send a message and run until all complete
// output: ping pong
a.Send(ActorId 1, Ping(), sourceId = ActorId 2)
a.RunAll()
```

### Actor messages versus container events

"Events" and "messages" are often used interchangeably, but here we use separate terms to distinguish container 'events' from actor 'messages'. Containers already have their own internal event system, but the semantics are a bit different from actors because container events are always stored in separate channels by event type rather than a single serialized channel for all actor message types. The use of separate channels within containers allows for efficient batch processing in cases where event types have no ordering dependencies, but ordering by default is preferable in many other cases involving actors.

### Wrapping containers

It's useful to wrap a container within an actor, where incoming messages to the actor automatically dispatched to the container, and systems within the container have access to an outbox for sending messages to other actors. This approach allows keeping isolated worlds, such as a subset of world state for AI forces or UI state.

### Replay debugging

If you can write logic where your game state is fully determined by the sequence of incoming messages, you can log these messages and replay them to diagnose bugs. This works best if you can isolate the problem to a single actor, such as observing incorrect state or incorrect outgoing messages given a reasonable input sequence.

### Message ordering

Messages sent from one actor to another are guaranteed to arrive in the order they were sent, but they may be interleaved with messages arriving from other actors. In general, multiple actors and parallelism can introduce complexity similar to the use of microservices, which address scaleability but can introduce race conditions and challenges in synchronization.

### Multithreading

You can designate actors to run on either the main thread (for UI if needed) or a background thread. Actors run when a batch of messages is delivered, resembling task-based parallelism. In addition to running designated actors, the main thread also delivers messages among actors, although this could change in the future if it becomes a bottleneck. Background actors currently run using a fixed pool of worker threads.

## Integration

How does Garnet integrate with larger frameworks or engines like Unity, MonoGame, or UrhoSharp? You have a few options depending on how much you want to depend on Garnet, your chosen framework, and your own code.

### Abstracting the framework

You can choose to insulate your code from the framework (e.g. MonoGame) at the cost of more effort building an abstraction layer, less power, and some overhead in marshalling data. You have several options for defining the abstraction layer:

- **Resources**: Define an interface for a subsystem and provide an implemention for the framework, e.g. ISpriteRenderer. This makes sense if you want synchronous calls and an explicit interface.

- **Events**: Define interface event types and framework-specific systems which subscribe to them, e.g. sprite rendering system subscribes to DrawSprite events. This way is more decoupled, but the interface may not be so clear.

- **Components**: Define interface component types and implement framework-specific systems which iterate over them, e.g. sprite rendering system iterates over entities with Sprite component. 

### Code organization

To minimize your dependency on Garnet and your chosen framework, you can organize your code into layers:

- Math code
- Domain types
- Domain logic
- Framework interface types
- [Garnet]
- System definitions
- [Framework]
- Startup code

See the [sample code](https://github.com/bcarruthers/garnet/tree/master/samples) for more detail.

## Roadmap

- Performance improvements
- Container snapshots and entity replication 
- More samples
- More test coverage
- Benchmarks
- Comprehensive docs
- Urho scripts with hot reloading
- Extensions or samples for networking and compression
- Fault tolerance in actor system
- Guidance for managing game states

## FAQ

- **Why F#?** F# offers conciseness, functional-first defaults like immutability, an algebraic type system, interactive code editing, and pragmatic support for other paradigms like OOP. Strong type safety makes it more likely that code is correct, which is especially helpful for tweaking game code that changes frequently enough to make unit testing impractical.

- **What about performance?** Functional code often involves allocation, which sometimes conflicts with the goal of consistent performance when garbage collection occurs. A goal of this library is to reduce the effort in writing code that minimizes allocation. But for simple games, this is likely a non-issue and you should start with idiomatic code.

- **Why use ECS over MVU?** You probably shouldn't start with ECS for a simple game, at least not when prototyping, unless you already have a good understanding of where it might be beneficial. MVU avoids a lot of complexity and has stronger type safety and immutability guarantees than ECS, but you may encounter issues if your project has demanding performance requirements or needs more flexibility than it allows. 

## License
This project is licensed under the [MIT license](https://github.com/bcarruthers/garnet/blob/master/LICENSE).

## Maintainer(s)

- [@bcarruthers](https://github.com/bcarruthers)