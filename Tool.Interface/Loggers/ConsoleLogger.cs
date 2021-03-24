using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using Tool.Interface;

namespace Tool.Loggers {
	internal sealed class ConsoleLogger : ILogger {
		private readonly Context _context;
		private bool _isFreed;

		public static readonly ConsoleLogger Instance = new ConsoleLogger();

		public bool IsLocked => _context.IsLocked;

		private ConsoleLogger() : this(new Context(new LoggerCore())) {
			_context.Creator = this;
			_context.Owner = this;
		}

		private ConsoleLogger(Context context) {
			_context = context;
		}

		public void Log(string value, LogLevel level, ConsoleColor? color = null) {
			CheckFreed();
			if (_context.IsLocked) {
			relock:
				if (_context.Owner != this) {
					lock (_context.LockObj) {
						if (_context.Owner != this) {
							Monitor.Wait(_context.LockObj);
							goto relock;
						}
					}
				}
			}

			_context.Core.LogCore(value, level, color);
		}

		public ILogger EnterLock() {
			CheckFreed();
			if (this != _context.Creator)
				throw new InvalidOperationException("Nested lock is not supported");

			relock:
			lock (_context.LockObj) {
				if (_context.IsLocked) {
					Monitor.Wait(_context.LockObj);
					goto relock;
				}

				_context.Owner = new ConsoleLogger(_context);
				_context.IsLocked = true;
				return _context.Owner;
			}
		}

		public ILogger ExitLock() {
			CheckFreed();
			if (_context.Creator == this)
				throw new InvalidOperationException("No lock can be exited");

			_isFreed = true;
			_context.Owner = _context.Creator;
			_context.IsLocked = false;
			lock (_context.LockObj)
				Monitor.PulseAll(_context.LockObj);
			return _context.Owner;
		}

		private void CheckFreed() {
			if (_isFreed)
				throw new InvalidOperationException("Current logger is freed");
		}

		private sealed class Context {
			public readonly object LockObj = new object();
			public readonly LoggerCore Core;
			public ConsoleLogger Creator;
			public volatile ConsoleLogger Owner;
			public volatile bool IsLocked;

			public Context(LoggerCore core) {
				Core = core;
			}
		}

		#region forwards
		public LogLevel Level {
			get {
				CheckFreed();
				return _context.Core.Level;
			}
			set {
				CheckFreed();
				_context.Core.Level = value;
			}
		}

		public bool IsAsync {
			get {
				CheckFreed();
				return _context.Core.IsAsync;
			}
			set {
				CheckFreed();
				_context.Core.IsAsync = value;
			}
		}

		public bool IsIdle {
			get {
				CheckFreed();
				return LoggerCore.IsIdle;
			}
		}

		public int QueueCount {
			get {
				CheckFreed();
				return LoggerCore.QueueCount;
			}
		}

		public void Info() {
			_context.Core.Info(this);
		}

		public void Info(string value) {
			_context.Core.Info(value, this);
		}

		public void Warning(string value) {
			_context.Core.Warning(value, this);
		}

		public void Error(string value) {
			_context.Core.Error(value, this);
		}

		public void Verbose1(string value) {
			_context.Core.Verbose1(value, this);
		}

		public void Verbose2(string value) {
			_context.Core.Verbose2(value, this);
		}

		public void Verbose3(string value) {
			_context.Core.Verbose3(value, this);
		}

		public void Exception(Exception value) {
			_context.Core.Exception(value, this);
		}

		public void Flush() {
			CheckFreed();
			LoggerCore.Flush();
		}
		#endregion

		#region core
		private sealed class LoggerCore {
			private static bool _isIdle = true;
			private static readonly object _logLock = new object();
			private static readonly ManualResetEvent _asyncIdleEvent = new ManualResetEvent(true);
			private static readonly Queue<LogItem> _asyncQueue = new Queue<LogItem>();
			private static readonly object _asyncLock = new object();
			private static readonly Thread _asyncWorker = new Thread(AsyncLoop) {
				Name = $"{nameof(ConsoleLogger)}.{nameof(AsyncLoop)}",
				IsBackground = true
			};

			private LogLevel _level;
			private volatile bool _isAsync;

			public LogLevel Level {
				get => _level;
				set => _level = value;
			}

			public bool IsAsync {
				get => _isAsync;
				set {
					if (value == _isAsync)
						return;

					lock (_logLock) {
						_isAsync = value;
						if (!value)
							Flush();
					}
				}
			}

			public static bool IsIdle => _isIdle;

			public static int QueueCount => _asyncQueue.Count;

			public LoggerCore() {
				_level = LogLevel.Info;
				_isAsync = true;
			}

			public void Info(ILogger logger) {
				Log(string.Empty, LogLevel.Info, null, logger);
			}

			public void Info(string value, ILogger logger) {
				Log(value, LogLevel.Info, ConsoleColor.Gray, logger);
			}

			public void Warning(string value, ILogger logger) {
				Log(value, LogLevel.Warning, ConsoleColor.Yellow, logger);
			}

			public void Error(string value, ILogger logger) {
				Log(value, LogLevel.Error, ConsoleColor.Red, logger);
			}

			public void Verbose1(string value, ILogger logger) {
				Log(value, LogLevel.Verbose1, ConsoleColor.DarkGray, logger);
			}

			public void Verbose2(string value, ILogger logger) {
				Log(value, LogLevel.Verbose2, ConsoleColor.DarkGray, logger);
			}

			public void Verbose3(string value, ILogger logger) {
				Log(value, LogLevel.Verbose3, ConsoleColor.DarkGray, logger);
			}

			public void Exception(Exception value, ILogger logger) {
				if (value is null)
					throw new ArgumentNullException(nameof(value));

				Error(ExceptionToString(value), logger);
			}

			public void Log(string value, LogLevel level, ConsoleColor? color, ILogger logger) {
				logger.Log(value, level, color);
			}

			public void LogCore(string value, LogLevel level, ConsoleColor? color) {
				if (level > Level)
					return;

				lock (_logLock) {
					if (_isAsync) {
						lock (_asyncLock) {
							_asyncQueue.Enqueue(new LogItem(value, level, color));
							if ((_asyncWorker.ThreadState & ThreadState.Unstarted) != 0)
								_asyncWorker.Start();
							Monitor.Pulse(_asyncLock);
						}
					}
					else {
						WriteConsole(value, color);
					}
				}
			}

			public static void Flush() {
				_asyncIdleEvent.WaitOne();
			}

			private static string ExceptionToString(Exception exception) {
				if (exception is null)
					throw new ArgumentNullException(nameof(exception));

				var sb = new StringBuilder();
				DumpException(exception, sb);
				return sb.ToString();
			}

			private static void DumpException(Exception exception, StringBuilder sb) {
				sb.AppendLine($"Type: {Environment.NewLine}{exception.GetType().FullName}");
				sb.AppendLine($"Message: {Environment.NewLine}{exception.Message}");
				sb.AppendLine($"Source: {Environment.NewLine}{exception.Source}");
				sb.AppendLine($"StackTrace: {Environment.NewLine}{exception.StackTrace}");
				sb.AppendLine($"TargetSite: {Environment.NewLine}{exception.TargetSite}");
				sb.AppendLine("----------------------------------------");
				if (!(exception.InnerException is null))
					DumpException(exception.InnerException, sb);
				if (exception is ReflectionTypeLoadException reflectionTypeLoadException) {
					foreach (var loaderException in reflectionTypeLoadException.LoaderExceptions)
						DumpException(loaderException, sb);
				}
			}

			private static void AsyncLoop() {
				var sb = new StringBuilder();
				while (true) {
					lock (_asyncLock) {
						if (_asyncQueue.Count == 0) {
							_isIdle = true;
							_asyncIdleEvent.Set();
							Monitor.Wait(_asyncLock);
						}
						_isIdle = false;
						_asyncIdleEvent.Reset();
					}
					// 等待输出被触发

					var currents = default(Queue<LogItem>);
					lock (_asyncLock) {
						currents = new Queue<LogItem>(_asyncQueue);
						_asyncQueue.Clear();
					}
					// 获取全部要输出的内容

					do {
						var current = currents.Dequeue();
						var color = current.Color;
						sb.Length = 0;
						sb.Append(current.Value);
						while (true) {
							if (currents.Count == 0)
								break;

							var next = currents.Peek();
							if (next.Level != current.Level)
								break;

							if (!color.HasValue && next.Color.HasValue)
								color = next.Color;
							// 空行的颜色是null，获取第一个非null的颜色作为合并日志的颜色
							if (next.Color.HasValue && next.Color != color)
								break;
							// 如果下一行的颜色不是null并且与当前颜色不同，跳出优化

							sb.AppendLine();
							sb.Append(currents.Dequeue().Value);
						}
						// 合并日志等级与颜色相同的，减少重绘带来的性能损失
						WriteConsole(sb.ToString(), color);
					} while (currents.Count > 0);
				}
			}

			private static void WriteConsole(string value, ConsoleColor? color) {
				ConsoleColor oldColor = default;
				if (color.HasValue) {
					oldColor = Console.ForegroundColor;
					Console.ForegroundColor = color.Value;
				}
				Console.WriteLine(value);
				if (color.HasValue)
					Console.ForegroundColor = oldColor;
			}

			private struct LogItem {
				public string Value;
				public LogLevel Level;
				public ConsoleColor? Color;

				public LogItem(string value, LogLevel level, ConsoleColor? color) {
					Value = value;
					Level = level;
					Color = color;
				}
			}
		}
		#endregion
	}
}
