﻿using System;
using FFImageLoading.Helpers;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
using FFImageLoading.Cache;
using FFImageLoading.Concurrency;

namespace FFImageLoading.Work
{
	public interface IWorkScheduler
	{
		/// <summary>
		/// Cancels any pending work for the task.
		/// </summary>
		/// <param name="task">Image loading task to cancel</param>
		void Cancel(IImageLoaderTask task);

		bool ExitTasksEarly { get; }

		void SetExitTasksEarly(bool exitTasksEarly);

		void SetPauseWork(bool pauseWork);

		void RemovePendingTask(IImageLoaderTask task);

		/// <summary>
		/// Schedules the image loading. If image is found in cache then it returns it, otherwise it loads it.
		/// </summary>
		/// <param name="key">Key for cache lookup.</param>
		/// <param name="task">Image loading task.</param>
		void LoadImage(IImageLoaderTask task);
	}

    public class WorkScheduler : IWorkScheduler
    {
        protected class PendingTask
        {
            public int Position { get; set; }

            public IImageLoaderTask ImageLoadingTask { get; set; }

            public Task FrameworkWrappingTask { get; set; }
        }

        private readonly IMiniLogger _logger;
        private readonly int _defaultParallelTasks;
        private readonly object _pendingTasksLock;
        private readonly List<PendingTask> _pendingTasks;

        private readonly ConcurrentDictionary<string, PendingTask> _currentlyRunning; // Here we use the dictionary as a concurrent set
        private Task _dispatch;

        private bool _exitTasksEarly; // volatile?
        private bool _pauseWork; // volatile?
        private int _currentPosition; // useful?

        public WorkScheduler(IMiniLogger logger)
        {
            _logger = logger;

            _pendingTasksLock = new object();
            _pendingTasks = new List<PendingTask>();

            _currentlyRunning = new ConcurrentDictionary<string, PendingTask>();
            _dispatch = Task.FromResult<byte>(1);

            int _processorCount = Environment.ProcessorCount;
            if (_processorCount <= 2)
                _defaultParallelTasks = 1;
            else
                _defaultParallelTasks = (int)Math.Truncate((double)_processorCount / 2d) + 1;
        }

        public virtual int MaxParallelTasks
        {
            get
            {
                return _defaultParallelTasks;
            }
        }

        /// <summary>
        /// Cancels any pending work for the task.
        /// </summary>
        /// <param name="task">Image loading task to cancel</param>
        /// <returns><c>true</c> if this instance cancel task; otherwise, <c>false</c>.</returns>
        public void Cancel(IImageLoaderTask task)
        {
            try
            {
                if (task != null && !task.IsCancelled && !task.Completed)
                {
                    task.Cancel();
                }
            }
            catch (Exception e)
            {
                _logger.Error("Exception occurent trying to cancel the task", e);
            }
            finally
            {
                if (task != null && task.IsCancelled)
                    task.Parameters.Dispose(); // this will ensure we don't keep a reference due to callbacks
            }
        }

        public bool ExitTasksEarly
        {
            get
            {
                return _exitTasksEarly;
            }
        }

        public void SetExitTasksEarly(bool exitTasksEarly)
        {
            _exitTasksEarly = exitTasksEarly;
            SetPauseWork(false);
        }

        public void SetPauseWork(bool pauseWork)
        {
            if (_pauseWork == pauseWork)
                return;

            _pauseWork = pauseWork;

            if (pauseWork)
            {
                _logger.Debug("SetPauseWork paused.");

                lock (_pendingTasksLock)
                {
                    foreach (var task in _pendingTasks)
                        task.ImageLoadingTask.Cancel();

                    _pendingTasks.Clear();
                }
            }

            if (!pauseWork)
            {
                _logger.Debug("SetPauseWork released.");
            }
        }

        public void RemovePendingTask(IImageLoaderTask task)
        {
            lock (_pendingTasksLock)
            {
                _pendingTasks.RemoveAll(p => p.ImageLoadingTask == task);
            }
        }

        /// <summary>
        /// Schedules the image loading. If image is found in cache then it returns it, otherwise it loads it.
        /// </summary>
        /// <param name="task">Image loading task.</param>
        public async void LoadImage(IImageLoaderTask task)
        {
            if (task == null)
                return;

            if (task.Parameters.DelayInMs != null && task.Parameters.DelayInMs > 0)
            {
                await Task.Delay(task.Parameters.DelayInMs.Value).ConfigureAwait(false);
            }

            if (task.IsCancelled)
            {
                task.Parameters.Dispose(); // this will ensure we don't keep a reference due to callbacks
                return;
            }

            // If we have the image in memory then it's pointless to schedule the job: just display it straight away
            if (task.CanUseMemoryCache())
            {
                var cacheResult = await task.TryLoadingFromCacheAsync().ConfigureAwait(false);
                if (cacheResult == CacheResult.Found || cacheResult == CacheResult.ErrorOccured) // If image is loaded from cache there is nothing to do here anymore, if something weird happened with the cache... error callback has already been called, let's just leave
                    return; // stop processing if loaded from cache OR if loading from cached raised an exception
            }

            _dispatch = _dispatch.ContinueWith(async t =>
            {
                try
                {
                    await LoadImageAsync(task).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Error("An error occured while loading image", ex);
                }
            });
        }

        private async Task LoadImageAsync(IImageLoaderTask task)
        {
            if (task.IsCancelled)
            {
                task.Parameters.Dispose(); // this will ensure we don't keep a reference due to callbacks
                return;
            }

            if (!task.Parameters.Preload)
            {
                lock (_pendingTasksLock)
                {
                    foreach (var pendingTask in _pendingTasks)
                    {
                        if (pendingTask.ImageLoadingTask != null && pendingTask.ImageLoadingTask.UsesSameNativeControl(task))
                            pendingTask.ImageLoadingTask.CancelIfNeeded();
                    }
                }
            }

            bool loadedFromCache = await task.PrepareAndTryLoadingFromCacheAsync().ConfigureAwait(false);
            if (loadedFromCache)
            {
                if (task.Parameters.OnFinish != null)
                    task.Parameters.OnFinish(task);

                task.Dispose();
                return; // image successfully loaded from cache
            }

            if (task.IsCancelled || _pauseWork)
            {
                task.Parameters.Dispose(); // this will ensure we don't keep a reference due to callbacks
                return;
            }

            QueueAndGenerateImage(task);
            return;
        }

        private PendingTask GetPendingTaskIfValid(IImageLoaderTask task, bool rawKey)
        {
            string key = task.GetKey(raw: rawKey);
            lock (_pendingTasksLock)
            {
                return _pendingTasks.FirstOrDefault(t => t.ImageLoadingTask.GetKey(raw: rawKey) == key);
            }
        }

        private PendingTask FindSimilarPendingTask(IImageLoaderTask task)
        {
            // At first check if the exact same items exists in pending tasks (exact same means same transformations, same downsample, ...)
            // Since it will be exactly the same it can be retrieved from memory cache
            var alreadyRunningTaskForSameKey = GetPendingTaskIfValid(task, false);
            if (alreadyRunningTaskForSameKey == null)
            {
                // No exact same task found, check if a similar task exists (not necessarily the same transformations, downsample, ...)
                alreadyRunningTaskForSameKey = GetPendingTaskIfValid(task, true);
            }

            return alreadyRunningTaskForSameKey;
        }

        private void QueueAndGenerateImage(IImageLoaderTask task)
        {
            _logger.Debug(string.Format("Generating/retrieving image: {0}", task.GetKey()));

            int position = Interlocked.Increment(ref _currentPosition);
            var currentPendingTask = new PendingTask() { Position = position, ImageLoadingTask = task };

            PendingTask alreadyRunningTaskForSameKey = null;
            lock (_pendingTasksLock)
            {
                alreadyRunningTaskForSameKey = FindSimilarPendingTask(task);
                if (alreadyRunningTaskForSameKey == null)
                {
                    _pendingTasks.Add(currentPendingTask);
                }
                else
                {
                    alreadyRunningTaskForSameKey.Position = position;
                }
            }

			if (alreadyRunningTaskForSameKey == null || !currentPendingTask.ImageLoadingTask.CanUseMemoryCache())
			{
				Run(currentPendingTask);
			}
			else
			{
				WaitForSimilarTask(currentPendingTask, alreadyRunningTaskForSameKey);
			}
		}

		private async void WaitForSimilarTask(PendingTask currentPendingTask, PendingTask alreadyQueuedTaskForSameKey)
		{
            string queuedKey = alreadyQueuedTaskForSameKey.ImageLoadingTask.GetKey();

            Action forceQueue = () =>
			{
                lock (_pendingTasksLock)
                {
                    _pendingTasks.Add(currentPendingTask);
                }
				Run(currentPendingTask);
			};

            if (alreadyQueuedTaskForSameKey.FrameworkWrappingTask == null)
			{
                _logger.Debug(string.Format("No C# Task defined for key: {0}", queuedKey));
				forceQueue();
				return;
			}

			_logger.Debug(string.Format("Wait for similar request for key: {0}", queuedKey));
			// This will wait for the pending task or if it is already finished then it will just pass
			await alreadyQueuedTaskForSameKey.FrameworkWrappingTask.ConfigureAwait(false);

			// Now our image should be in the cache
			var cacheResult = await currentPendingTask.ImageLoadingTask.TryLoadingFromCacheAsync().ConfigureAwait(false);
			if (cacheResult != FFImageLoading.Cache.CacheResult.Found)
			{
				_logger.Debug(string.Format("Similar request finished but the image is not in the cache: {0}", queuedKey));
				forceQueue();
			}
			else
			{
				var task = currentPendingTask.ImageLoadingTask;
				if (task.Parameters.OnFinish != null)
					task.Parameters.OnFinish(task);

				task.Dispose();
			}
		}

		private async void Run(PendingTask pendingTask)
		{
			if (MaxParallelTasks <= 0)
			{
				pendingTask.FrameworkWrappingTask = pendingTask.ImageLoadingTask.RunAsync(); // FMT: threadpool will limit concurrent work
				await pendingTask.FrameworkWrappingTask.ConfigureAwait(false);
				return;
			}

			var tcs = new TaskCompletionSource<bool>();

			var successCallback = pendingTask.ImageLoadingTask.Parameters.OnSuccess;
			pendingTask.ImageLoadingTask.Parameters.Success((size, result) =>
			{
				tcs.TrySetResult(true);

				if (successCallback != null)
					successCallback(size, result);
			});

			var finishCallback = pendingTask.ImageLoadingTask.Parameters.OnFinish;
			pendingTask.ImageLoadingTask.Parameters.Finish(sw =>
			{
				tcs.TrySetResult(false);

				if (finishCallback != null)
					finishCallback(sw);
			});

			pendingTask.FrameworkWrappingTask = tcs.Task;
			await RunAsync().ConfigureAwait(false); // FMT: we limit concurrent work using MaxParallelTasks
		}

		private async Task RunAsync()
		{
            if (_currentlyRunning.Count >= MaxParallelTasks)
            {
                return;
            }

            Dictionary<string, PendingTask> currentLotOfPendingTasks = null;

            int numberOfTasks = MaxParallelTasks - _currentlyRunning.Count;
            if (numberOfTasks > 0)
            {
                currentLotOfPendingTasks = new Dictionary<string, PendingTask>();

                lock (_pendingTasksLock)
                {
                    foreach (var task in
                             _pendingTasks
                                .Where(t => !t.ImageLoadingTask.IsCancelled && !t.ImageLoadingTask.Completed)
                                .OrderByDescending(t => t.ImageLoadingTask.Parameters.Priority)
                                .ThenBy(t => t.Position))
                    {
                        // We don't want to load, at the same time, images that have same key or same raw key at the same time
                        // This way we prevent concurrent downloads and benefit from caches
                        string key = task.ImageLoadingTask.GetKey();
                        if (!_currentlyRunning.ContainsKey(key) && !currentLotOfPendingTasks.ContainsKey(key))
                        {
                            string rawKey = task.ImageLoadingTask.GetKey(raw: true);
                            if (!_currentlyRunning.ContainsKey(rawKey) && !currentLotOfPendingTasks.ContainsKey(rawKey))
                            {
                                currentLotOfPendingTasks.Add(key, task);

                                if (currentLotOfPendingTasks.Count == numberOfTasks)
                                    break;
                            }
                        }
                    }
                }
            }


            if (currentLotOfPendingTasks == null || currentLotOfPendingTasks.Count == 0)
            {
                return; // FMT: no need to do anything else
            }

            if (currentLotOfPendingTasks.Count == 1)
            {
                await QueueTaskAsync(currentLotOfPendingTasks.Values.First(), false).ConfigureAwait(false);
            }
            else
            {
                var tasks = currentLotOfPendingTasks.Select(p => QueueTaskAsync(p.Value, true));
                await Task.WhenAny(tasks).ConfigureAwait(false);
            }
        }

        private async Task QueueTaskAsync(PendingTask pendingTask, bool scheduleOnThreadPool)
        {
            if (_currentlyRunning.Count >= MaxParallelTasks)
                    return;

            string key = pendingTask.ImageLoadingTask.GetKey();
            if (!_currentlyRunning.TryAdd(key, pendingTask))
                return; // If we can't add it it most likely means that it's already in

                try
                {
                    if (scheduleOnThreadPool)
                    {
                        await Task.Run(pendingTask.ImageLoadingTask.RunAsync).ConfigureAwait(false);
                    }
                    else
                    {
                        await pendingTask.ImageLoadingTask.RunAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    PendingTask doneTask;
                    if (!_currentlyRunning.TryRemove(key, out doneTask))
                    {
                        _logger.Error("WorkScheduler: Could not remove task from running tasks.");
                    }
                }

            await RunAsync().ConfigureAwait(false);
        }
	}
}

