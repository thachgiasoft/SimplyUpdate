﻿using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SimplyUpdate.Updater
{
	public class UpdaterClient
	{
		private String RemoteRepository = String.Empty;
		private static String LocalPath = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
		private Func<UpdateAvailableInfo,Boolean> WhenUpdateAvailableAction = (e) => true;
		private IProgress<ProgressValue> UpdateProgress = null;
		private Action WhenUpdateCompletedAction = null;
		private Action<Exception> WhenErrorAction = null;
		private CancellationToken cancellationToken = CancellationToken.None;
		private Action<LogLevel, String> LogAction = null;

		public UpdaterClient Configure(String remoteRepository)
		{
			RemoteRepository = remoteRepository;
			return this;
		}

		public UpdaterClient WhenUpdateAvailable(Func<UpdateAvailableInfo,Boolean> action)
		{
			WhenUpdateAvailableAction = action;
			return this;
		}

		public UpdaterClient WithUpdateProgress(IProgress<ProgressValue> progress)
		{
			UpdateProgress = progress;
			return this;
		}

		public UpdaterClient WithCancellationToken(CancellationToken ct)
		{
			cancellationToken = ct;
			return this;
		}

		public UpdaterClient WithLogger(Action<LogLevel, String> action)
		{
			LogAction = action;
			return this;
		}

		public UpdaterClient WhenUpdateCompleted(Action action)
		{
			WhenUpdateCompletedAction = action;
			return this;
		}
		public UpdaterClient WhenError(Action<Exception> action)
		{
			WhenErrorAction = action;
			return this;
		}


		public void Run() => RunAsync().Wait();

		public async Task RunAsync()
		{
			PurgeOldFile();
			LogAction?.Invoke(LogLevel.Info, $"Search new version ...");
			var updateInfo = await CheckUpdate();
			LogAction?.Invoke(LogLevel.Debug, $"Remote version: {updateInfo.RemoteVersion} - Local version: {updateInfo.LocalVersion}");

			if (updateInfo.UpdateAvailable)
			{
				LogAction?.Invoke(LogLevel.Info, $"New version {updateInfo.RemoteVersion} found");
				if (WhenUpdateAvailableAction(updateInfo))
					try
					{
						String zipFile = Path.GetTempFileName();
						var uriZip = new Uri(RemoteRepository + "software.zip");

						if (await DownloadFile(uriZip, zipFile))
						{
							UnzipFile(zipFile);
							var uriXml = new Uri(RemoteRepository + "software.xml");
							using (WebClient clt = new WebClient())
								clt.DownloadFile(uriXml, Path.Combine(LocalPath, "software.xml"));

							File.Delete(zipFile);
							WhenUpdateCompletedAction?.Invoke();
						}
					}
					catch (Exception ex)
					{
						WhenErrorAction?.Invoke(ex);
						LogAction?.Invoke(LogLevel.Error, ex.Message);
					}
			}
		}

		private void PurgeOldFile()
		{
			LogAction?.Invoke(LogLevel.Debug, $"Purge old files ...");
			foreach (var f in Directory.GetFiles(LocalPath, "*.SimplyUpdateOldFile", SearchOption.AllDirectories))
				try
				{
					File.Delete(f);
				}
				catch(Exception ex)
				{
					LogAction?.Invoke(LogLevel.Error, ex.Message);
				}
		}

		private Task<UpdateAvailableInfo> CheckUpdate()
		=> Task.Factory.StartNew(() =>
			{
				Int32 remoteVersion = 0;

				try
				{
					String uriXml = RemoteRepository + "software.xml";
					XDocument doc = XDocument.Load(uriXml);
					remoteVersion = Convert.ToInt32((from n in doc.Descendants("Version")
																					 select n.Value).FirstOrDefault());
				}
				catch { }

				Int32 localVersion = GetLocalVersion();

				return new UpdateAvailableInfo(localVersion, remoteVersion);
			});

		public static Int32 GetLocalVersion()
		{
			Int32 retVal = 0;
			String localXml = System.IO.Path.Combine(LocalPath, "software.xml");
			try
			{

				if (System.IO.File.Exists(localXml))
				{
					XDocument doc = XDocument.Load(localXml);
					retVal = Convert.ToInt32((from n in doc.Descendants("Version")
																					select n.Value).FirstOrDefault());
				}
			}
			catch { }
			return retVal;
		}

		private void UnzipFile(string zipFile)
		{
			using (ZipArchive archive = ZipFile.OpenRead(zipFile))
				foreach (var entry in archive.Entries)
				{
					var localFile = Path.Combine(LocalPath, entry.FullName);
					if (File.Exists(localFile))
						RenameToOld(localFile);

					Directory.GetParent(localFile).Create();
					entry.ExtractToFile(localFile);
				}
		}

		private void RenameToOld(String sourceFileName)
		{
			var newFile = sourceFileName + ".SimplyUpdateOldFile";
			var i = 0;
			while (File.Exists(newFile))
				newFile = sourceFileName + $".{i}.SimplyUpdateOldFile";

			File.Move(sourceFileName, newFile);
		}

		private async Task<Boolean> DownloadFile(Uri address, String localPath)
		{
			try
			{
				using (WebClient clt = new WebClient())
				{
					clt.DownloadProgressChanged += (sender, e) =>
					{
						UpdateProgress?.Report(new ProgressValue( UpdateStepEnum.Download, e.ProgressPercentage));
						if (cancellationToken.IsCancellationRequested)
							clt.CancelAsync();
					};

					await clt.DownloadFileTaskAsync(address, localPath);

					return true;
				}
			}
			catch(Exception ex)
			{
				LogAction?.Invoke(LogLevel.Error, ex.Message);
				WhenErrorAction?.Invoke(ex);
				return false;
			}
		}
	}
}
