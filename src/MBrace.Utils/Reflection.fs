﻿namespace Nessos.MBrace.Utils


    module Reflection =
        
        open System
        open System.Reflection
        open System.Collections
        open System.Collections.Generic
        open System.Text.RegularExpressions
        open System.Runtime.Serialization

        open Microsoft.FSharp.Quotations
        open Microsoft.FSharp.Quotations.Patterns

        open Microsoft.FSharp.Reflection
        open Microsoft.FSharp.Core.OptimizedClosures

        open Nessos.MBrace.Utils.String

        type CompType(t : Type) =
            inherit ProjectionComparison<CompType,string>(t.AssemblyQualifiedName)
            member __.Value = t

        module internal Primitives =
            let tunit = typeof<unit>
            let tbool = typeof<bool>
            let tobj = typeof<obj>
            let texn = typeof<exn>
            let tint32 = typeof<int>
            let tchar = typeof<char>
            let tbyte = typeof<byte>
            let tdecimal = typeof<decimal>
            let tstring = typeof<string>
            let tfloat = typeof<float>
            let tsingle = typeof<single>
            let tsbyte = typeof<sbyte>
            let tint16 = typeof<int16>
            let tuint16 = typeof<uint16>
            let tuint32 = typeof<uint32>
            let tint64 = typeof<int64>
            let tuint64 = typeof<uint64>
            let tguid = typeof<Guid>
            let tdatetime = typeof<DateTime>

            let tlist = typedefof<_ list>
            let tmap = typedefof<Map<_,_>>
            let tset = typedefof<Set<_>>
            let toption = typedefof<_ option>

            let fsGenerics = [ tlist ; tmap ; tset ; toption ]
            let fsEnums = [ tlist ; tmap ; tset ]


        let (|GenericType|_|) (t : Type) =
            if t.IsGenericType then
                Some (t.GetGenericTypeDefinition(), t.GetGenericArguments())
            else None

        let (|FsGenericType|_|) (t : Type) =
            if t.IsGenericType then
                let gt = t.GetGenericTypeDefinition()
                if List.exists ((=) gt) Primitives.fsGenerics then Some(gt, t.GetGenericArguments())
                else None
            else None


        // recognizes standard F# IEnumerable types: arrays, lists, maps, sets
        let (|FsEnumeration|_|) (t : Type) =
            if t.IsArray then
                let et = t.GetElementType()
                Some(t, [|t.GetElementType()|])
            elif t.IsGenericType then
                let gt = t.GetGenericTypeDefinition()
                if List.exists ((=) gt) Primitives.fsEnums then
                    Some(gt, t.GetGenericArguments())
                else None
            else None
                

        let inline matches<'T> t = t = typedefof<'T>

        let allFlags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static

        type Assembly with
            static member TryFind(name : string) =
                AppDomain.CurrentDomain.GetAssemblies()
                |> Array.tryFind (fun a -> try a.FullName = name || a.GetName().Name = name with _ -> false)

        /// matches against lambda types, returning a tuple ArgType [] * ResultType
        let (|FSharpFunc|_|) : Type -> _ =
            let fsFunctionTypes =
                [
                    typedefof<FSharpFunc<_,_>>
                    typedefof<FSharpFunc<_,_,_>>
                    typedefof<FSharpFunc<_,_,_,_>>
                    typedefof<FSharpFunc<_,_,_,_,_>>
                    typedefof<FSharpFunc<_,_,_,_,_,_>>
                ] 
                |> Seq.map (fun t -> t.AssemblyQualifiedName)
                |> Set.ofSeq

            let rec tryGetFSharpFunc =
                function
                | GenericType (t, args) when fsFunctionTypes.Contains t.AssemblyQualifiedName ->
                    let l = args.Length
                    Some(args.[0..l-2], args.[l-1])
                | t ->
                    if t.BaseType <> null then 
                        tryGetFSharpFunc t.BaseType
                    else None

            let rec collect (t : Type) =
                match tryGetFSharpFunc t with
                | None -> None
                | Some(args, rest) ->
                    match collect rest with
                    | Some(args', codomain) -> Some(Array.append args args', codomain)
                    | None -> Some (args, rest)

            collect

        let (|FsTuple|_|) (t : Type) =
            if FSharpType.IsTuple t then
                Some(FSharpType.GetTupleElements t)
            else None


        let (|Named|Array|Ptr|Param|) (t : System.Type) =
            if t.IsGenericType
            then Named(t.GetGenericTypeDefinition(), t.GetGenericArguments())
            elif t.IsGenericParameter
            then Param(t.GenericParameterPosition)
            elif not t.HasElementType
            then Named(t, [| |])
            elif t.IsArray
            then Array(t.GetElementType(), t.GetArrayRank())
            elif t.IsByRef
            then Ptr(true, t.GetElementType())
            elif t.IsPointer
            then Ptr(false, t.GetElementType())
            else failwith "MSDN says this can’t happen"

        /// this predicate recognizes type patterns whose components are sealed types
        let rec isSealedPattern (t : Type) =
            if t.IsSealed then true
            elif t.IsArray then isSealedPattern (t.GetElementType())
            elif FSharpType.IsTuple t then Array.forall isSealedPattern (FSharpType.GetTupleElements t)
            elif t.IsGenericType then
                let gt = t.GetGenericTypeDefinition()
                if List.exists ((=) gt) Primitives.fsGenerics then
                    Array.forall isSealedPattern (t.GetGenericArguments())
                else false
            else false            

        /// gather all types within an object graph
        let gatherTypes (graph : obj) =
            let gathered = new System.Collections.Generic.HashSet<Type> ()

            let rec traverse : obj list -> _ =
                function
                | [] -> ()
                | null :: rest -> traverse rest
                | o :: rest ->
                    let t = o.GetType()
                    if gathered.Contains t then traverse rest
                    else
                        gathered.Add t |> ignore

                        let nested =
                            match t with
                            | _ when t.IsValueType -> []
                            // handle standard IEnumerable F# types
                            | FsEnumeration (t,ga) ->
                                let es = o :?> IEnumerable
                                if Array.forall isSealedPattern ga then
                                    let enum = es.GetEnumerator()
                                    if enum.MoveNext() then [enum.Current] else []
                                else [ for e in es -> e ]
                            | _ ->
                                let fields = t.GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)
                                [ for fInfo in fields -> fInfo.GetValue o ]

                        traverse (nested @ rest)

            do traverse [graph]

            Seq.toList gathered


        let rec isParentType(p : Type, c : Type) =
            if p = c then true
            else
                match c.DeclaringType with
                | null -> false
                | c' -> isParentType(p, c')

        /// returns the root type/module containing the type
        let rec rootType (t : Type) =
            match t.DeclaringType with
            | null -> t
            | t' -> rootType t'

        let rec rootModuleOrNamespace (t : Type) =
            if t.IsArray || t.IsByRef || t.IsPointer then rootModuleOrNamespace <| t.GetElementType()
            else
                match t.Namespace with
                | null ->
                    match t.DeclaringType with
                    | null -> t.Name
                    | d -> rootModuleOrNamespace d
                | ns -> ns            
                

        open Primitives

        type internal Priority =
            | Bottom = 100
            | Arrow = 4
            | Tuple = 3
            | Postfix = 2
            | Generic = 1
            | Const = 0

        let prettyPrint (t : Type) =
            let strip =
                let regex = memoize (fun txt -> Regex txt)
                fun pattern input ->
                    (regex pattern).Replace(input,MatchEvaluator(fun _ -> ""))

            let format (t : Type) =
                if t.Namespace = "Microsoft.FSharp.Collections" then
                    t.Name |> strip "^FSharp"
                else t.Name
                
                |> strip @"`[0-9]*$" 
                
            let rec traverse parent (t : Type) =
                string {
                    if t.IsGenericType then
                        match t with
                        | FsTuple args ->
                            if parent <= Priority.Tuple then yield '('

                            yield! traverse Priority.Tuple args.[0]

                            for arg in args.[1..] do
                                yield " * "
                                yield! traverse Priority.Tuple arg

                            if parent <= Priority.Tuple then yield ')'
                        | FSharpFunc(args, rt) ->
                            if parent <= Priority.Arrow then yield '('
                        
                            for arg in args do
                                yield! traverse Priority.Arrow arg
                                yield " -> "
                            yield! traverse Priority.Arrow rt

                            if parent <= Priority.Arrow then yield ')'
                        | Array (arg, _) -> yield! traverse Priority.Postfix arg ; yield " []"
                        | Ptr (_,arg) -> yield! traverse Priority.Postfix arg ; yield " ref"
                        | GenericType (t0, args) ->
                            match t0 with
                            | _ when matches<_ array> t0 -> yield! traverse Priority.Postfix args.[0] ; yield " []"
                            | _ when matches<_ list> t0 -> yield! traverse Priority.Postfix args.[0] ; yield " list"
                            | _ when matches<_ option> t0 -> yield! traverse Priority.Postfix args.[0] ; yield " option"
                            | _ when matches<_ seq> t0 -> yield! traverse Priority.Postfix args.[0] ; yield " seq"
                            | _ when matches<_ ref> t0 -> yield! traverse Priority.Postfix args.[0] ; yield " ref"
                            | _ ->
                                yield format t0
                                yield "<"
                                yield! traverse Priority.Generic args.[0]

                                for arg in args.[1..] do
                                    yield ","
                                    yield! traverse Priority.Generic arg

                                yield ">"

                        | t -> yield t.ToString()
                    else
                        match t with
                        | _ when t = tunit -> yield "unit"
                        | _ when t = tbool -> yield "bool"
                        | _ when t = tobj -> yield "obj"
                        | _ when t = texn -> yield "exn"
                        | _ when t = tint32 -> yield "int"
                        | _ when t = tchar -> yield "char"
                        | _ when t = tbyte -> yield "byte"
                        | _ when t = tdecimal -> yield "decimal"
                        | _ when t = tstring -> yield "string"
                        | _ when t = tfloat -> yield "float"
                        | _ when t = tsingle -> yield "single"
                        | _ when t = tsbyte -> yield "sbyte"
                        | _ when t = tint16 -> yield "int16"
                        | _ when t = tuint16 -> yield "uint16"
                        | _ when t = tuint32 -> yield "uint32"
                        | _ when t = tint64 -> yield "int64"
                        | _ when t = tuint64 -> yield "uint64"
                        | Array (arg, _) -> yield! traverse Priority.Postfix arg ; yield " []"
                        | Ptr (_,arg) -> yield! traverse Priority.Postfix arg ; yield " ref"
                        | t -> yield t.ToString()
                }

            traverse Priority.Bottom t |> String.build


        type UnionCaseAttributeReader(uci : UnionCaseInfo) =
            member __.GetAttrs<'T when 'T :> Attribute> (?includeDeclaringTypeAttrs) =
                let includeDeclaringTypeAttrs = defaultArg includeDeclaringTypeAttrs false

                let attrs = uci.GetCustomAttributes(typeof<'T>) |> Seq.map (fun o -> o :?> 'T)

                if includeDeclaringTypeAttrs then
                    let parentAttrs = uci.DeclaringType.GetCustomAttributes<'T>()
                    Seq.append parentAttrs attrs |> Seq.toList
                else
                    Seq.toList attrs

            member __.ContainsAttr<'T when 'T :> Attribute> (?includeDeclaringTypeAttrs) =
                let includeDeclaringTypeAttrs = defaultArg includeDeclaringTypeAttrs false

                if includeDeclaringTypeAttrs then
                    uci.DeclaringType.GetCustomAttributes<'T> () |> Seq.isEmpty |> not
                        || uci.GetCustomAttributes(typeof<'T>) |> Seq.isEmpty |> not
                else
                    uci.GetCustomAttributes(typeof<'T>) |> Seq.isEmpty |> not

        // an assortment of GAC tools
        // mono not supported

        [<RequireQualifiedAccess>]
        module GacTools =
            open System.IO

            let internal gac_paths = lazy(
                let windir = Environment.GetEnvironmentVariable("windir")
                let gacRoot = Path.Combine(windir, @"Microsoft.NET\assembly")
                let paths = [ "GAC_32" ; "GAC_64" ; "GAC_MSIL" ] |> List.map (fun p -> Path.Combine(gacRoot, p))
                paths |> List.filter Directory.Exists)

            /// looks up gac by partial assembly name
            let lookupPartial(name : string) =
                if runsOnMono then raise <| new NotSupportedException("mono not supported... yet")

                let lookup path =
                    let path0 = Path.Combine(path, name)
                    if Directory.Exists path0 then
                        Directory.EnumerateFiles(path0, name + ".dll", SearchOption.AllDirectories)
                    else Seq.empty

                gac_paths.Value
                |> Seq.collect lookup 
                |> Seq.map Assembly.ReflectionOnlyLoadFrom 
                |> Seq.toArray
                

    