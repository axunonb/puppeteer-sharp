﻿#pragma warning disable CS0067 // Temporal, do not merge with this
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using PuppeteerSharp.Helpers.Json;
using PuppeteerSharp.Messaging;

namespace PuppeteerSharp
{
    internal class ChromeTargetManager : ITargetManager
    {
        private readonly List<string> _ignoredTargets = new();
        private readonly Connection _connection;
        private readonly Func<TargetInfo, CDPSession, Target> _targetFactoryFunc;
        private readonly Func<TargetInfo, bool> _targetFilterFunc;
        private readonly ILogger<ChromeTargetManager> _logger;
        private readonly ConcurrentDictionary<string, Target> _attachedTargetsByTargetId = new();
        private readonly ConcurrentDictionary<string, Target> _attachedTargetsBySessionId = new();
        private readonly ConcurrentDictionary<string, TargetInfo> _discoveredTargetsByTargetId = new();
        private readonly ConcurrentDictionary<ICDPConnection, List<Func<Target, Target, Task>>> _targetInterceptors = new();
        private readonly List<string> _targetsIdsForInit = new();
        private readonly TaskCompletionSource<bool> _initializeCompletionSource = new();

        public ChromeTargetManager(
            Connection connection,
            Func<TargetInfo, CDPSession, Target> targetFactoryFunc,
            Func<TargetInfo, bool> targetFilterFunc)
        {
            _connection = connection;
            _targetFilterFunc = targetFilterFunc;
            _targetFactoryFunc = targetFactoryFunc;
            _connection.MessageReceived += OnMessageReceived;
            _connection.SessionDetached += Connection_SessionDetached;
            _logger = _connection.LoggerFactory.CreateLogger<ChromeTargetManager>();

            _ = _connection.SendAsync("Target.setDiscoverTargets", new TargetSetDiscoverTargetsRequest
            {
                Discover = true,
                Filter = new[]
                {
                    new TargetSetDiscoverTargetsRequest.DiscoverFilter()
                    {
                        Type = "tab",
                        Exlude = true,
                    },
                },
            }).ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.LogError(t.Exception, "Target.setDiscoverTargets failed");
                    }
                    else
                    {
                        StoreExistingTargetsForInit();
                    }
                },
                TaskScheduler.Default);
        }

        public event EventHandler<TargetChangedArgs> TargetAvailable;

        public event EventHandler<TargetChangedArgs> TargetGone;

        public event EventHandler<TargetChangedArgs> TargetChanged;

        public event EventHandler<TargetChangedArgs> TargetDiscovered;

        internal IDictionary<string, Target> TargetsMap { get; }

        public ConcurrentDictionary<string, Target> GetAvailableTargets() => _attachedTargetsByTargetId;

        public async Task InitializeAsync()
        {
            await _connection.SendAsync("Target.setAutoAttach", new TargetSetAutoAttachRequest()
            {
                WaitForDebuggerOnStart = true,
                Flatten = true,
                AutoAttach = true,
            }).ConfigureAwait(false);

            FinishInitializationIfReady();
            await _initializeCompletionSource.Task.ConfigureAwait(false);
        }

        private void FinishInitializationIfReady(string targetId = null) => throw new NotImplementedException();

        private void StoreExistingTargetsForInit()
        {
            foreach (var kv in _discoveredTargetsByTargetId)
            {
                if ((_targetFilterFunc == null || _targetFilterFunc(kv.Value)) &&
                    kv.Value.Type != TargetType.Browser)
                {
                    _targetsIdsForInit.Add(kv.Key);
                }
            }
        }

        private async void OnMessageReceived(object sender, MessageEventArgs e)
        {
            try
            {
                switch (e.MessageID)
                {
                    case "Target.attachedToTarget":
                        await OnAttachedToTarget(sender, e.MessageData.ToObject<TargetAttachedToTargetResponse>(true)).ConfigureAwait(false);
                        return;
                    case "Target.detachedFromTarget":
                        OnDetachedToTarget(sender, e.MessageData.ToObject<TargetDetachedFromTargetResponse>(true));
                        return;
                    case "Target.targetCreated":
                        OnTargetCreated(e.MessageData.ToObject<TargetCreatedResponse>(true));
                        return;

                    case "Target.targetDestroyed":
                        OnTargetDestroyed(e.MessageData.ToObject<TargetDestroyedResponse>(true));
                        return;

                    case "Target.targetInfoChanged":
                        OnTargetInfoChanged(e.MessageData.ToObject<TargetCreatedResponse>(true));
                        return;
                }
            }
            catch (Exception ex)
            {
                var message = $"Browser failed to process {e.MessageID}. {ex.Message}. {ex.StackTrace}";
                _logger.LogError(ex, message);
                _connection.Close(message);
            }
        }

        private void Connection_SessionDetached(object sender, SessionEventArgs e)
        {
            e.Session.MessageReceived -= OnMessageReceived;
            _targetInterceptors.TryRemove(e.Session, out var _);
        }

        private void OnTargetCreated(TargetCreatedResponse e)
        {
            _discoveredTargetsByTargetId[e.TargetInfo.TargetId] = e.TargetInfo;

            TargetDiscovered?.Invoke(this, new TargetChangedArgs { TargetInfo = e.TargetInfo });

            if (e.TargetInfo.Type == TargetType.Browser && e.TargetInfo.Attached)
            {
                if (_attachedTargetsByTargetId.ContainsKey(e.TargetInfo.TargetId))
                {
                    return;
                }

                var target = _targetFactoryFunc(e.TargetInfo, null);
                _attachedTargetsByTargetId[e.TargetInfo.TargetId] = target;
            }
        }

        private void OnTargetDestroyed(TargetDestroyedResponse e)
        {
            _discoveredTargetsByTargetId.TryRemove(e.TargetId, out var targetInfo);
            FinishInitializationIfReady(e.TargetId);

            if (targetInfo?.Type == TargetType.ServiceWorker && _attachedTargetsByTargetId.TryRemove(e.TargetId, out var target))
            {
                TargetGone?.Invoke(this, new TargetChangedArgs { Target = target, TargetInfo = targetInfo });
            }
        }

        private void OnTargetInfoChanged(TargetCreatedResponse e)
        {
            _discoveredTargetsByTargetId[e.TargetInfo.TargetId] = e.TargetInfo;

            if (_ignoredTargets.Contains(e.TargetInfo.TargetId) ||
                !_attachedTargetsByTargetId.ContainsKey(e.TargetInfo.TargetId) ||
                !e.TargetInfo.Attached)
            {
                return;
            }

            _attachedTargetsByTargetId.TryGetValue(e.TargetInfo.TargetId, out var target);
            TargetChanged?.Invoke(this, new TargetChangedArgs { Target = target, TargetInfo = e.TargetInfo });
        }

        private async Task OnAttachedToTarget(object sender, TargetAttachedToTargetResponse e)
        {
            var parent = sender as ICDPConnection;
            var parentSession = parent as CDPSession;
            var targetInfo = e.TargetInfo;
            var session = _connection.GetSession(e.SessionId);

            if (session == null)
            {
                throw new PuppeteerException($"Session {e.SessionId} was not created.");
            }

            Func<Task> silentDetach = async () =>
            {
                await session.SendAsync("Runtime.runIfWaitingForDebugger")
                    .ContinueWith(
                        t => _logger.LogError(t.Exception, "Runtime.runIfWaitingForDebugger failed."),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default)
                    .ConfigureAwait(false);

                await parent.SendAsync(
                    "Target.detachFromTarget",
                    new TargetDetachFromTargetRequest
                    {
                        SessionId = session.Id,
                    })
                    .ContinueWith(
                        t => _logger.LogError(t.Exception, "Runtime.runIfWaitingForDebugger failed."),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default)
                    .ConfigureAwait(false);
            };

            if (!_connection.IsAutoAttached(targetInfo.TargetId))
            {
                return;
            }

            if (targetInfo.Type == TargetType.ServiceWorker &&
                _connection.IsAutoAttached(targetInfo.TargetId))
            {
                FinishInitializationIfReady(targetInfo.TargetId);
                await silentDetach().ConfigureAwait(false);
                return;
            }

            var existingTarget = _attachedTargetsByTargetId.TryGetValue(targetInfo.TargetId, out var target);
            if (!existingTarget)
            {
                _targetFactoryFunc(targetInfo, session);
            }

            session.MessageReceived += OnMessageReceived;

            if (existingTarget)
            {
                _attachedTargetsBySessionId.TryAdd(session.Id, target);
            }
            else
            {
                _attachedTargetsByTargetId.TryAdd(targetInfo.TargetId, target);
                _attachedTargetsBySessionId.TryAdd(session.Id, target);
            }

            if (_targetInterceptors.TryGetValue(parent, out var interceptors))
            {
                foreach (var interceptor in interceptors)
                {
                    Target parentTarget = null;
                    if (parentSession != null && !_attachedTargetsBySessionId.TryGetValue(parentSession.Id, out parentTarget))
                    {
                        throw new PuppeteerException("Parent session not found in attached targets");
                    }

                    await interceptor(target, parentTarget).ConfigureAwait(false);
                }
            }

            _targetsIdsForInit.Remove(target.TargetId);

            if (!existingTarget)
            {
                TargetAvailable?.Invoke(this, new TargetChangedArgs { Target = target });
            }

            FinishInitializationIfReady();

            try
            {
                await Task.WhenAll(
                    session.SendAsync("Target.setAutoAttach", new TargetSetAutoAttachRequest
                    {
                        WaitForDebuggerOnStart = true,
                        Flatten = true,
                        AutoAttach = true,
                    }),
                    session.SendAsync("Runtime.runIfWaitingForDebugger")).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to call setautoAttach and runIfWaitingForDebugger", ex);
            }
        }

        private void OnDetachedToTarget(object sender, TargetDetachedFromTargetResponse e) => throw new NotImplementedException();

        public ConcurrentDictionary<string, Target> GetAllTargets() => throw new NotImplementedException();

        Task ITargetManager.InitializeAsync() => throw new NotImplementedException();

        public void AddTargetInterceptor(CDPSession session, Func<Target, Target, Task> interceptor)
        {
            lock (_targetInterceptors)
            {
                _targetInterceptors.TryGetValue(session, out var interceptors);

                if (interceptor == null)
                {
                    interceptors = new List<Func<Target, Target, Task>>();
                    _targetInterceptors.TryAdd(session, interceptors);
                }

                interceptors.Add(interceptor);
            }
        }

        public void RemoveTargetInterceptor(CDPSession session, Func<Target, Target, Task> interceptor)
        {
            _targetInterceptors.TryGetValue(session, out var interceptors);

            if (interceptor != null)
            {
                interceptors.Remove(interceptor);
            }
        }
    }
}
#pragma warning restore CS0067