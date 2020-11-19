﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.IO;
using System.IO.Abstractions;
using System.IO.Enumeration;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using Polly;

namespace Nethermind.Blockchain.Data
{
    public class FileLocalDataSource<T> : ILocalDataSource<T>, IDisposable
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private T _data;
        private Timer _timer;
        private readonly int _interval;
        private DateTime _lastChange = DateTime.MinValue;
        private string _filePath;

        public FileLocalDataSource(string filePath, IJsonSerializer jsonSerializer, IFileSystem fileSystem, ILogManager logManager, int interval = 500)
        {
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logger = logManager?.GetClassLogger<FileLocalDataSource<T>>() ?? throw new ArgumentNullException(nameof(logManager));
            _interval = interval;
            SetupWatcher(filePath.GetApplicationResourcePath());
            LoadFile();
        }

        protected virtual T DefaultValue => default;

        public T Data => _data;
        
        public event EventHandler Changed;

        public void Dispose()
        {
            _timer?.Dispose();
        }

        private void SetupWatcher(string filePath)
        {
            _data = DefaultValue;
            IFileInfo fileInfo = null;
            try
            {
                fileInfo = _fileSystem.FileInfo.FromFileName(filePath);
            }
            catch (ArgumentException) { }
            catch (PathTooLongException) { }
            catch (NotSupportedException) { }

            if (fileInfo == null)
            {
                // file name is not valid
                if (_logger.IsError) _logger.Error($"Invalid file path to watch: {filePath}.");
            }
            else
            {
                // file name is valid
                _filePath = filePath;
                if (_logger.IsInfo) _logger.Info($"Watching file for changes: {filePath}.");
                _timer = new Timer(_interval) {Enabled = true};
                _timer.Elapsed += async (o, e) => await LoadFileAsync();
                // _timer = new Timer(async (state) => await LoadFileAsync(), null, _interval, _interval);
            }
        }

        private async Task LoadFileAsync()
        {
            if (_logger.IsTrace) _logger.Trace($"Trying to load local data from file: {_filePath}.");
            var start = DateTime.Now;
            try
            {
                await Policy.Handle<JsonSerializationException>()
                    .Or<IOException>()
                    .WaitAndRetryAsync(4, CalcRetryIntervals, (exception, i) => ReportRetry(start, exception))
                    .ExecuteAsync(() =>
                    {
                        LoadFileCore(start);
                        return Task.CompletedTask;
                    });
            }
            catch (JsonSerializationException e)
            {
                ReportJsonError(start, e);
            }
            catch (IOException e)
            {
                ReportIOError(start, e);
            }
        }

        private void LoadFile()
        {
            if (_logger.IsTrace) _logger.Trace($"Trying to load local data from file: {_filePath}.");
            var start = DateTime.Now;
            try
            {
                Policy.Handle<JsonSerializationException>()
                    .Or<IOException>()
                    .WaitAndRetry(2, CalcRetryIntervals, (exception, i) => ReportRetry(start, exception))
                    .Execute(() => LoadFileCore(start));
            }
            catch (JsonSerializationException e)
            {
                ReportJsonError(start, e);
            }
            catch (IOException e)
            {
                ReportIOError(start, e);
            }
        }

        private static TimeSpan CalcRetryIntervals(int i) => TimeSpan.FromMilliseconds(Math.Pow(10, i - 1));

        private void LoadFileCore(DateTime start)
        {
            DateTime? lastChange = null;
            
            if (_fileSystem.File.Exists(_filePath))
            {
                var lastWriteTime = _fileSystem.File.GetLastAccessTime(_filePath);
                if (lastWriteTime > _lastChange)
                {
                    using Stream file = _fileSystem.File.OpenRead(_filePath);
                    using StreamReader reader = new StreamReader(file);
                    string readToEnd = reader.ReadToEnd();
                    _data = _jsonSerializer.Deserialize<T>(readToEnd);
                    if (_logger.IsDebug) _logger.Debug($"Loaded and deserialized {typeof(T)} from {_filePath} on {start:hh:mm:ss.ffff}. {readToEnd}");
                    lastChange = lastWriteTime;
                }
            }
            else if (!Equals(_data, DefaultValue))
            {
                lastChange = DateTime.Now;
                _data = DefaultValue;
            }

            if (lastChange.HasValue)
            {
                _lastChange = lastChange.Value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        
        private void ReportJsonError(DateTime start, JsonSerializationException e)
        {
            if (_logger.IsError) _logger.Error($"Couldn't deserialize {typeof(T)} from {_filePath} on {start:hh:mm:ss.ffff}. Will not retry any more.", e);
        }

        private void ReportRetry(DateTime start, Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Couldn't load and deserialize {typeof(T)} from {_filePath} on {start:hh:mm:ss.ffff}. Retrying...", exception);
        }

        private void ReportIOError(DateTime start, IOException e)
        {
            if (_logger.IsError) _logger.Error($"Couldn't load {typeof(T)} from {_filePath} on {start:hh:mm:ss.ffff}. Will not retry any more.", e);
        }
    }
}
