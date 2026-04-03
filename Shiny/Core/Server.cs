using Serilog;
using Shiny.Bootstrap;
using Shiny.Feature;
using Shiny.Message;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Core {
    public sealed class Server {
        private readonly AutoResetEvent m_Signal = new(false);
        private readonly ContinuationQueue m_Continuations = new();
        private readonly MessageQueue m_Messages = new();
        private readonly List<Action<ServerContext>> m_StartActions = new();

        private readonly ServerSynchronizationContext m_SyncContext;
        private readonly ServerContext m_ServerContext;

        private volatile bool m_Running;
        private long m_CurrentFrame;
        private int m_LogicTheadId;

        public long TickInterval { get; }
        public int Fps { get; }
        public long CurrentFrame => Interlocked.Read(ref m_CurrentFrame);
        public ModuleManager Modules { get; }
        public ComponentManager Components { get; }
        public IMessageDispatcher Dispatcher { get; }
        public bool IsInLogicThread => Environment.CurrentManagedThreadId == m_LogicTheadId;

        public Server(IServerBootstrap bootstrap, int fps) {
            ArgumentNullException.ThrowIfNull(bootstrap);
            if (fps <= 0) {
                throw new ArgumentOutOfRangeException(nameof(fps));
            }
            Fps = fps;
            TickInterval = Stopwatch.Frequency / fps;
            Modules = new ModuleManager();
            Components = new ComponentManager();
            Dispatcher = new MessageDispatcher();

            m_SyncContext = new ServerSynchronizationContext(m_Continuations, Signal);
            m_ServerContext = new ServerContext(this);

            var builder = new ServerBuilder();
            bootstrap.Build(builder);
            builder.BuildInto(this);
        }

        public void Run() {
            if (m_LogicTheadId != 0) {
                throw new InvalidOperationException("Server.Run can only be called once.");
            }

            m_LogicTheadId = Environment.CurrentManagedThreadId;

            Init();
            Start();

            m_Running = true;

            var previousSyncContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(m_SyncContext);

            try {
                RunLoop();
            } finally {
                SynchronizationContext.SetSynchronizationContext(previousSyncContext);
                Stop();
            }
        }

        public void VerifyAccess() {
            if (!IsInLogicThread) {
                throw new InvalidOperationException("Access from non-logic thread.");
            }
        }

        public void RequestStop() {
            m_Running = false;
            Signal();
        }

        public void PostMessage(IServerMessage message) {
            ArgumentNullException.ThrowIfNull(message);
            m_Messages.Enqueue(message);
            Signal();
        }

        public TModule GetModule<TModule>() where TModule : class, IModule {
            return Modules.Get<TModule>();
        }

        public bool TryGetModule<TModule>(out TModule? module) where TModule : class, IModule {
            module = Modules.TryGet<TModule>();
            return module != null;
        }

        public TComponent GetComponent<TComponent>() where TComponent : class, IComponent {
            return Components.Get<TComponent>();
        }

        public bool TryGetComponent<TComponent>(out TComponent? component) where TComponent : class, IComponent {
            component = Components.TryGet<TComponent>();
            return component != null;
        }

        public void Post(Action action) {
            ArgumentNullException.ThrowIfNull(action);
            PostContinuation(static state =>((Action)state!).Invoke(), action);
        }

        public void RunAsync(Func<Task> asyncAction) {
            ArgumentNullException.ThrowIfNull(asyncAction);
            if (IsInLogicThread) {
                StartAsyncOnLogicThread(asyncAction);
            } else {
                Post(()=> StartAsyncOnLogicThread(asyncAction));
            }
        }

        public void RunAsync<TState>(TState state, Func<TState, Task> asyncAction) {
            ArgumentNullException.ThrowIfNull(asyncAction);
            if (IsInLogicThread) {
                StartAsyncOnLogicThread(state, asyncAction);
            } else {
                Post(() => StartAsyncOnLogicThread(state, asyncAction));
            }
        }

        internal void PostContinuation(SendOrPostCallback callback, object? state) {
            m_Continuations.Enqueue(callback, state);
            Signal();
        }

        internal void AddStartActions(IEnumerable<Action<ServerContext>> actions) {
            m_StartActions.AddRange(actions);
        }

        private void Init() {
            Modules.InitAll(m_ServerContext);
            Components.InitAll(m_ServerContext);
        }

        private void Start() {
            Modules.StartAll();
            Components.StartAll();

            foreach (var action in m_StartActions) {
                action(m_ServerContext);
            }
        }

        private void Stop() {
            Components.StopAllReverse();
            Modules.StopAllReverse();
        }

        private void RunLoop() {
            long nextTick = Stopwatch.GetTimestamp() + TickInterval;

            while (m_Running) {
                RunPreTickPhase();
                RunMessagePhase();

                int frameCatchUp = 0;
                const int maxCatchUpFrames = 4;

                long now = Stopwatch.GetTimestamp();
                while (now >= nextTick && frameCatchUp < maxCatchUpFrames) {
                    RunTickPhase(now);
                    nextTick += TickInterval;
                    frameCatchUp++;

                    now = Stopwatch.GetTimestamp();
                    if (now - nextTick > TickInterval * 5) {
                        nextTick = now + TickInterval;
                        break;
                    }
                }

                RunPostTickPhase();
                WaitForNextTick(nextTick);
            }
        }

        private void RunPreTickPhase() {
            DrainContinuations();
            Modules.RunPreTick();
            Components.RunPreTick();
        }

        private void RunMessagePhase() {
            DrainMessages();
        }

        private void RunTickPhase(long now) {
            long frame = Interlocked.Increment(ref m_CurrentFrame);
            var tick = new TickContext {
                FrameIndex = frame,
                NowTimestamp = now,
                TickInterval = TickInterval
            };
            Modules.RunTick(in tick);
            Components.RunTick(in tick);
        }

        private void RunPostTickPhase() {
            Components.RunPostTick();
            Modules.RunPostTick();
        }

        private void DrainContinuations() {
            const int maxContinuationsPerLoop = 1000;
            m_Continuations.Drain(maxContinuationsPerLoop);
        }

        private void DrainMessages() {
            const int maxMessagesPerLoop = 1000;
            m_Messages.Drain(msg => Dispatcher.Dispatch(msg), maxMessagesPerLoop);
        }

        private void WaitForNextTick(long nextTick) {
            long now = Stopwatch.GetTimestamp();
            long remainTicks = nextTick - now;
            int waitMs = (int)(remainTicks * 1000 / Stopwatch.Frequency);
            if (waitMs > 0) {
                m_Signal.WaitOne(waitMs);
            } else {
                Thread.Yield();
            }
        }

        private void Signal() {
            m_Signal.Set();
        }

        private void StartAsyncOnLogicThread(Func<Task> asyncAction) {
            VerifyAccess();

            Task task;
            try {
                task = asyncAction();
            } catch (Exception ex) {
                OnUnhandledException(ex);
                return;
            }

            if (task.IsCompleted) {
                ObserveCompletedTask(task);
                return;
            }

            task.ContinueWith(static (t, state) => {
                var server = (Server)state!;
                server.ObserveCompletedTask(t); // 注意这个continue可能在其他线程执行，不要在里面做可能引起竞态的操作，因为指定了 TaskContinuationOptions.ExecuteSynchronously
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void StartAsyncOnLogicThread<TState>(TState state, Func<TState, Task> asyncAction) {
            VerifyAccess();

            Task task;
            try {
                task = asyncAction(state);
            } catch (Exception ex) {
                OnUnhandledException(ex);
                return;
            }

            if (task.IsCompleted) {
                ObserveCompletedTask(task);
                return;
            }

            task.ContinueWith(static (t, s) => {
                var server = (Server)s!;
                server.ObserveCompletedTask(t); // 注意这个continue可能在其他线程执行，不要在里面做可能引起竞态的操作，因为指定了 TaskContinuationOptions.ExecuteSynchronously
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // 注意，这里只能观察任务的完成结果，不能触碰游戏逻辑，这个延续可能是在线程池线程上
        private void ObserveCompletedTask(Task task) {
            if (task.IsFaulted) {
                var ex = task.Exception?.GetBaseException() ?? new Exception("Async task faulted with unknown exception.");
                OnUnhandledException(ex);
                return;
            }
            if (task.IsCanceled) {
                Log.Information("Async task canceled.");
            } else {
                Log.Debug("Async Task Complete.");
            }
        }

        private void OnUnhandledException(Exception ex) {
            Log.Error("Unhandled exception: {Exception}", ex);

            // 后续可以替换为：
            // 1. metrics
            // 2. host crash policy
            // 3. fatal/non-fatal classification
            // 或者更正式一点，可以给 ServerHost 或外部注入 handler
        }
    }
}
