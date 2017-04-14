﻿namespace Aardvark.Service

open System
open System.Text
open System.Net
open System.Threading
open System.Collections.Concurrent

open Suave
open Suave.Http
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.Files
open Suave.RequestErrors
open Suave.Logging
open Suave.Utils
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket


open Aardvark.Base
open Aardvark.Base.Incremental
open Aardvark.Application
open System.Diagnostics

type Message =
    | RequestImage of size : V2i
    | Rendered
    | Shutdown
    | Change of scene : string * samples : int

type Command =
    | Invalidate


module Pickler =
    open MBrace.FsPickler
    open MBrace.FsPickler.Json

    let json = FsPickler.CreateJsonSerializer(false, true)

[<AutoOpen>]
module private Tools = 
    open OpenTK.Graphics
    open OpenTK.Graphics.OpenGL4
    open Aardvark.Rendering.GL
    //open TurboJpegWrapper
    open System.Diagnostics
    open System.Runtime.CompilerServices
    open System.Runtime.InteropServices
    open System.Security
    open Microsoft.FSharp.NativeInterop

    let private downloadFBO (jpeg : TJCompressor) (size : V2i) (ctx : Context) =
        let pbo = GL.GenBuffer()

        let rowSize = 3 * size.X
        let align = ctx.PackAlignment
        let alignedRowSize = (rowSize + (align - 1)) &&& ~~~(align - 1)
        let sizeInBytes = alignedRowSize * size.Y
        try
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pbo)
            GL.BufferStorage(BufferTarget.PixelPackBuffer, nativeint sizeInBytes, 0n, BufferStorageFlags.MapReadBit)

            GL.ReadPixels(0, 0, size.X, size.Y, PixelFormat.Rgb, PixelType.UnsignedByte, 0n)

            let ptr = GL.MapBufferRange(BufferTarget.PixelPackBuffer, 0n, nativeint sizeInBytes, BufferAccessMask.MapReadBit)

            jpeg.Compress(
                ptr, alignedRowSize, size.X, size.Y, 
                TJPixelFormat.RGB, 
                TJSubsampling.S420, 
                90, 
                TJFlags.BottomUp ||| TJFlags.ForceSSE3
            )

        finally
            GL.UnmapBuffer(BufferTarget.PixelPackBuffer) |> ignore
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0)
            GL.DeleteBuffer(pbo)
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0)

    type private Compressor =
        class
            [<DefaultValue; ThreadStatic>]
            static val mutable private instance : TJCompressor

            static member Instance =
                if isNull Compressor.instance then  
                    Compressor.instance <- new TJCompressor()

                Compressor.instance

        end

    type Framebuffer with
        member x.DownloadJpegColor() =
            let jpeg = Compressor.Instance
            let ctx = x.Context
            use __ = ctx.ResourceLock

            let color = x.Attachments.[DefaultSemantic.Colors] |> unbox<Renderbuffer>

            let size = color.Size
            if color.Samples > 1 then
                let resolved = GL.GenRenderbuffer()
                GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, resolved)
                GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, color.Size.X, color.Size.Y)
                GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0)

                let fbo = GL.GenFramebuffer()
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo)
                GL.FramebufferRenderbuffer(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, resolved)

                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, x.Handle)

                GL.BlitFramebuffer(
                    0, 0, size.X - 1, size.Y - 1, 
                    0, 0, size.X - 1, size.Y - 1,
                    ClearBufferMask.ColorBufferBit,
                    BlitFramebufferFilter.Nearest
                )

                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0)
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0)
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo)
                    
                try
                    ctx |> downloadFBO jpeg size
                finally
                    GL.DeleteFramebuffer(fbo)
                    GL.DeleteRenderbuffer(resolved)

            else
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, x.Handle)
                ctx |> downloadFBO jpeg size

    type IFramebuffer with
        member x.DownloadJpegColor() =
            match x with
                | :? Framebuffer as fbo -> fbo.DownloadJpegColor()
                | _ -> failwith "not implemented"
 
    type WebSocket with
        member x.readMessage() =
            socket {
                let! (t,d,fin) = x.read()
                if fin then 
                    return (t,d)
                else
                    let! (_, rest) = x.readMessage()
                    return (t, Array.append d rest)
            }

[<AutoOpen>]
module TimeExtensions =
    open System.Diagnostics
    let private sw = Stopwatch()
    let private start = MicroTime(TimeSpan.FromTicks(DateTime.Now.Ticks))
    do sw.Start()

    type MicroTime with
        static member Now = start + sw.MicroTime



type ClientInfo =
    {
        token : AdaptiveToken
        signature : IFramebufferSignature
        targetId : string
        sceneName : string
        session : Guid
        size : V2i
        samples : int
        time : MicroTime
    }

type ClientState =
    {
        viewTrafo   : Trafo3d
        projTrafo   : Trafo3d
    }
    
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ClientState =
    let pickRay (pp : PixelPosition) (state : ClientState) =
        let n = pp.NormalizedPosition
        let ndc = V3d(2.0 * n.X - 1.0, 1.0 - 2.0 * n.Y, 0.0)
        let viewDir = state.projTrafo.Backward.TransformPosProj ndc |> Vec.normalize
        let ray = Ray3d(V3d.Zero, viewDir)
        ray.Transformed(state.viewTrafo.Backward)


type ClientValues internal(_signature : IFramebufferSignature) =
    
    let _time = Mod.init MicroTime.Zero
    let _session = Mod.init Guid.Empty
    let _size = Mod.init V2i.II
    let _viewTrafo = Mod.init Trafo3d.Identity
    let _projTrafo = Mod.init Trafo3d.Identity
    let _samples = Mod.init 1

    member internal x.Update(info : ClientInfo, state : ClientState) =
        _time.Value <- info.time
        _session.Value <- info.session

        _size.Value <- info.size
        _viewTrafo.Value <- state.viewTrafo
        _projTrafo.Value <- state.projTrafo
        _samples.Value <- info.samples
//
//        _size.MarkOutdated()
//        _viewTrafo.MarkOutdated()
//        _projTrafo.MarkOutdated()
//        _samples.MarkOutdated()
//        _session.MarkOutdated()

    member x.runtime = _signature.Runtime
    member x.signature = _signature
    member x.size = _size :> IMod<_>
    member x.time = _time :> IMod<_>
    member x.session = _session :> IMod<_>
    member x.viewTrafo = _viewTrafo :> IMod<_>
    member x.projTrafo = _projTrafo :> IMod<_>
    member x.samples = _samples :> IMod<_>


[<AbstractClass>]
type Scene() =
    let cache = ConcurrentDictionary<IFramebufferSignature, ConcreteScene>()
    let clientInfos = ConcurrentDictionary<Guid * string, unit -> Option<ClientInfo * ClientState>>()


    member internal x.AddClientInfo(session : Guid, id : string, getter : unit -> Option<ClientInfo * ClientState>) =
        clientInfos.TryAdd((session, id), getter) |> ignore
        
    member internal x.RemoveClientInfo(session : Guid, id : string) =
        clientInfos.TryRemove((session, id)) |> ignore

    member x.TryGetClientInfo(session : Guid, id : string) : Option<ClientInfo * ClientState> =
        match clientInfos.TryGetValue ((session, id)) with
            | (true, getter) -> getter()
            | _ -> None

    member internal x.GetConcreteScene(name : string, signature : IFramebufferSignature) =
        cache.GetOrAdd(signature, fun signature -> ConcreteScene(name, signature, x))

    abstract member Compile : ClientValues -> IRenderTask

and internal ConcreteScene(name : string, signature : IFramebufferSignature, scene : Scene) as this =
    inherit AdaptiveObject()

    static let deleteTimeout = 1000

    let mutable refCount = 0
    let mutable task : Option<IRenderTask> = None

    let state = ClientValues(signature)


    let destroy (o : obj) =
        let deadTask = 
            lock this (fun () ->
                if refCount = 0 then
                    match task with
                        | Some t -> 
                            task <- None
                            Some t
                        | None -> 
                            Log.error "[Scene] %s: invalid state" name
                            None
                else
                    None
            )

        match deadTask with
            | Some t ->
                // TODO: fix in rendering
                transact ( fun () -> t.Dispose() )
                Log.line "[Scene] %s: destroyed" name
            | None ->
                ()
    
    let timer = new Timer(TimerCallback(destroy), null, Timeout.Infinite, Timeout.Infinite)

    let create() =
        refCount <- refCount + 1
        if refCount = 1 then
            match task with
                | Some task -> 
                    // refCount was 0 but task was not deleted
                    timer.Change(Timeout.Infinite, Timeout.Infinite) |> ignore
                    task
                | None ->
                    // refCount was 0 and there was no task
                    Log.line "[Scene] %s: created" name
                    let t = scene.Compile state
                    task <- Some t
                    t
        else
            match task with
                | Some t -> t
                | None -> failwithf "[Scene] %s: invalid state" name
        
    let release() =
        refCount <- refCount - 1
        if refCount = 0 then
            timer.Change(deleteTimeout, Timeout.Infinite) |> ignore
            
    member x.Scene = scene
                    
    member internal x.Apply(info : ClientInfo, s : ClientState) =
        state.Update(info, s)

    member internal x.State = state

    member internal x.CreateNewRenderTask() =
        lock x (fun () ->
            let task = create()

            { new AbstractRenderTask() with
                member x.FramebufferSignature = task.FramebufferSignature
                member x.Runtime = task.Runtime
                member x.PerformUpdate(t,rt) = task.Update(t, rt)
                member x.Perform(t,rt,o) = task.Run(t, rt, o)
                
                member x.Dispose() = 
                    lock x (fun () ->
                        release()
                    )
                
                member x.Use f = task.Use f
    
            } :> IRenderTask
        )

    member x.FramebufferSignature = signature

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Scene =

    [<AutoOpen>]
    module Implementation = 
        type EmptyScene() =
            inherit Scene()
            override x.Compile(_) = RenderTask.empty

        type ArrayScene(scenes : Scene[]) =
            inherit Scene()
            override x.Compile(v) =
                scenes |> Array.map (fun s -> s.Compile v) |> RenderTask.ofArray

        type CustomScene(compile : ClientValues -> IRenderTask) =
            inherit Scene()
            override x.Compile(v) = compile v
            

    let empty = EmptyScene() :> Scene

    let custom (compile : ClientValues -> IRenderTask) = CustomScene(compile) :> Scene

    let ofArray (scenes : Scene[]) = ArrayScene(scenes) :> Scene
    let ofList (scenes : list<Scene>) = ArrayScene(List.toArray scenes) :> Scene
    let ofSeq (scenes : seq<Scene>) = ArrayScene(Seq.toArray scenes) :> Scene
    
        
type Server =
    {
        runtime         : IRuntime
        content         : string -> Option<Scene>
        getState        : ClientInfo -> Option<ClientState>
    }

type internal ClientRenderTask internal(server : Server, getScene : IFramebufferSignature -> string -> ConcreteScene) =
    let runtime = server.runtime
    let mutable task = RenderTask.empty

    let targetSize = Mod.init V2i.II
    
    let mutable currentSize = -V2i.II
    let mutable currentSignature = Unchecked.defaultof<IFramebufferSignature>
    
    let mutable depth : Option<IRenderbuffer> = None 
    let mutable color : Option<IRenderbuffer> = None
    let mutable target : Option<IFramebuffer> = None 

    static let mutable threadCount = 0


    let deleteFramebuffer() =
        target |> Option.iter runtime.DeleteFramebuffer
        depth |> Option.iter runtime.DeleteRenderbuffer
        color |> Option.iter runtime.DeleteRenderbuffer
        target <- None
        color <- None
        depth <- None
        currentSignature <- Unchecked.defaultof<IFramebufferSignature>
        currentSize <- -V2i.II

    let recreateFramebuffer (size : V2i) (signature : IFramebufferSignature) =
        deleteFramebuffer()

        currentSize <- size
        currentSignature <- signature

        let depthSignature =
            match signature.DepthAttachment with
                | Some att -> att
                | _ -> { format = RenderbufferFormat.Depth24Stencil8; samples = 1 }
                
        let colorSignature =
            match Map.tryFind 0 signature.ColorAttachments with
                | Some (sem, att) when sem = DefaultSemantic.Colors -> att
                | _ -> { format = RenderbufferFormat.Rgba8; samples = 1 }

        let d = runtime.CreateRenderbuffer(currentSize, depthSignature.format, depthSignature.samples)
        let c = runtime.CreateRenderbuffer(currentSize, colorSignature.format, colorSignature.samples)
        let newTarget =
            runtime.CreateFramebuffer(
                signature,
                [
                    DefaultSemantic.Colors, c :> IFramebufferOutput
                    DefaultSemantic.Depth, d :> IFramebufferOutput
                ]
            )

        depth <- Some d
        color <- Some c
        target <- Some newTarget
        newTarget

    let rec getFramebuffer (size : V2i) (signature : IFramebufferSignature) =
        match target with
            | Some t ->
                if currentSize <> size || currentSignature <> signature then
                    recreateFramebuffer size signature
                else
                    t
            | None ->
                recreateFramebuffer size signature
                    
    let renderTime = Stopwatch()
    let compressTime = Stopwatch()
    let mutable frameCount = 0

    let mutable currentScene : Option<string * ConcreteScene> = None

    let mutable lastInfo : Option<ClientInfo> = None

    let getInfo() =
        match lastInfo with
            | Some lastInfo ->
                let now = { lastInfo with time = MicroTime.Now }
                match server.getState now with
                    | Some cam -> Some (now, cam)
                    | None -> None
            | _ ->
                None

    let rebuildTask (name : string) (signature : IFramebufferSignature) =
        match currentScene with
            | Some (oldName, scene) -> Log.warn "rebuild(%s <> %s)"  oldName name
            | _ -> ()

        transact (fun () -> task.Dispose())
        let newScene = getScene signature name
        let clear = runtime.CompileClear(signature, Mod.constant C4f.Black, Mod.constant 1.0)
        let render = newScene.CreateNewRenderTask()
        task <- RenderTask.ofList [clear; render]

        // needs to hold
        let lastInfo = lastInfo.Value
        newScene.Scene.AddClientInfo(lastInfo.session, lastInfo.targetId, getInfo)

        currentScene <- Some (name, newScene)
        newScene, task

    let getSceneAndTask (name : string) (signature : IFramebufferSignature) =
        match currentScene with
            | Some(sceneName, scene) ->
                if sceneName <> name || scene.FramebufferSignature <> signature then
                    match lastInfo with
                        | Some info -> scene.Scene.RemoveClientInfo(info.session, info.targetId)
                        | _ -> ()
                    rebuildTask name signature
                else
                    scene, task
            | None ->
                rebuildTask name signature

    member x.Run(token : AdaptiveToken, info : ClientInfo, state : ClientState) =
        lastInfo <- Some info

        let scene, task = getSceneAndTask info.sceneName info.signature

        let mutable t = Unchecked.defaultof<IDisposable>
        let mutable target = Unchecked.defaultof<IFramebuffer>

        lock scene (fun () ->
            if Interlocked.Increment(&threadCount) > 1 then
                Log.warn "HATE"
            t <- runtime.ContextLock
            target <- getFramebuffer info.size info.signature
            let innerToken = token.Isolated
            try
                scene.EvaluateAlways innerToken (fun innerToken ->
                    scene.OutOfDate <- true
                    renderTime.Start()
                    transact (fun () -> scene.Apply(info, state))

                    task.Run(innerToken, RenderToken.Empty, OutputDescription.ofFramebuffer target)
                    renderTime.Stop()
                    innerToken.Release()
                )
            finally
                //printfn "race here"
                innerToken.Release()
//                let real = scene.State.projTrafo |> Mod.force
//                let should = state.projTrafo
//                if real <> should then
//                    Log.warn "bad"
                Interlocked.Decrement(&threadCount) |> ignore
        )
        compressTime.Start()
        let data = target.DownloadJpegColor()
        compressTime.Stop()
        t.Dispose()
        frameCount <- frameCount + 1
        data

    member x.Dispose() =
        match lastInfo, currentScene with
            | Some i, Some(_,s) ->
                s.Scene.RemoveClientInfo(i.session, i.targetId)
                lastInfo <- None
            | _ -> 
                ()
        deleteFramebuffer()
        task.Dispose()
        renderTime.Reset()
        compressTime.Reset()
        frameCount <- 0
        currentScene <- None
        lastInfo <- None

    member x.RenderTime = renderTime.MicroTime
    member x.CompressTime = compressTime.MicroTime
    member x.FrameCount = frameCount

    interface IDisposable with
        member x.Dispose() =
            x.Dispose()

type internal ClientCreateInfo =
    {
        server          : Server
        session         : Guid
        id              : string
        sceneName       : string
        samples         : int
        socket          : WebSocket
        getSignature    : int -> IFramebufferSignature
    }

type internal Client(createInfo : ClientCreateInfo, getState : ClientInfo -> ClientState, getContent : IFramebufferSignature -> string -> ConcreteScene) as this =
    static let mutable currentId = 0
 
    let id = Interlocked.Increment(&currentId)
    let sender = AdaptiveObject()
    let requestedSize : MVar<V2i> = MVar.empty()
    let mutable createInfo = createInfo
    let mutable task = new ClientRenderTask(createInfo.server, getContent)
    let mutable running = false
    let mutable disposed = 0

    let mutable frameCount = 0
    let roundTripTime = Stopwatch()
    let invalidateTime = Stopwatch()

    let send (cmd : Command) =
        let data = Pickler.json.Pickle cmd
        let res = createInfo.socket.send Opcode.Text (ByteSegment data) true |> Async.RunSynchronously
        match res with
            | Choice1Of2 () -> ()
            | Choice2Of2 err ->
                Log.warn "[Client] %d: send of %A faulted (stopping): %A" id cmd err
                this.Dispose()
                //failwithf "[Client] %d: %A" id err

    let subscribe() =
        sender.AddMarkingCallback(fun () ->
            send Invalidate
            invalidateTime.Start()
        )

    let mutable info =
        {
            token = Unchecked.defaultof<AdaptiveToken>
            signature = createInfo.getSignature createInfo.samples
            targetId = createInfo.id
            sceneName = createInfo.sceneName
            session = createInfo.session
            samples = createInfo.samples
            size = V2i.II
            time = MicroTime.Now
        }

    let mutable subscription = subscribe()


    let renderLoop() =
        while running do
            let size = MVar.take requestedSize
            if size.AllGreater 0 then

                sender.EvaluateAlways AdaptiveToken.Top (fun token ->
                    let info = Interlocked.Change(&info, fun info -> { info with token = token; size = size; time = MicroTime.Now })
                    try
                        let state = getState info
                        let data = task.Run(token, info, state)

                        let res = createInfo.socket.send Opcode.Binary (ByteSegment data) true |> Async.RunSynchronously
                        match res with
                            | Choice1Of2() -> ()
                            | Choice2Of2 err ->
                                running <- false
                                Log.warn "[Client] %d: could not send render-result due to %A (stopping)" id err
                    with e ->
                        running <- false
                        Log.error "[Client] %d: rendering faulted with %A (stopping)" id e
                    
                )

    let mutable renderThread = new Thread(ThreadStart(renderLoop), IsBackground = true, Name = "ClientRenderer_" + string createInfo.session)


    member x.Info = info
    member x.FrameCount = frameCount
    member x.FrameTime = roundTripTime.MicroTime
    member x.InvalidateTime = invalidateTime.MicroTime
    member x.RenderTime = task.RenderTime
    member x.CompressTime = task.CompressTime


    member x.Revive(newInfo : ClientCreateInfo) =
        if Interlocked.Exchange(&disposed, 0) = 1 then
            Log.line "[Client] %d: revived" id
            createInfo <- newInfo
            task <- new ClientRenderTask(newInfo.server, getContent)
            subscription <- subscribe()
            renderThread <- new Thread(ThreadStart(renderLoop), IsBackground = true, Name = "ClientRenderer_" + string info.session)

    member x.Dispose() =
        if Interlocked.Exchange(&disposed, 1) = 0 then
            task.Dispose()
            subscription.Dispose()
            running <- false
            MVar.put requestedSize V2i.Zero
            frameCount <- 0
            roundTripTime.Reset()
            invalidateTime.Reset()

    member x.Run =
        running <- true
        renderThread.Start()
        socket {
            
            Log.line "[Client] %d: running %s" id info.sceneName
            try
                while running do
                    let! (code, data) = createInfo.socket.readMessage()

                    match code with
                        | Opcode.Text ->
                            try
                                let msg : Message = Pickler.json.UnPickle data

                                match msg with
                                    | RequestImage size ->
                                        invalidateTime.Stop()
                                        roundTripTime.Start()
                                        MVar.put requestedSize size

                                    | Rendered ->
                                        roundTripTime.Stop()
                                        frameCount <- frameCount + 1

                                    | Shutdown ->
                                        running <- false

                                    | Change(scene, samples) ->
                                        let signature = createInfo.getSignature samples
                                        Interlocked.Change(&info, fun info -> { info with samples = samples; signature = signature; sceneName = scene }) |> ignore

                            with e ->
                                Log.warn "[Client] %d: unexpected message %A" id (Encoding.UTF8.GetString data)

                        | Opcode.Binary ->
                            Log.warn "[Client] %d: unexpected binary message" id
                        | Opcode.Close ->
                            running <- false
                        | _ ->
                            ()

            finally
                Log.line "[Client] %d: stopped" id
                x.Dispose()
        }

    interface IDisposable with
        member x.Dispose() = x.Dispose()





[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Server =
    open System.IO
    open System.Collections.Generic
    open System.Collections.Concurrent
    open System.Runtime.CompilerServices

    [<AutoOpen>]
    module private Utils = 
        type ClientStatistics =
            {
                session         : Guid
                name            : string
                frameCount      : int
                invalidateTime  : float
                renderTime      : float
                compressTime    : float
                frameTime       : float
            }

        type Client with
            member internal x.GetStatistics() =
                {
                    session = x.Info.session
                    name = x.Info.sceneName
                    frameCount = x.FrameCount
                    invalidateTime = x.InvalidateTime.TotalSeconds
                    renderTime = x.RenderTime.TotalSeconds
                    compressTime = x.CompressTime.TotalSeconds
                    frameTime = x.FrameTime.TotalSeconds
                }

        let mimeTypes =
            Dictionary.ofList [
                ".js", "text/javascript"
                ".css", "text/css"
                ".svg", "image/svg+xml"
            ]

        let assemblyWebParts = 
            let assembly = typeof<Client>.Assembly
            assembly.GetManifestResourceNames()
                |> Array.toList
                |> List.collect (fun name ->
                    use stream = assembly.GetManifestResourceStream name
                    let reader = new StreamReader(stream)
                    let text = reader.ReadToEnd()

                    let ext = Path.GetExtension name

                    // respond with the text
                    let part = OK text

                    // set the mime-type (if known)
                    let part = 
                        match mimeTypes.TryGetValue ext with
                            | (true, mime) -> part >=> Writers.setMimeType mime
                            | _ -> part 

                    // index.* is also reachable via /
                    let parts =
                        if Path.GetFileNameWithoutExtension name = "index" then
                            [
                                path ("/" + name) >=> part
                                path "/" >=> Redirection.redirectRelative ("/" + name)
                            ]
                        else
                            [ path ("/" + name) >=> part ]
                
                    // return the part
                    parts
                )

        let (|Int|_|) (str : string) =
            match Int32.TryParse str with
                | (true, v) -> Some v
                | _ -> None

        let noState =
            {
                viewTrafo = Trafo3d.Identity
                projTrafo = Trafo3d.Identity
            }
    let empty (r : IRuntime) =
        {
            runtime = r
            content = fun _ -> None
            getState = fun _ -> None
        }

    let withState (get : ClientInfo -> ClientState) (server : Server) =
        { server with getState = get >> Some }

    let withContent (get : string -> Option<Scene>) (server : Server) =
        { server with content = get }

    let addScenes (find : string -> Option<Scene>) (server : Server) =
        { 
            server with
                content = fun n ->
                    match find n with
                        | Some s -> Some s
                        | None -> server.content n
        }

    let addScene (name : string) (scene : Scene) (server : Server) =
        { 
            server with
                content = fun n ->
                    if n = name then Some scene
                    else server.content n
        }

    let add (name : string) (create : ClientValues -> IRenderTask) (server : Server) =
        addScene name (Scene.custom create) server

    let toWebPart (info : Server) =
        let clients = Dict<Guid * string, Client>()

        let signatures = ConcurrentDictionary<int, IFramebufferSignature>()

        let getSignature (samples : int) =
            signatures.GetOrAdd(samples, fun samples ->
                info.runtime.CreateFramebufferSignature(
                    samples,
                    [
                        DefaultSemantic.Colors, RenderbufferFormat.Rgba8
                        DefaultSemantic.Depth, RenderbufferFormat.Depth24Stencil8
                    ]
                )
            )

        let getState (ci : ClientInfo) =
            match info.getState ci with
                | Some c -> c
                | None -> noState



        let content signature id  =
            match info.content id with   
                | Some scene -> scene.GetConcreteScene(id, signature)
                | None -> Scene.empty.GetConcreteScene(id, signature)

        let render (targetId : string) (ws : WebSocket) (context: HttpContext) =
            let request = context.request
            let args = request.query |> List.choose (function (n,Some v) -> Some(n,v) | _ -> None) |> Map.ofList

            let sceneName =
                match Map.tryFind "scene" args with
                    | Some scene -> scene
                    | _ -> targetId

            let samples =
                match Map.tryFind "samples" args with
                    | Some (Int samples) -> samples
                    | _ -> 1

            let sessionId =
                match Map.tryFind "session" args with
                    | Some id -> Guid.Parse id
                    | _ -> Guid.NewGuid()

            let createInfo =
                {
                    server          = info
                    session         = sessionId
                    id              = targetId
                    sceneName       = sceneName
                    samples         = samples
                    socket          = ws
                    getSignature    = getSignature
                }            

            let client = 
                let key = (sessionId, targetId)
                lock clients (fun () ->
                    clients.GetOrCreate(key, fun (sessionId, targetId) ->
                        Log.line "[Server] created client for (%A/%s)" sessionId targetId
                        new Client(createInfo, getState, content)
                    )
                )

            client.Revive(createInfo)
            client.Run

        let statistics (ctx : HttpContext) =
            let request = ctx.request
            let args = request.query |> List.choose (function (n,Some v) -> Some(n,v) | _ -> None) |> Map.ofList

            let clients = 
                match Map.tryFind "session" args, Map.tryFind "name" args with
                    | Some sid, Some name -> 
                        match Guid.TryParse sid with
                            | (true, sid) -> 
                                match clients.TryGetValue((sid, name)) with
                                    | (true, c) -> [| c |]
                                    | _ -> [||]
                            | _ ->
                                [||]
                    | _ -> 
                        clients.Values |> Seq.toArray

            let stats = clients  |> Array.filter (fun v -> not (isNull (v :> obj))) |> Array.map (fun c -> c.GetStatistics()) |> Array.filter (fun s -> s.frameCount > 0)
            let json = Pickler.json.PickleToString stats
            ctx |> (OK json >=> Writers.setMimeType "text/json")

        let screenshot (sceneName : string) (context: HttpContext) =
            let request = context.request
            let args = request.query |> List.choose (function (n,Some v) -> Some(n,v) | _ -> None) |> Map.ofList

            let samples = 
                match Map.tryFind "samples" args with
                    | Some (Int s) -> s
                    | _ -> 1

            let signature = getSignature samples

            match Map.tryFind "w" args, Map.tryFind "h" args with
                | Some (Int w), Some (Int h) when w > 0 && h > 0 ->
                    let scene = content signature sceneName
                    use task = new ClientRenderTask(info, content)

                    let clientInfo = 
                        {
                            token = AdaptiveToken.Top
                            signature = signature
                            targetId = ""
                            sceneName = sceneName
                            session = Guid.Empty
                            size = V2i(w,h)
                            samples = samples
                            time = MicroTime.Now
                        }

                    let state = getState clientInfo
                    let data = task.Run(AdaptiveToken.Top, clientInfo, state)

                    context |> (ok data >=> Writers.setMimeType "image/jpeg")
                | _ ->
                    context |> BAD_REQUEST "no width/height specified"

        let parts = 
            assemblyWebParts @ [ 
                pathScan "/render/%s" (render >> handShake)
                path "/stats.json" >=> statistics 
                pathScan "/screenshot/%s" (screenshot) 
            ]
        choose parts

    let run (port : int) (server : Server) =
        server |> toWebPart |> List.singleton |> WebPart.runServer port

    let start (port : int) (server : Server) =
        server |> toWebPart |> List.singleton |> WebPart.startServer port
