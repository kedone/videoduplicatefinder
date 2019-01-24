using DuplicateFinderEngine.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DuplicateFinderEngine {
	public class ScanEngine {
		public Settings Settings { get; } = new Settings();
		public event EventHandler<OwnScanProgress> Progress;
		public event EventHandler ScanDone;
		public event EventHandler ThumbnailsPopulated;
		public event EventHandler FilesEnumerated;
		public event EventHandler DatabaseCleaned;
		public event EventHandler DatabaseVideosExportedToCSV;
		public int ScanProgressMaxValue;
		public int ScanProgressValue;
		public TimeSpan TimeElapsed;
		public TimeSpan RemainingTime;
		private bool _isScanning;
		private readonly Stopwatch SearchSW = new Stopwatch();

		public Stopwatch ElapsedTimer = new Stopwatch();
		private PauseTokenSource m_pauseTokeSource;
		private CancellationTokenSource m_cancelationTokenSource;


		public HashSet<DuplicateItem> Duplicates { get; set; } = new HashSet<DuplicateItem>();
		private Dictionary<string, VideoFileEntry> DatabaseFileList = new Dictionary<string, VideoFileEntry>();
		private List<VideoFileEntry> ScanFileList = new List<VideoFileEntry>();
		private readonly List<float> positionList = new List<float>();


		public async void StartSearch() {
			Duplicates.Clear();
			positionList.Clear();
			ElapsedTimer.Reset();
			SearchSW.Reset();
			for (var i = 0; i < Settings.ThumbnailCount; i++) {
				positionList.Add(1.0F / (Settings.ThumbnailCount + 1));
			}
			_isScanning = true;
			m_pauseTokeSource = new PauseTokenSource();
			m_cancelationTokenSource = new CancellationTokenSource();

			//get files
			Logger.Instance.Info(Properties.Resources.BuildingFileList);
			await Task.Run(() => InternalBuildFileList());
			FilesEnumerated?.Invoke(this, null);
			//start scan
			Logger.Instance.Info(Properties.Resources.StartScan);
			if (!m_cancelationTokenSource.IsCancellationRequested)
				await Task.Run(() => InternalSearch(m_cancelationTokenSource.Token, m_pauseTokeSource));
			ScanDone?.Invoke(this, null);
			Logger.Instance.Info(Properties.Resources.ScanDone);
			_isScanning = false;
			ScanProgressValue = 0;
			DatabaseHelper.SaveDatabase(DatabaseFileList);
		}

		public async void PopulateDuplicateThumbnails() {
			await Task.Run(() => PopulateThumbnails(m_cancelationTokenSource.Token));
			ThumbnailsPopulated?.Invoke(this, null);
		}

		public async void CleanupDatabase() {
			await Task.Run(() => DatabaseHelper.CleanupDatabase(DatabaseFileList));
			DatabaseCleaned?.Invoke(this, null);
		}
		public async void ExportDatabaseVideosToCSV(bool onlyVideos, bool onlyFlagged) {
			if (DatabaseFileList.Count == 0) DatabaseFileList = DatabaseHelper.LoadDatabase();

			var db = DatabaseFileList.Values as IEnumerable<VideoFileEntry>;
			if (onlyVideos) db = db.Where(v => !v.IsImage);
			if (onlyFlagged) db = db.Where(v => v.Flags.Any(EntryFlags.ManuallyExcluded | EntryFlags.AllErrors));

			await Task.Run(() => DatabaseHelper.ExportDatabaseToCSV(db));
			DatabaseVideosExportedToCSV?.Invoke(this, null);
		}
		private void InternalBuildFileList() {
			ScanFileList.Clear();
			DatabaseFileList = DatabaseHelper.LoadDatabase();

			var st = Stopwatch.StartNew();
			foreach (var item in Settings.IncludeList) {
				foreach (var path in FileHelper.GetFilesRecursive(item, Settings.IgnoreReadOnlyFolders,
					Settings.IncludeSubDirectories, Settings.IncludeImages, Settings.BlackList.ToList())) {
					if (!DatabaseFileList.TryGetValue(path, out var vf)) {
						vf = new VideoFileEntry(path);
						DatabaseFileList.Add(path, vf);
					}
					ScanFileList.Add(vf);
				}
			}

			st.Stop();
			Logger.Instance.Info(string.Format(Properties.Resources.FinishedBuildingFileListIn, st.Elapsed));
		}

		public void Pause() {
			if (!_isScanning || m_pauseTokeSource.IsPaused) return;
			Logger.Instance.Info(Properties.Resources.ScanPaused);
			ElapsedTimer.Stop();
			SearchSW.Stop();
			m_pauseTokeSource.IsPaused = true;

		}

		public void Resume() {
			if (!_isScanning || m_pauseTokeSource.IsPaused != true) return;
			Logger.Instance.Info(Properties.Resources.ScanResumed);
			m_pauseTokeSource.IsPaused = false;
			ElapsedTimer.Start();
			SearchSW.Start();
			m_pauseTokeSource.IsPaused = false;
		}

		public void Stop() {
			if (m_pauseTokeSource.IsPaused)
				Resume();
			Logger.Instance.Info(Properties.Resources.ScanStopped);
			if (_isScanning)
				m_cancelationTokenSource.Cancel();
		}


		private int processedFiles;
		private DateTime startTime = DateTime.Now;
		private DateTime lastProgressUpdate = DateTime.MinValue;
		private TimeSpan progressUpdateItvl = TimeSpan.FromMilliseconds(300);
		private void InitProgress(int count) {
			startTime = DateTime.Now;
			ScanProgressMaxValue = count;
			processedFiles = 0;
			lastProgressUpdate = DateTime.MinValue;
		}

		void IncrementProgress(string path) {
			Interlocked.Increment(ref processedFiles);
			var pushUpdate = processedFiles == ScanProgressMaxValue ||
								lastProgressUpdate + progressUpdateItvl < DateTime.Now;
			if (!pushUpdate) return;
			lastProgressUpdate = DateTime.Now;
			var timeRemaining = TimeSpan.FromTicks(DateTime.Now.Subtract(startTime).Ticks *
									(ScanProgressMaxValue - (processedFiles + 1)) / (processedFiles + 1));
			Progress?.Invoke(this,
							new OwnScanProgress {
								CurrentPosition = processedFiles,
								CurrentFile = path,
								Elapsed = ElapsedTimer.Elapsed,
								Remaining = timeRemaining
							});
		}

		private void InternalSearch(CancellationToken cancelToken, PauseTokenSource pauseTokenSource) {
			ElapsedTimer.Start();
			SearchSW.Start();;
			var duplicateDict = new Dictionary<string, DuplicateItem>();

			try {
				var parallelOpts = new ParallelOptions {
					MaxDegreeOfParallelism = Environment.ProcessorCount,
					CancellationToken = cancelToken
				};

				var reScanList = ScanFileList
					.Where(vf => !vf.Flags.Any(EntryFlags.ManuallyExcluded | EntryFlags.AllErrors))
					.Where(vf => (vf.mediaInfo == null && !vf.IsImage) || vf.grayBytes == null)
					.ToList();

				InitProgress(reScanList.Count);
				Parallel.For(0, reScanList.Count, parallelOpts, i => {
					while (pauseTokenSource.IsPaused) Thread.Sleep(50);
					var entry = reScanList[i];

					if (entry.mediaInfo == null && !entry.IsImage) {
						var ffProbe = new FFProbeWrapper.FFProbeWrapper();
						var info = ffProbe.GetMediaInfo(entry.Path);
						if (info == null) {
							entry.Flags.Set(EntryFlags.MetadataError);
							return;
						}
						entry.mediaInfo = info;
					}

					if (entry.grayBytes == null) {
						var (error, grayBytes) = entry.IsImage ? GetImageAsBitmaps(entry, positionList.Count) : GetVideoThumbnailAsBitmaps(entry, positionList);
						if (error > 0)
							entry.Flags.Set(error);
						else
							entry.grayBytes = grayBytes;
					}

					IncrementProgress(entry.Path);
				});
				SearchSW.Stop();
				Logger.Instance.Info(string.Format(Properties.Resources.ThumbnailsFinished, SearchSW.Elapsed, processedFiles));

				SearchSW.Restart();
				var percentageDifference = 1.0f - Settings.Percent / 100f;
				var dupeScanList = ScanFileList.Where(vf => !vf.Flags.Any(EntryFlags.AllErrors | EntryFlags.ManuallyExcluded)).ToList();

				InitProgress(dupeScanList.Count);
				Parallel.For(0, dupeScanList.Count, parallelOpts, i => {
					while (pauseTokenSource.IsPaused) Thread.Sleep(50);

					var baseItem = dupeScanList[i];
					if (baseItem.grayBytes == null || baseItem.grayBytes.Count == 0) {
						IncrementProgress(baseItem.Path);
						return;
					}

					for (var n = i + 1; n < dupeScanList.Count; n++) {
						var compItem = dupeScanList[n];
						if (baseItem.IsImage && !compItem.IsImage) continue;
						if (compItem.grayBytes == null || compItem.grayBytes.Count == 0) continue;
						if (baseItem.grayBytes.Count != compItem.grayBytes.Count) continue;
						var duplicateCounter = 0;
						var percent = new float[baseItem.grayBytes.Count];
						for (var j = 0; j < baseItem.grayBytes.Count; j++) {
							percent[j] = ExtensionMethods.PercentageDifference2(baseItem.grayBytes[j], compItem.grayBytes[j]);
							if (percent[j] < percentageDifference) {
								duplicateCounter++;
							}
							else { break; }
						}
						if (duplicateCounter != baseItem.grayBytes.Count) continue;

						var percSame = percent.Average();
						lock (duplicateDict) {
							if (duplicateDict.TryGetValue(baseItem.Path, out var existing)) {
								if (!duplicateDict.TryGetValue(compItem.Path, out _))
									duplicateDict.Add(compItem.Path, new DuplicateItem(compItem, percSame) { GroupId = existing.GroupId });
							}
							else {
								if (duplicateDict.TryGetValue(compItem.Path, out existing)) {
									duplicateDict.Add(baseItem.Path, new DuplicateItem(baseItem, percSame) { GroupId = existing.GroupId });
								}
								else {
									var groupId = Guid.NewGuid();
									duplicateDict.Add(compItem.Path, new DuplicateItem(compItem, percSame) { GroupId = groupId });
									duplicateDict.Add(baseItem.Path, new DuplicateItem(baseItem, percSame) { GroupId = groupId });
								}
							}
						}
					}
					IncrementProgress(baseItem.Path);
				});

				SearchSW.Stop();
				Logger.Instance.Info(string.Format(Properties.Resources.DuplicatesCheckFinishedIn, SearchSW.Elapsed));
				Duplicates = new HashSet<DuplicateItem>(duplicateDict.Values);
			}
			catch (OperationCanceledException) {
				Logger.Instance.Info(Properties.Resources.CancellationExceptionCaught);
			}
		}

		private void PopulateThumbnails(CancellationToken cancelToken) {
			var st = Stopwatch.StartNew();
			try {
				var dupList = Duplicates.Where(d => d.Thumbnail == null || d.Thumbnail.Count == 0).ToList();
				InitProgress(dupList.Count);
				Parallel.For(0, dupList.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancelToken }, i => {
					var entry = dupList[i];
					entry.UpdateThumbnails(entry.IsImage ? GetImageThumbnail(entry, positionList.Count) : GetVideoThumbnail(entry, positionList));
					IncrementProgress(entry.Path);
				});
				st.Stop();
				Logger.Instance.Info(string.Format(Properties.Resources.ThumbnailsFinished, st.Elapsed, processedFiles));
			}
			catch (OperationCanceledException) {
				Logger.Instance.Info(Properties.Resources.CancellationExceptionCaught);
			}
		}

		public struct OwnScanProgress {
			public string CurrentFile;
			public int CurrentPosition;
			public TimeSpan Elapsed;
			public TimeSpan Remaining;
		}

		private List<Image> GetVideoThumbnail(DuplicateItem videoFile, List<float> positions) {
			var ffMpeg = new FFmpegWrapper.FFmpegWrapper();
			var images = new List<Image>();
			try {
				for (var i = 0; i < positions.Count; i++) {
					var b = ffMpeg.GetVideoThumbnail(videoFile.Path, Convert.ToSingle(videoFile.Duration.TotalSeconds * positionList[i]), false);
					if (b == null || b.Length == 0) return null;
					using (var byteStream = new MemoryStream(b)) {
						var bitmapImage = Image.FromStream(byteStream);
						images.Add(bitmapImage);
					}
				}
			}
			catch (FFmpegWrapper.FFMpegException ex) {
				Logger.Instance.Info($"FFMpegException, file: {videoFile.Path}, reason: {ex.Message}");
				return null;
			}
			catch (Exception ex) {
				Logger.Instance.Info($"Exception, file: {videoFile.Path}, reason: {ex.Message}, stacktrace {ex.StackTrace}");
				return null;
			}
			return images;
		}
		private static List<Image> GetImageThumbnail(DuplicateItem videoFile, int count) {
			var images = new List<Image>();
			for (var i = 0; i < count; i++) {
				Image bitmapImage;
				try {
					bitmapImage = Image.FromFile(videoFile.Path);
				}
				catch (Exception ex) {
					Logger.Instance.Info($"Exception, file: {videoFile.Path}, reason: {ex.Message}, stacktrace {ex.StackTrace}");
					return null;
				}
				//Fill some missing data now when we have the information
				videoFile.FrameSize = $"{bitmapImage.Width}x{bitmapImage.Height}";
				videoFile.FrameSizeInt = bitmapImage.Width + bitmapImage.Height;

				double resizeFactor = 1;
				if (bitmapImage.Width > 100 || bitmapImage.Height > 100) {
					double widthFactor = Convert.ToDouble(bitmapImage.Width) / 100;
					double heightFactor = Convert.ToDouble(bitmapImage.Height) / 100;
					resizeFactor = Math.Max(widthFactor, heightFactor);

				}
				int width = Convert.ToInt32(bitmapImage.Width / resizeFactor);
				int height = Convert.ToInt32(bitmapImage.Height / resizeFactor);
				var newImage = new Bitmap(width, height);
				using (var g = Graphics.FromImage(newImage)) {
					g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
					g.DrawImage(bitmapImage, 0, 0, newImage.Width, newImage.Height);
				}

				bitmapImage.Dispose();

				images.Add(newImage);
			}
			return images;
		}

		private (EntryFlags err, List<byte[]>) GetVideoThumbnailAsBitmaps(VideoFileEntry videoFile, List<float> positions) {
			var ffMpeg = new FFmpegWrapper.FFmpegWrapper();
			var images = new List<byte[]>();
			try {
				for (var i = 0; i < positions.Count; i++) {

					var b = ffMpeg.GetVideoThumbnail(videoFile.Path, Convert.ToSingle(videoFile.mediaInfo.Duration.TotalSeconds * positionList[i]), true);
					if (b == null || b.Length == 0) return (EntryFlags.ThumbnailError, null);
					var d = ExtensionMethods.VerifyGrayScaleValues(b);
					if (d == null) return (EntryFlags.TooDark, null);
					images.Add(d);
				}

			}
			catch (FFmpegWrapper.FFMpegException ex) {
				Logger.Instance.Info($"FFMpegException, file: {videoFile.Path}, reason: {ex.Message}");
				return (EntryFlags.ThumbnailError, null);
			}
			return (0, images);

		}

		private static (EntryFlags err, List<byte[]>) GetImageAsBitmaps(VideoFileEntry videoFile, int count) {
			var images = new List<byte[]>();
			for (var i = 0; i < count; i++) {
				try {
					using (var byteStream = File.OpenRead(videoFile.Path)) {
						using (var bitmapImage = Image.FromStream(byteStream)) {
							var b = new Bitmap(16, 16);
							using (var g = Graphics.FromImage(b)) {
								g.DrawImage(bitmapImage, 0, 0, 16, 16);
							}
							var d = ExtensionMethods.GetGrayScaleValues(b);
							if (d == null) return (EntryFlags.TooDark, null);
							images.Add(d);
						}
					}
				}
				catch (Exception ex) {
					Logger.Instance.Info($"Exception, file: {videoFile.Path}, reason: {ex.Message}, stacktrace {ex.StackTrace}");
					return (EntryFlags.ThumbnailError, null);
				}
			}
			return (0, images);
		}


		private static class ExtensionMethods {
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public static byte[] VerifyGrayScaleValues(byte[] data, double darkProcent = 80) {
				int darkPixels = 0;
				for (int i = 0; i < data.Length; i++) {
					if (data[i] <= 0x20)
						darkPixels++;
				}
				return 100d / data.Length * darkPixels >= darkProcent ? null : data;
			}

			public static unsafe byte[] GetGrayScaleValues(Bitmap original, double darkProcent = 80) {
				// Lock the bitmap's bits.  
				var rect = new Rectangle(0, 0, original.Width, original.Height);
				var bmpData = original.LockBits(rect, ImageLockMode.ReadOnly, original.PixelFormat);

				// Get the address of the first line.
				var ptr = bmpData.Scan0;

				// Declare an array to hold the bytes of the bitmap.
				var bytes = bmpData.Stride * original.Height;
				var rgbValues = new byte[bytes];
				var buffer = new byte[256];

				// Copy the RGB values into the array.
				fixed (byte* byteArrayPtr = rgbValues) {
					Buffer.MemoryCopy((void*)ptr, byteArrayPtr, rgbValues.Length, rgbValues.Length);
				}
				original.UnlockBits(bmpData);

				int count = 0, all = bmpData.Width * bmpData.Height;
				var buffercounter = 0;
				for (var i = 0; i < rgbValues.Length; i += 4) {
					byte r = rgbValues[i + 2], g = rgbValues[i + 1], b = rgbValues[i];
					buffer[buffercounter] = r;
					buffercounter++;
					var brightness = (byte)Math.Round(0.299 * r + 0.5876 * g + 0.114 * b);
					if (brightness <= 0x20)
						count++;
				}
				return 100d / all * count >= darkProcent ? null : buffer;

			}
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public static float PercentageDifference2(byte[] img1, byte[] img2) {
				if (img1.AsSpan().SequenceEqual(img2.AsSpan())) return 1f;
				float diff = 0;
				for (var y = 0; y < img1.Length; y++) {
					diff += (float)Math.Abs(img1[y] - img2[y]) / 255;
				}
				return diff / (16 * 16);
			}
		}
	}
}
