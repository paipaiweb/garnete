module Garnet.Tests.Entities

open System.Collections.Generic
open Expecto
open Garnet.Ecs

[<AutoOpen>]
module Assertions =
    let shouldEqual b a =
        Expect.equal a b ""

    let shouldNotEqual b a =
        Expect.notEqual a b ""

[<AutoOpen>]
module MockTypes =
    type Velocity = {
        velocity : int }

    type Position = {
        position : int }

[<Tests>]
let tests =
    testList "entities" [
        testCase "increment ID version" <| fun () ->
            Eid.fromParts 1 0 0 |> Eid.getGen |> shouldEqual 1
            Eid.incrementGen (Eid 100) |> shouldEqual (Eid.fromParts 1 0 100)

        testCase "incrementing ID version wraps" <| fun () ->
            Eid.fromParts Eid.maxGen 0 100 
            |> Eid.incrementGen 
            |> shouldEqual (Eid.fromParts 0 0 100)

        testCase "create ID from pool" <| fun () ->
            let p = EidPool(10)
            p.Next().Index |> shouldEqual 64

        testCase "recycle ID to pool" <| fun () ->
            let segmentSize = 64
            let p = EidPool(10)
            let usedIds = HashSet<_>()
            for j = 1 to 100 do
                for i = 0 to segmentSize - 1 do
                    let eid = p.Next()
                    eid.Gen |> shouldEqual 0
                    usedIds.Add(eid) |> shouldEqual true
                    p.Recycle(eid)

        testCase "create entity" <| fun () ->
            let c = Container()
            let e = c.Create()
            e.Add { velocity = 3 }
            e.Add { position = 5 }
            c.Get<Eid>().Count |> shouldEqual 0
            c.Commit()
            c.Get<Eid>().Count |> shouldEqual 1            
            c.Get<Velocity>().Count |> shouldEqual 1
            
        testCase "remove entity" <| fun () ->
            let c = Container()
            let e = c.Create()
            e.Add { velocity = 3 }
            c.Commit()
            e.Destroy()
            c.Get<Eid>().Count |> shouldEqual 1
            c.Get<Velocity>().Count |> shouldEqual 1
            c.Commit()
            c.Get<Eid>().Count |> shouldEqual 0
            c.Get<Velocity>().Count |> shouldEqual 0

        testCase "add and remove simultaneously" <| fun () ->
            let c = Container()
            // Eid and component go into additions
            let e = c.Create()
            e.Add 123
            // Eid goes into removal
            e.Destroy()
            c.Commit()
            // so components are present
            c.Get<Eid>().ComponentCount |> shouldEqual 0
            c.Get<Eid>().Count |> shouldEqual 0
            c.Get<int>().ComponentCount |> shouldEqual 0

        testCase "add and remove sequentially" <| fun () ->
            let c = Container()
            // Eid and component go into additions
            let e = c.Create()
            e.Add 123
            // additions applied
            c.Commit()
            c.Get<Eid>().ComponentCount |> shouldEqual 1
            c.Get<Eid>().Count |> shouldEqual 1
            c.Get<int>().ComponentCount |> shouldEqual 1
            // Eid goes into removal
            e.Destroy()
            // removals applied
            c.Commit()
            // so components are not present
            c.Get<Eid>().ComponentCount |> shouldEqual 0
            c.Get<Eid>().Count |> shouldEqual 0
            // automatically removed during commit:
            c.Get<int>().ComponentCount |> shouldEqual 0
    ]