﻿namespace Aardvark.Base.Incremental

open System
open System.Collections
open System.Collections.Generic
open Aardvark.Base.Incremental
open Aardvark.Base

type IListReader<'a> = IOpReader<plist<'a>, deltalist<'a>>

type alist<'a> =
    abstract member IsConstant  : bool
    abstract member Content     : IMod<plist<'a>>
    abstract member GetReader   : unit -> IListReader<'a>

[<StructuredFormatDisplay("{AsString}")>]
type clist<'a>(initial : seq<'a>) =
    let history = History PList.trace
    do 
        let mutable last = Index.zero
        let ops = 
            initial |> Seq.map (fun v ->
                let t = Index.after last
                last <- t
                t, Set v
            )
            |> DeltaList.ofSeq

        history.Perform ops |> ignore

    member x.Count = 
        lock x (fun () -> history.State.Count)

    member x.Clear() =
        lock x (fun () ->
            if x.Count > 0 then
                let deltas = history.State.Content |> MapExt.map (fun k v -> Remove) |> DeltaList.ofMap
                history.Perform deltas |> ignore
        )

    member x.Append(v : 'a) =
        lock x (fun () ->
            let t = Index.after history.State.MaxIndex
            history.Perform (DeltaList.ofList [t, Set v]) |> ignore
            t
        )

    member x.Prepend(v : 'a) =
        lock x (fun () ->
            let t = Index.before history.State.MinIndex
            history.Perform (DeltaList.ofList [t, Set v]) |> ignore
            t
        )

    member x.Remove(i : Index) =
        lock x (fun () ->
            history.Perform (DeltaList.ofList [i, Remove])
        )

    member x.RemoveAt(i : int) =
        lock x (fun () ->
            let (id,_) = history.State.Content |> MapExt.item i
            x.Remove id |> ignore
        )

    member x.IndexOf(v : 'a) =
        lock x (fun () ->
            history.State 
                |> Seq.tryFindIndex (Unchecked.equals v) 
                |> Option.defaultValue -1
        )

    member x.Remove(v : 'a) =
        lock x (fun () ->
            match x.IndexOf v with
                | -1 -> false
                | i -> x.RemoveAt i; true
        )

    member x.Contains(v : 'a) =
        lock x (fun () -> history.State) |> Seq.exists (Unchecked.equals v)

    member x.Insert(i : int, value : 'a) =
        lock x (fun () ->
            if i < 0 || i > history.State.Content.Count then
                raise <| IndexOutOfRangeException()

            let l, s, r = MapExt.neighboursAt i history.State.Content
            let r = 
                match s with
                    | Some s -> Some s
                    | None -> r
            let index = 
                match l, r with
                    | Some (before,_), Some (after,_) -> Index.between before after
                    | None,            Some (after,_) -> Index.before after
                    | Some (before,_), None           -> Index.after before
                    | None,            None           -> Index.after Index.zero
            history.Perform (DeltaList.ofList [index, Set value]) |> ignore
            index
        )
        
    member x.CopyTo(arr : 'a[], i : int) = 
        let state = lock x (fun () -> history.State )
        state.CopyTo(arr, i)

    member x.Item
        with get (i : int) = 
            let state = lock x (fun () -> history.State)
            state.[i]

        and set (i : int) (v : 'a) =
            lock x (fun () ->
                let k = history.State.Content |> MapExt.tryItem i
                match k with
                    | Some (id,_) -> history.Perform(DeltaList.ofList [id, Set v]) |> ignore
                    | None -> ()
            )

    member x.Item
        with get (i : Index) =
            let state = lock x (fun () -> history.State)
            state.[i]

        and set (i : Index) (v : 'a) =
            lock x (fun () ->
                history.Perform(DeltaList.ofList [i, Set v]) |> ignore
            )

    override x.ToString() =
        let suffix =
            if x.Count > 5 then "; ..."
            else ""

        let content =
            history.State |> Seq.truncate 5 |> Seq.map (sprintf "%A") |> String.concat "; "

        "clist [" + content + suffix + "]"

    member private x.AsString = x.ToString()

    interface alist<'a> with
        member x.IsConstant = false
        member x.Content = history :> IMod<_>
        member x.GetReader() = history.NewReader()

    interface ICollection<'a> with 
        member x.Add(v) = x.Append v |> ignore
        member x.Clear() = x.Clear()
        member x.Remove(v) = x.Remove v
        member x.Contains(v) = x.Contains v
        member x.CopyTo(arr,i) = x.CopyTo(arr, i)
        member x.IsReadOnly = false
        member x.Count = x.Count

    interface IList<'a> with
        member x.RemoveAt(i) = x.RemoveAt i
        member x.IndexOf(item : 'a) = x.IndexOf item
        member x.Item
            with get(i : int) = x.[i]
            and set (i : int) (v : 'a) = x.[i] <- v
        member x.Insert(i,v) = x.Insert(i,v) |> ignore

    interface IEnumerable with
        member x.GetEnumerator() = (history.State :> seq<_>).GetEnumerator() :> _

    interface IEnumerable<'a> with
        member x.GetEnumerator() = (history.State :> seq<_>).GetEnumerator() :> _

module AList =
    
    [<AutoOpen>]
    module private Implementation = 
        type EmptyReader<'a> private() =
            inherit ConstantObject()

            static let instance = new EmptyReader<'a>() :> IOpReader<_,_>
            static member Instance = instance

            interface IOpReader<deltalist<'a>> with
                member x.Dispose() =
                    ()

                member x.GetOperations caller =
                    DeltaList.empty

            interface IOpReader<plist<'a>, deltalist<'a>> with
                member x.State = PList.empty

        type EmptyList<'a> private() =
            let content = Mod.constant PList.empty

            static let instance = EmptyList<'a>() :> alist<_>

            static member Instance = instance

            interface alist<'a> with
                member x.IsConstant = true
                member x.Content = content
                member x.GetReader() = EmptyReader.Instance

        type ConstantList<'a>(content : Lazy<plist<'a>>) =
            let deltas = lazy ( plist.ComputeDeltas(PList.empty, content.Value) )
            let mcontent = ConstantMod<plist<'a>>(content) :> IMod<_>

            interface alist<'a> with
                member x.IsConstant = true
                member x.GetReader() = new History.Readers.ConstantReader<_,_>(PList.trace, deltas, content) :> IListReader<_>
                member x.Content = mcontent
        
            new(content : plist<'a>) = ConstantList<'a>(Lazy.CreateFromValue content)

        type AdaptiveList<'a>(newReader : unit -> IOpReader<deltalist<'a>>) =
            let h = History.ofReader PList.trace newReader
            interface alist<'a> with
                member x.IsConstant = false
                member x.Content = h :> IMod<_>
                member x.GetReader() = h.NewReader()
                
        let inline alist (f : Ag.Scope -> #IOpReader<deltalist<'a>>) =
            let scope = Ag.getContext()
            AdaptiveList<'a>(fun () -> f scope :> IOpReader<_>) :> alist<_>
            
        let inline constant (l : Lazy<plist<'a>>) =
            ConstantList<'a>(l) :> alist<_>

    [<AutoOpen>]
    module private Readers =
        open System.Collections.Generic

        type MapReader<'a, 'b>(scope : Ag.Scope, input : alist<'a>, mapping : Index -> 'a -> 'b) =
            inherit AbstractReader<deltalist<'b>>(scope, DeltaList.monoid)

            let r = input.GetReader()

            override x.Release() =
                r.Dispose()

            override x.Compute() =
                r.GetOperations x |> DeltaList.map (fun i op ->
                    match op with
                        | Remove -> Remove
                        | Set v -> Set (mapping i v)
                )

        type IndexedReader<'a>(scope : Ag.Scope, outerIndex : Index, inner : IListReader<'a>) =
            inherit AbstractReader<deltalist<'a>>(scope, DeltaList.monoid)

            member x.Index = outerIndex

            override x.Release() =
                inner.Dispose()

            override x.Compute() =
                inner.GetOperations x
                
            member x.State = inner.State
            interface IListReader<'a> with
                member x.State = inner.State

        type CollectReader<'a, 'b>(scope : Ag.Scope, input : alist<'a>, mapping : Index -> 'a -> alist<'b>) =
            inherit AbstractDirtyReader<IndexedReader<'b>, deltalist<'b>>(scope, DeltaList.monoid)

            static let comparer =
                { new IComparer<Index * Index * Index> with
                    member x.Compare((lo,li,_), (ro,ri,_)) =
                        let o = compare lo ro
                        if o = 0 then
                            compare li ri
                        else
                            o
                }

            let cache = Dictionary<Index, 'a * IndexedReader<'b>>()

            let times = SortedSetExt<Index * Index * Index>(comparer)

            let invokeIndex (outer : Index) (inner : Index) =
                let (l,s,r) = times.FindNeighbours((outer, inner, Unchecked.defaultof<_>))
                let s = if s.HasValue then Some s.Value else None

                match s with
                    | Some(_,_,i) -> 
                        i

                    | None ->
                        let l = if l.HasValue then Some l.Value else None
                        let r = if r.HasValue then Some r.Value else None

                        let result = 
                            match l, r with
                                | None, None                    -> Index.after Index.zero
                                | Some(_,_,l), None             -> Index.after l
                                | None, Some(_,_,r)             -> Index.before r
                                | Some (_,_,l), Some(_,_,r)     -> Index.between l r

                        times.Add((outer, inner, result)) |> ignore

                        result

            let revokeIndex (outer : Index) (inner : Index) =
                let (l,s,r) = times.FindNeighbours((outer, inner, inner))

                if s.HasValue then
                    times.Remove s.Value |> ignore
                    let (_,_,s) = s.Value
                    s
                else
                    failwith "[AList] removing unknown index"

            let invoke (i : Index) (v : 'a) =
                match cache.TryGetValue i with
                    | (true, (ov,ro)) -> 
                        if Object.Equals(v, ov) then
                            None, ro
                        else
                            let r = new IndexedReader<_>(scope, i, (mapping i v).GetReader())
                            cache.[i] <- (v, r)
                            Some ro, r
                    | _ -> 
                        let r = new IndexedReader<_>(scope, i, (mapping i v).GetReader())
                        cache.[i] <- (v, r)
                        None, r

            let revoke (i : Index) =
                match cache.TryGetValue i with
                    | (true, (_,r)) -> 
                        cache.Remove i |> ignore
                        r
                    | _ -> failwith "[AList] cannot revoke non existing reader"

            let reader = input.GetReader()

            override x.Release() =
                reader.Dispose()
                for (KeyValue(_,(_,r))) in cache do
                    r.Dispose()
                
                cache.Clear()
                times.Clear()

            override x.Compute(dirty) =
                
                let mutable ops =
                    reader.GetOperations x |> DeltaList.collect (fun oi op ->
                        match op with
                            | Remove -> 
                                let r = revoke oi

                                let operations = 
                                    r.State.Content |> MapExt.mapMonotonic (fun ii v ->
                                        let i = revokeIndex oi ii
                                        i, Remove
                                    )

                                r.Dispose()
                                dirty.Remove r |> ignore

                                DeltaList.ofMap operations

                            | Set v ->
                                let old, r = invoke oi v

                                let rem =
                                    match old with
                                        | Some o ->
                                            let res = 
                                                o.State.Content |> MapExt.mapMonotonic (fun ii v ->
                                                    let i = revokeIndex oi ii
                                                    i, Remove
                                                )
                                            o.Dispose()
                                            dirty.Remove o |> ignore
                                            DeltaList.ofMap res

                                        | None -> 
                                            DeltaList.empty

                                let add = 
                                    r.GetOperations(x) |> DeltaList.mapMonotonic (fun ii op ->
                                        match op with
                                            | Remove -> 
                                                let i = revokeIndex oi ii
                                                i, Remove
                                            | Set v ->
                                                let i = invokeIndex oi ii
                                                i, Set v
                                    )

                                DeltaList.combine rem add

                    )

                for d in dirty do
                    let deltas = 
                        d.GetOperations(x) |> DeltaList.mapMonotonic (fun ii op ->
                            match op with
                                | Remove -> 
                                    let i = revokeIndex d.Index ii
                                    i, Remove
                                | Set v ->
                                    let i = invokeIndex d.Index ii
                                    i, Set v
                        )

                    ops <- DeltaList.combine ops deltas
                    
                ops

                
    /// the empty alist
    let empty<'a> = EmptyList<'a>.Instance
    
    /// creates a new alist containing only the given element
    let single (v : 'a) =
        ConstantList(PList.single v) :> alist<_>
        
    /// creates a new alist using the given list
    let ofPList (l : plist<'a>) =
        ConstantList(l) :> alist<_>

    /// creates a new alist using the given sequence
    let ofSeq (s : seq<'a>) =
        s |> PList.ofSeq |> ofPList

    /// creates a new alist using the given sequence
    let ofList (l : list<'a>) =
        l |> PList.ofList |> ofPList

    /// creates a new alist using the given sequence
    let ofArray (l : 'a[]) =
        l |> PList.ofArray |> ofPList

    let mapi (mapping : Index -> 'a -> 'b) (list : alist<'a>) =
        alist <| fun scope -> new MapReader<'a, 'b>(scope, list, mapping)

    let map (mapping : 'a -> 'b) (list : alist<'a>) =
        mapi (fun _ v -> mapping v) list

    let collecti (mapping : Index -> 'a -> alist<'b>) (list : alist<'a>) =
        alist <| fun scope -> new CollectReader<'a, 'b>(scope, list, mapping)

    let collect (mapping : 'a -> alist<'b>) (list : alist<'a>) =
        collecti (fun _ v -> mapping v) list