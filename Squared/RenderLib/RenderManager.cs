﻿#pragma warning disable 0420 // a reference to a volatile field will not be treated as volatile

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Squared.Util;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;
using Squared.Game;
using Microsoft.Xna.Framework;
using Squared.Render.Internal;

namespace Squared.Render {
    public static class RenderExtensionMethods {
        public static int ComputePrimitiveCount (this PrimitiveType primitiveType, int vertexCount) {
            switch (primitiveType) {
                case PrimitiveType.LineStrip:
                    return vertexCount - 1;
                case PrimitiveType.LineList:
                    return vertexCount / 2;
                case PrimitiveType.TriangleStrip:
                    return vertexCount - 2;
                case PrimitiveType.TriangleList:
                    return vertexCount / 3;
                default:
                    throw new ArgumentException();
            }
        }

        public static int ComputeVertexCount (this PrimitiveType primitiveType, int primitiveCount) {
            switch (primitiveType) {
                case PrimitiveType.LineStrip:
                    return primitiveCount + 1;
                case PrimitiveType.LineList:
                    return primitiveCount * 2;
                case PrimitiveType.TriangleStrip:
                    return primitiveCount + 2;
                case PrimitiveType.TriangleList:
                    return primitiveCount * 3;
                default:
                    throw new ArgumentException();
            }
        }
    }

    public interface IBatchContainer {
        void Add (Batch batch);
        RenderManager RenderManager { get; }
        bool IsDisposed { get; }
    }

    public sealed class DeviceManager {
        public struct ActiveMaterial : IDisposable {
            public readonly DeviceManager DeviceManager;
            public readonly Material Material;

            public ActiveMaterial (DeviceManager deviceManager, Material material) {
                DeviceManager = deviceManager;
                Material = material;

                if (deviceManager.CurrentMaterial != material) {
                    if (deviceManager.CurrentMaterial != null)
                        deviceManager.CurrentMaterial.End(deviceManager);

                    deviceManager.CurrentMaterial = material;

                    material.Begin(deviceManager);
                }
            }

            public void Dispose () {
            }
        }

        private readonly Stack<RenderTargetBinding[]> RenderTargetStack = new Stack<RenderTargetBinding[]>();

        public readonly GraphicsDevice Device;
        private Effect ParameterEffect;
        private Material CurrentMaterial;
        internal Effect CurrentEffect;

        public DeviceManager (GraphicsDevice device) {
            Device = device;
            CurrentEffect = null;
        }

        public void SetParameterEffect (Effect effect) {
            ParameterEffect = effect;
        }

        public void PushRenderTarget (RenderTarget2D newRenderTarget) {
            RenderTargetStack.Push(Device.GetRenderTargets());
            Device.SetRenderTarget(newRenderTarget);
        }

        public void PopRenderTarget () {
            Device.SetRenderTargets(RenderTargetStack.Pop());
        }

        public EffectParameterCollection SharedParameters {
            get {
                return ParameterEffect.Parameters;
            }
        }

        public EffectParameterCollection CurrentParameters {
            get {
                return CurrentEffect.Parameters;
            }
        }

        public ActiveMaterial ApplyMaterial (Material material) {
            return new ActiveMaterial(this, material);
        }

        public void Finish () {
            if (CurrentMaterial != null) {
                CurrentMaterial.End(this);
                CurrentMaterial = null;
            }

            CurrentEffect = null;

            for (var i = 0; i < 4; i++) {
                Device.Textures[i] = null;
            }

            Device.SetRenderTargets();
            Device.SetVertexBuffers();
        }
    }

    // Thread-safe
    public sealed class RenderManager {
        class WorkerThreadInfo {
            public volatile int Start, Count;
            public volatile Frame Frame;
        }

        public readonly DeviceManager DeviceManager;

        private int _FrameCount = 0;
        private Dictionary<Type, IArrayPoolAllocator> _ArrayAllocators = 
            new Dictionary<Type, IArrayPoolAllocator>(new ReferenceComparer<Type>());
        private Dictionary<Type, IBatchPool> _BatchAllocators =
            new Dictionary<Type, IBatchPool>(new ReferenceComparer<Type>());
        private FramePool _FrameAllocator;

        private WorkerThreadInfo[] _WorkerInfo;
#if XBOX
        private WorkerThread[] _WorkerThreads;
#else
        private Action[] _WorkerDelegates;
#endif

        public RenderManager (GraphicsDevice device) {
            DeviceManager = new DeviceManager(device);
            _FrameAllocator = new FramePool(this);

            int threadCount = Math.Max(2, Math.Min(8, Environment.ProcessorCount));
#if XBOX
            // XNA reserves two hardware threads for its own purposes
            threadCount -= 2;
#endif

            _WorkerInfo = new WorkerThreadInfo[threadCount];
            for (int i = 0; i < threadCount; i++)
                _WorkerInfo[i] = new WorkerThreadInfo();

#if XBOX
            _WorkerThreads = new WorkerThread[threadCount];

            for (int i = 0; i < _WorkerThreads.Length; i++) {
                _WorkerThreads[i] = new WorkerThread(WorkerThreadFunc, i);
                _WorkerThreads[i].Tag = _WorkerInfo[i];
            }
#else
            _WorkerDelegates = new Action[threadCount];

            for (int i = 0; i < threadCount; i++) {
                // Make a copy so that each delegate gets a different value
                int j = i;
                _WorkerDelegates[i] = () =>
                    WorkerThreadFunc(_WorkerInfo[j]);
            }
#endif
        }

#if XBOX
        private void WorkerThreadFunc (WorkerThread thread) {
            var info = thread.Tag as WorkerThreadInfo;
#else
        private void WorkerThreadFunc (WorkerThreadInfo info) {
#endif
            Frame frame;
            int start, count;
            lock (info) {
                frame = info.Frame;
                start = info.Start;
                count = info.Count;
            }

            try {
                frame.PrepareSubset(start, count);
            } finally {
                lock (info) {
                    info.Frame = null;
                    info.Start = info.Count = 0;
                }
            }
        }

        internal void ParallelPrepare (Frame frame) {
            int batchCount = frame.Batches.Count;
            int chunkSize = (int)Math.Ceiling((float)batchCount / _WorkerInfo.Length);

            for (int i = 0, j = 0; i < _WorkerInfo.Length; i++, j += chunkSize) {
                var info = _WorkerInfo[i];

                lock (info) {
                    if (info.Frame != null)
                        throw new InvalidOperationException("A render is already in progress");

                    info.Frame = frame;
                    info.Start = j;
                    info.Count = Math.Min(chunkSize, batchCount - j);
                }

#if XBOX
                _WorkerThreads[i].RequestWork();
#endif
            }

#if XBOX
            for (int i = 0; i < _WorkerThreads.Length; i++) {
                var wt = _WorkerThreads[i];

                wt.WaitForPendingWork();

                var info = wt.Tag as WorkerThreadInfo;
                lock (info) {
                    if (info.Frame != null)
                        throw new InvalidOperationException();
                }
            }
#else
            System.Threading.Tasks.Parallel.Invoke(
                new System.Threading.Tasks.ParallelOptions {
                    MaxDegreeOfParallelism = _WorkerInfo.Length
                },
                _WorkerDelegates
            );
#endif
        }

        internal int PickFrameIndex () {
            return Interlocked.Increment(ref _FrameCount);
        }

        internal void ReleaseFrame (Frame frame) {
            _FrameAllocator.Release(frame);
        }

        public Frame CreateFrame () {
            CollectAllocators();

            return _FrameAllocator.Allocate();
        }

        public void CollectAllocators () {
            lock (_ArrayAllocators)
                foreach (var allocator in _ArrayAllocators.Values)
                    allocator.Step();
        }

        public ArrayPoolAllocator<T> GetArrayAllocator<T> () {
            var t = typeof(T);
            IArrayPoolAllocator o;
            lock (_ArrayAllocators)
                _ArrayAllocators.TryGetValue(t, out o);

            ArrayPoolAllocator<T> result;
            if (o == null) {
                result = new ArrayPoolAllocator<T>();
                lock (_ArrayAllocators) {
                    if (!_ArrayAllocators.TryGetValue(t, out o))
                        _ArrayAllocators[t] = result;
                    else
                        result = (ArrayPoolAllocator<T>)o;
                }
            } else {
                result = (ArrayPoolAllocator<T>)o;
            }

            return result;
        }

        public T AllocateBatch<T> () 
            where T : Batch, new() {

            var t = typeof(T);
            IBatchPool p;
            lock (_BatchAllocators)
                _BatchAllocators.TryGetValue(t, out p);

            BatchPool<T> allocator;
            if (p == null) {
                Func<IBatchPool, T> constructFn = (pool) =>
                    new T() {
                        Pool = pool
                    };

                allocator = new BatchPool<T>(constructFn);

                lock (_BatchAllocators) {
                    if (!_BatchAllocators.TryGetValue(t, out p))
                        _BatchAllocators[t] = allocator;
                    else
                        allocator = (BatchPool<T>)p;
                }
            } else {
                allocator = (BatchPool<T>)p;
            }

            return allocator.Allocate();
        }
    }

    public sealed class BatchComparer : IComparer<Batch> {
        public int Compare (Batch x, Batch y) {
            if (x == null) {
                if (y == null)
                    return 0;
                else
                    return -1;
            } else if (y == null) {
                return 1;
            }

            int result = x.Layer - y.Layer;
            if (result == 0) {
                int mx = 0, my = 0;

                if (x.Material != null)
                    mx = x.Material.MaterialID;
                if (y.Material != null)
                    my = y.Material.MaterialID;

                result = mx - my;
            }

            return result;
        }
    }

    // Thread-safe
    public sealed class Frame : IDisposable, IBatchContainer {
        private const int State_Initialized = 0;
        private const int State_Preparing = 1;
        private const int State_Prepared = 2;
        private const int State_Drawing = 3;
        private const int State_Drawn = 4;
        private const int State_Disposed = 5;

        public static BatchComparer BatchComparer = new BatchComparer();

        private static ListPool<Batch> _ListPool = new ListPool<Batch>(
            16, 256, 4096
        );

        public RenderManager RenderManager;
        public int Index;

        public List<Batch> Batches;

        volatile int State = State_Disposed;

        // If you allocate a frame manually instead of using the pool, you'll need to initialize it
        //  yourself using Initialize (below).
        public Frame () {
        }

        RenderManager IBatchContainer.RenderManager {
            get {
                return RenderManager;
            }
        }

        internal void Initialize (RenderManager renderManager, int index) {
            Batches = _ListPool.Allocate();
            RenderManager = renderManager;
            Index = index;
            State = State_Initialized;
        }

        public void Add (Batch batch) {
            if (State != State_Initialized)
                throw new InvalidOperationException();

            lock (Batches) {
#if DEBUG
                if (Batches.Contains(batch))
                    throw new InvalidOperationException("Batch already added to this frame");
#endif

                Batches.Add(batch);
            }
        }

        public void Prepare (bool parallel) {
            if (Interlocked.Exchange(ref State, State_Preparing) != State_Initialized)
                throw new InvalidOperationException();

            BatchCombiner.CombineBatches(Batches);
            Batches.Sort(BatchComparer);

            if (parallel)
                RenderManager.ParallelPrepare(this);
            else
                PrepareSubset(0, Batches.Count);

            if (Interlocked.Exchange(ref State, State_Prepared) != State_Preparing)
                throw new InvalidOperationException();
        }

        internal void PrepareSubset (int start, int count) {
            int end = start + count;

            for (int i = start; i < end; i++) {
                var batch = Batches[i];
                if (batch != null)
                    batch.Prepare();
            }
        }

        public void Draw () {
            if (Interlocked.Exchange(ref State, State_Drawing) != State_Prepared)
                throw new InvalidOperationException();

            var dm = RenderManager.DeviceManager;
            var device = dm.Device;

            int c = Batches.Count;
            for (int i = 0; i < c; i++) {
                var batch = Batches[i];
                if (batch != null)
                    batch.IssueAndWrapExceptions(dm);
            }

            dm.Finish();

            if (Interlocked.Exchange(ref State, State_Drawn) != State_Drawing)
                throw new InvalidOperationException();
        }

        public void Dispose () {
            if (State == State_Disposed)
                return;

            for (int i = 0; i < Batches.Count; i++) {
                var batch = Batches[i];
                if (batch != null)
                    batch.ReleaseResources();
            }

            _ListPool.Release(ref Batches);

            RenderManager.ReleaseFrame(this);

            State = State_Disposed;
        }

        public bool IsDisposed {
            get {
                return State == State_Disposed;
            }
        }
    }

    public abstract class Batch : IDisposable {
        private static Dictionary<Type, int> TypeIds = new Dictionary<Type, int>();

        public static bool CaptureStackTraces = false;

        public readonly int TypeId;

        public StackTrace StackTrace;
        public IBatchContainer Container;
        public int Layer;
        public Material Material;

        internal int Index;
        internal bool ReleaseAfterDraw;
        internal bool Released;
        internal IBatchPool Pool;

        protected static int _BatchCount = 0;

        protected Batch () {
            var thisType = GetType();
            if (!TypeIds.TryGetValue(thisType, out TypeId))
                TypeIds.Add(thisType, TypeId = TypeIds.Count);
        }

        protected void Initialize (IBatchContainer container, int layer, Material material) {
            if ((material != null) && (material.IsDisposed))
                throw new ObjectDisposedException("material");

            StackTrace = null;
            Released = false;
            ReleaseAfterDraw = false;
            Container = container;
            Layer = layer;
            Material = material;

            Index = Interlocked.Increment(ref _BatchCount);

            container.Add(this);
        }

        /// <summary>
        /// Adds a previously-constructed batch to a new frame/container.
        /// Use this if you already created a batch in a previous frame and wish to use it again.
        /// </summary>
        public void Reuse (IBatchContainer newContainer, int? newLayer = null) {
            if (Released)
                throw new ObjectDisposedException("batch");

            Container = newContainer;

            if (newLayer.HasValue)
                Layer = newLayer.Value;
        }

        public void CaptureStack (int extraFramesToSkip) {
            if (CaptureStackTraces)
                StackTrace = new StackTrace(2 + extraFramesToSkip, true);
        }

        // This is where you should do any computation necessary to prepare a batch for rendering.
        // Examples: State sorting, computing vertices.
        public abstract void Prepare ();

        // This is where you send commands to the video card to render your batch.
        public abstract void Issue (DeviceManager manager);

        protected virtual void OnReleaseResources () {
            Released = true;

            if (Pool != null) {
                Pool.Release(this);
                Pool = null;
            }

            StackTrace = null;
            Container = null;
            Material = null;
            Index = -1;
        }

        public void ReleaseResources () {
            if (Released)
                throw new ObjectDisposedException("Batch");

            if (!ReleaseAfterDraw)
                return;

            OnReleaseResources();
        }

        /// <summary>
        /// Notifies the render manager that it should release the batch once the current frame is done drawing.
        /// You may opt to avoid calling this method in order to reuse a batch across multiple frames.
        /// </summary>
        public virtual void Dispose () {
            ReleaseAfterDraw = true;
        }

        public bool IsReusable {
            get {
                return !ReleaseAfterDraw;
            }
        }

        public bool IsReleased {
            get {
                return Released;
            }
        }
    }

    public abstract class ListBatch<T> : Batch {
        protected List<T> _DrawCalls;

        private static ListPool<T> _ListPool = new ListPool<T>(
            2048, 128, 1024
        );

        new protected void Initialize (IBatchContainer container, int layer, Material material) {
            base.Initialize(container, layer, material);

            _DrawCalls = _ListPool.Allocate();
        }

        protected virtual void BeforeAdd (ref T item) {
        }

        protected void Add (ref T item) {
            BeforeAdd(ref item);
            _DrawCalls.Add(item);
        }

        protected override void OnReleaseResources() {
            _ListPool.Release(ref _DrawCalls);

            base.OnReleaseResources();
        }
    }

    public class ClearBatch : Batch {
        public Color? ClearColor;
        public float? ClearZ;
        public int? ClearStencil;

        public void Initialize (IBatchContainer container, int layer, Material material, Color? clearColor, float? clearZ, int? clearStencil) {
            base.Initialize(container, layer, material);
            ClearColor = clearColor;
            ClearZ = clearZ;
            ClearStencil = clearStencil;

            if (!(clearColor.HasValue || clearZ.HasValue || clearStencil.HasValue))
                throw new ArgumentException("You must specify at least one of clearColor, clearZ and clearStencil.");
        }

        public override void Prepare () {
        }

        public override void Issue (DeviceManager manager) {
            using (manager.ApplyMaterial(Material)) {
                var clearOptions = default(ClearOptions);

                if (ClearColor.HasValue)
                    clearOptions |= ClearOptions.Target;
                if (ClearZ.HasValue)
                    clearOptions |= ClearOptions.DepthBuffer;
                if (ClearStencil.HasValue)
                    clearOptions |= ClearOptions.Stencil;

                manager.Device.Clear(
                    clearOptions,
                    ClearColor.GetValueOrDefault(Color.Black), ClearZ.GetValueOrDefault(0), ClearStencil.GetValueOrDefault(0)
                );
            }
        }

        public static void AddNew (IBatchContainer container, int layer, Material material, Color? clearColor = null, float? clearZ = null, int? clearStencil = null) {
            if (container == null)
                throw new ArgumentNullException("container");

            var result = container.RenderManager.AllocateBatch<ClearBatch>();
            result.Initialize(container, layer, material, clearColor, clearZ, clearStencil);
            result.CaptureStack(0);
            result.Dispose();
        }
    }

    public class SetRenderTargetBatch : Batch {
        public RenderTarget2D RenderTarget;

        public void Initialize (IBatchContainer container, int layer, RenderTarget2D renderTarget) {
            base.Initialize(container, layer, null);
            RenderTarget = renderTarget;
        }

        public override void Prepare () {
        }

        public override void Issue (DeviceManager manager) {
            manager.Device.SetRenderTarget(RenderTarget);
        }

        [Obsolete("Use BatchGroup.ForRenderTarget instead.")]
        public static void AddNew (IBatchContainer container, int layer, RenderTarget2D renderTarget) {
            if (container == null)
                throw new ArgumentNullException("frame");

            var result = container.RenderManager.AllocateBatch<SetRenderTargetBatch>();
            result.Initialize(container, layer, renderTarget);
            result.CaptureStack(0);
            result.Dispose();
        }
    }

    public class BatchGroup : ListBatch<Batch>, IBatchContainer {
        Action<DeviceManager, object> _Before, _After;
        private object _UserData;

        protected override void BeforeAdd (ref Batch batch) {
#if DEBUG
            if (_DrawCalls.Contains(batch))
                throw new InvalidOperationException("Group already contains this batch.");
#endif
        }

        public override void Prepare () {
            BatchCombiner.CombineBatches(_DrawCalls);
            _DrawCalls.Sort(Frame.BatchComparer);

            foreach (var batch in _DrawCalls)
                if (batch != null)
                    batch.Prepare();
        }

        public override void Issue (DeviceManager manager) {
            if (_Before != null)
                _Before(manager, _UserData);

            try {
                foreach (var batch in _DrawCalls)
                    if (batch != null)
                        batch.IssueAndWrapExceptions(manager);
            } finally {
                if (_After != null)
                    _After(manager, _UserData);
            }
        }

        private void SetRenderTargetCallback (DeviceManager dm, object userData) {
            dm.PushRenderTarget((RenderTarget2D)userData);
        }

        private void RestoreRenderTargetCallback (DeviceManager dm, object userData) {
            dm.PopRenderTarget();
        }

        public static BatchGroup ForRenderTarget (IBatchContainer container, int layer, RenderTarget2D renderTarget) {
            if (container == null)
                throw new ArgumentNullException("container");

            var result = container.RenderManager.AllocateBatch<BatchGroup>();
            result.Initialize(container, layer, result.SetRenderTargetCallback, result.RestoreRenderTargetCallback, renderTarget);
            result.CaptureStack(0);

            return result;
        }

        public static BatchGroup New (IBatchContainer container, int layer, Action<DeviceManager, object> before = null, Action<DeviceManager, object> after = null, object userData = null) {
            if (container == null)
                throw new ArgumentNullException("container");

            var result = container.RenderManager.AllocateBatch<BatchGroup>();
            result.Initialize(container, layer, before, after, userData);
            result.CaptureStack(0);

            return result;
        }

        public void Initialize (IBatchContainer container, int layer, Action<DeviceManager, object> before, Action<DeviceManager, object> after, object userData) {
            base.Initialize(container, layer, null);

            RenderManager = container.RenderManager;
            _Before = before;
            _After = after;
            _UserData = userData;
        }

        public RenderManager RenderManager {
            get;
            private set;
        }

        public bool IsDisposed {
            get {
                return _DrawCalls == null;
            }
        }

        public void Add (Batch batch) {
            base.Add(ref batch);
        }

        protected override void OnReleaseResources () {
            foreach (var batch in _DrawCalls)
                if (batch != null)
                    batch.ReleaseResources();

            _DrawCalls.Clear();

            base.OnReleaseResources();
        }
    }

    public class BatchIssueFailedException : Exception {
        public readonly Batch Batch;

        public BatchIssueFailedException (Batch batch, Exception innerException) 
            : base (FormatMessage(batch, innerException), innerException) {

            Batch = batch;
        }

        public static string FormatMessage (Batch batch, Exception innerException) {
            if (batch.StackTrace != null)
                return String.Format(
                    "Failed to issue batch of type '{0}'. Stack trace at time of batch creation:{1}{2}", 
                    batch.GetType().Name,
                    Environment.NewLine, 
                    batch.StackTrace
                );
            else
                return String.Format(
                    "Failed to issue batch of type '{0}'. Set Batch.CaptureStackTraces to true to enable stack trace captures.",
                    batch.GetType().Name
                );
        }
    }

    public static class BatchExtensions {
        public static void IssueAndWrapExceptions (this Batch batch, DeviceManager manager) {
            try {
                batch.Issue(manager);
            } catch (Exception exc) {
                throw new BatchIssueFailedException(batch, exc);
            }
        }
    }
}
