using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibHac.Fs;

namespace yui
{
	class ProgressReporter
	{
		readonly int Max;
		readonly (int, int) Cursor;

		int Cur = 0;
		bool Complete = false;
		int MaxWrittenLen = 0;

		public ProgressReporter(int max)
		{
			Max = max;
			Cursor = (Console.CursorLeft, Console.CursorTop);
		}

		private void UpdateVal(string s)
		{
			var pos = (Console.CursorLeft, Console.CursorTop);
			(Console.CursorLeft, Console.CursorTop) = Cursor;
			Console.Write(s);
			(Console.CursorLeft, Console.CursorTop) = pos;
			MaxWrittenLen = Math.Max(s.Length, MaxWrittenLen);
		}

		public void Increment()
		{
			if (Complete)
				throw new Exception("Can't increment a completed process");

			Interlocked.Increment(ref Cur);
			// Don't need to update the terminal output every time
			if (Monitor.TryEnter(this))
				// Don't waste time updating the screen in the download thread
				Task.Run(() =>
				{
					UpdateVal($"{Cur} / {Max}");
					Monitor.Exit(this);
				});
		}

		// Should be called once all threaded operations are finished
		public void MarkComplete()
		{
			Complete = true;
			UpdateVal("Done." + new string(' ', MaxWrittenLen - 5));
		}
	}

	class SysUpdateHandler : IDisposable
	{
		public readonly HandlerArgs Args;
		readonly Yui yui;
		string OutPath;

		public SysUpdateHandler(string[] cmdArgs)
		{
			Args = new HandlerArgs(cmdArgs);

			if (Args.console_verbose)
				Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));

			if (Args.file_verbose != null)
				Trace.Listeners.Add(new TextWriterTraceListener(File.OpenWrite(Args.file_verbose)));

			yui = new Yui(new YuiConfig(
				ContentHandler: StoreContent,
				MetaHandler: StoreMeta,
				MaxParallelism: Args.max_jobs,
				Keyset: Args.keyset,
				Client: new CdnClientConfig {
					DeviceID = Args.device_id,
					Env = Args.env,
					FirmwareVersion = Args.firmware_version,
					Platform = Args.platform,
					Tencent = Args.tencent
				}.WithCertFromFile(Args.cert_loc).MakeClient()
			));

			OutPath = Args.out_path ?? "";
		}

		ProgressReporter? CurrentReporter;

		// Url is only for debugging purposes
		void StoreContent(Stream data, string ncaID, string? url)
		{
			string path = FileName(OutPath, ncaID, false);

			Trace.WriteLine($"[GotContent] [{data.Length}] {url ?? ""} => {path}");
			using FileStream fs = File.OpenWrite(path);
			data.CopyTo(fs);

			CurrentReporter?.Increment();
		}

		void StoreMeta(byte[] data, string titleID, string ncaID, string version, string? url)
		{
			string path = FileName(OutPath, ncaID, true);

			Trace.WriteLine($"[GotMeta] {titleID} v{version} [{data.Length}] {url ?? ""} => {path}");
			File.WriteAllBytes(path, data);

			CurrentReporter?.Increment();
		}

		static string FileName(string root, string ncaID, bool isMeta) =>
			Path.Combine(root, $"{ncaID}{(isMeta ? ".cnmt" : "")}.nca");

		private void SafeHandleDirectory(string path)
		{
			if (Directory.Exists(path))
			{
				if (!Args.ignore_warnings)
					Console.Write($"[WARNING] '{path}' already exists. \nPlease confirm that it should be overwritten [type 'y' to accept, anything else to abort]: ");
				if (Args.ignore_warnings || Console.ReadKey().KeyChar == 'y')
				{
					Console.WriteLine();
					Directory.Delete(path, true);
				}
				else
				{
					Console.WriteLine("Aborting...");
					Environment.Exit(-2);
				}
			}
			Directory.CreateDirectory(path);
		}

		private void BeginProgressReport(string message, int max)
		{
			Console.Write(message, max);
			if (!Args.console_verbose) // With verbose args progress reporting is useless
				CurrentReporter = new ProgressReporter(max);
			Console.WriteLine();
		}

		private void CompleteProgressReport() 
		{
			CurrentReporter?.MarkComplete();
			StopProgressReport();
		}

		private void StopProgressReport() 
		{
			CurrentReporter = null;
		}

		public void GetLatest()
		{
			Console.WriteLine("Getting sysupdate meta...");
			var update = yui.GetSysUpdateMetaNca();

			if (String.IsNullOrEmpty(OutPath))
				OutPath = $"sysupdate-[{update.Version.Value}]-{update.Version}-bn_{update.Version.BuildNumber}";
			SafeHandleDirectory(OutPath);

			// store it to disk as we're downloading the full update
			StoreMeta(update.Data, update.TitleID, update.NcaID, update.Version.Value.ToString(), null);

			Console.WriteLine("Parsing update entries...");
			var metaEntries = yui.GetContentEntries(new MemoryStorage(update.Data));

			if (Args.title_filter != null)
				metaEntries = metaEntries.Where(x => Args.title_filter.Contains(x.ID)).ToArray();

			BeginProgressReport("Downloading {} meta title(s)... ", metaEntries.Length);
			var contentEntries = yui.ProcessMeta(metaEntries);
			CompleteProgressReport();

			BeginProgressReport("Downloading {} content(s)... ", contentEntries.Length);
			yui.ProcessContent(contentEntries);
			CompleteProgressReport();

			Console.WriteLine($"All done !");
		}

		public void PrintLatestSysVersion()
		{
			var update_meta = yui.GetLatestUpdateInfo();
			var ver = Yui.ParseVersion(update_meta.system_update_metas[0].title_version);

			Console.WriteLine(
				$"Latest version on CDN: {ver} [{ver.Value}] buildnum={ver.BuildNumber}"
			);
		}

		public void Dispose()
		{
			StopProgressReport();
			yui.Dispose();
		}
	}
}