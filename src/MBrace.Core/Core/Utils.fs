﻿namespace Nessos.MBrace.Core

    open System
    open System.Collections.Concurrent
    open System.Text.RegularExpressions

    module internal Utils =

        type TagObj = TagObj of obj * System.Type

        let inline safeBox<'T> (value : 'T) = TagObj(value, typeof<'T>)
        let inline safeUnbox<'T> (TagObj (o, t)) = 
            if typeof<'T>.IsAssignableFrom t then o :?> 'T
            else
                let msg = sprintf "Unable to case object of type '%O' to type '%O'." t typeof<'T>
                raise <| new InvalidCastException()


        /// thread safe counter implementation
        type UniqueIdGenerator (?start : int64) =
            let mutable count = 0L

            member __.Next () = System.Threading.Interlocked.Increment &count

        let memoize (f : 'T -> 'U) : 'T -> 'U =
            let cache = new ConcurrentDictionary<'T,'U>()
            fun x -> cache.GetOrAdd(x, f)

        /// memoized regex active pattern
        let (|RegexMatch|_|) =
            let regex = memoize(fun pattern -> Regex(pattern))
            
            fun (pat : string) (inp : string) ->
                let m = (regex pat).Match inp in
                if m.Success 
                then Some (List.tail [ for g in m.Groups -> g.Value ])
                else None


        type Async with
            static member Raise(e : exn) = Async.FromContinuations(fun (_,ec,_) -> ec e)
            static member Choice<'T>(tasks : Async<'T option> seq) : Async<'T option> =
                let wrap task =
                    async {
                        let! res = task
                        match res with
                        | None -> return ()
                        | Some r -> return! Async.Raise <| SuccessException r
                    }

                async {
                    try
                        do!
                            tasks
                            |> Seq.map wrap
                            |> Async.Parallel
                            |> Async.Ignore

                        return None
                    with 
                    | :? SuccessException<'T> as ex -> return Some ex.Value
                }

        and private SuccessException<'T>(value : 'T) =
            inherit System.Exception()
            member __.Value = value