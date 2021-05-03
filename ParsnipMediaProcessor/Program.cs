using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Hosting;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParsnipData;
using ParsnipData.Media;
using System.Drawing;

namespace ParsnipMediaProcessor
{
    class Program
    {
        private static readonly string Website = ConfigurationManager.AppSettings["WebsiteUrl"];
        private static readonly string FtpUrl = ConfigurationManager.AppSettings["FtpUrl"];
        private static readonly NetworkCredential FtpCredentials = new NetworkCredential(ConfigurationManager.AppSettings["FtpUsername"], ConfigurationManager.AppSettings["FtpPassword"]);
        private static readonly short NumberOfGeneratedThumbnails = Convert.ToInt16(ConfigurationManager.AppSettings["NumberOfGeneratedThumbnails"]);
        private static readonly int MaxShortSide = Convert.ToInt16(ConfigurationManager.AppSettings["MaxShortSide"]);
        private static readonly short CompressionLevel = Convert.ToInt16(ConfigurationManager.AppSettings["CompressionLevel"]);
        private static readonly string RemoteOriginalVideosDir = ConfigurationManager.AppSettings["RemoteOriginalsDir"];
        private static readonly string RemoteCompressedVideosDir = ConfigurationManager.AppSettings["RemoteCompressedDir"];
        private static readonly string RemoteThumbnailsDir = ConfigurationManager.AppSettings["RemoteThumbnailsDir"];
        private static readonly string RelativeLocalOriginalVideosDir = ConfigurationManager.AppSettings["RelativeLocalOriginalsDir"];
        private static readonly string RelativeLocalThumbnailsDir = ConfigurationManager.AppSettings["RelativeLocalThumbnailsDir"];
        private static readonly string RelativeLocalCompressedVideosDir = ConfigurationManager.AppSettings["RelativeLocalCompressedDir"];
        private static readonly string FullyQualifiedLocalOriginalVideosDir = $"{AppDomain.CurrentDomain.BaseDirectory}{RelativeLocalOriginalVideosDir}";
        private static readonly string FullyQualifiedLocalCompressedVideosDir = $"{AppDomain.CurrentDomain.BaseDirectory}{RelativeLocalCompressedVideosDir}";
        private static readonly string HandbrakeCLIDir = ConfigurationManager.AppSettings["HandbrakeCLIDir"];
        private static readonly string LocalWebsiteDir = ConfigurationManager.AppSettings["LocalWebsiteDir"];
        public static readonly string CompressedFileExtension = ".mp4";
        static void Main(string[] args)
        {
            try
            {
                CheckDirectories();
                CompressVideo();
                StitchVideoSequence();
            }
            catch(Exception ex)
            {

            }
            finally
            {
                Thread.Sleep(5000);
                Process.Start(AppDomain.CurrentDomain.FriendlyName);
                Environment.Exit(0);
            }
        }

        static void CompressVideo()
        {
            Video Video = null;
            string localOriginalFileDir = null;
            string localCompressedFileDir = null;

            try
            {
                Video = Video.SelectOldestUncompressed();
                if (Video.Id != null)
                {
                    try
                    {
                        if (Video != null && Video.Id != null && Video.VideoData != null)
                        {
                            if (Video.Status.Equals(MediaStatus.Reprocess))
                                Video.DeleteAllThumbnails();

                            Video.Status = MediaStatus.Processing;
                            Video.UpdateMetadata();

                            localOriginalFileDir = $"{FullyQualifiedLocalOriginalVideosDir}\\{Video.Id}{Video.VideoData.OriginalFileExtension}";
                            localCompressedFileDir = $"{FullyQualifiedLocalCompressedVideosDir}\\{Video.Id}.mp4";
                            if (TryDownload())
                            {
                                ScrapeLocalVideoData(Video, localOriginalFileDir);
                                GenerateAndUploadThumbnails(Video);
                                compressVideo(Video);
                                ScrapeLocalVideoData(Video, localCompressedFileDir);
                                UploadCompressedVideo(Video);
                                Video.Status = MediaStatus.Complete;
                                Video.UpdateMetadata();
                            }
                            else
                            {
                                Video.Status = MediaStatus.Error;
                                Video.UpdateMetadata();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Video.Status = MediaStatus.Error;
                        Video.UpdateMetadata();
                        throw ex;
                    }
                }
            }
            catch(Exception ex)
            {
                Debug.WriteLine($"There was an exception whilst compressing a video {ex}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(localOriginalFileDir))
                    File.Delete(localOriginalFileDir);

                if (Video != null && Video.Id != null && !string.IsNullOrWhiteSpace(Video.Id.ToString()))
                    File.Delete($"{FullyQualifiedLocalCompressedVideosDir}\\{Video.Id}{CompressedFileExtension}");
            }

            bool TryDownload()
            {
                try
                {
                    DownloadFile(Video.VideoData.OriginalFileDir, localOriginalFileDir);
                    return true;
                }
                catch(Exception ex)
                {
                    Debug.WriteLine($"There was an exception whilst downloading a file: {ex}");
                    return false;
                }
            }
        }

        static void compressVideo(Video video, string originalFileDir = null)
        {
            int originalScale = Media.GetAspectScale(video.VideoData.Width, video.VideoData.Height);
            int compressedScale = default;
            var compressedFileName = $"{RelativeLocalCompressedVideosDir}\\{video.Id}";

            if (originalFileDir == null)
            {
                originalFileDir = $"{RelativeLocalOriginalVideosDir}\\{video.Id}{video.VideoData.OriginalFileExtension}";
            }
            
            if (video.VideoData.Width > video.VideoData.Height)
            {
                //Height is short side
                for (int i = 1; i <= originalScale; i++)
                {
                    var YScale = video.VideoData.YScale * i;
                    if (YScale <= MaxShortSide && YScale % 2 == 0)
                        compressedScale = i;
                }
            }
            else
            {
                //Width is short side
                for (int i = 1; i <= originalScale; i++)
                {
                    var XScale = video.VideoData.XScale * i;
                    if (XScale <= MaxShortSide && XScale % 2 == 0)
                        compressedScale = i;
                }
            }

            if (compressedScale == 0)
                compressedScale = originalScale;

            var compressedFileWidth = video.VideoData.XScale * compressedScale;
            var compressedFileHeight = video.VideoData.YScale * compressedScale;

            Process process = new Process();
            process.StartInfo.FileName = "CompressAuto.bat";
            process.StartInfo.Arguments = $"{originalFileDir} {compressedFileName} {CompressedFileExtension} {compressedFileWidth} {compressedFileHeight} {CompressionLevel}";
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            process.Start();
            process.WaitForExit();
            int exitCode = process.ExitCode;
            process.Close();

            if(exitCode != 0)
            {
                throw new Exception($"ffmpeg compression process failed. Exit code: {exitCode}");
            }
        }

        static void StitchVideoSequence()
        {
            VideoSequence VideoSequence = null;
            var linesDirectory = $"{RelativeLocalCompressedVideosDir}\\{Guid.NewGuid().ToString().Substring(0, 8)}.txt";
            string localStitchedFileDir = null;
            try
            {
                VideoSequence = VideoSequence.SelectOldestUnstitchedVideoSequence();

                if (VideoSequence != null && VideoSequence.Video != null && VideoSequence.Video.Id != null && VideoSequence.Video.VideoData != null && VideoSequence.SequencedVideos != null)
                {
                    if (VideoSequence.Video.Status.Equals(MediaStatus.Reprocess))
                        VideoSequence.Video.DeleteAllThumbnails();

                    int xScale = VideoSequence.SequencedVideos[0].VideoData.XScale;
                    int yScale = VideoSequence.SequencedVideos[0].VideoData.YScale;
                    var scaleVideo = new Video();
                    
                    VideoSequence.Video.Status = MediaStatus.Processing;
                    VideoSequence.Video.UpdateMetadata();
                    localStitchedFileDir = $"{RelativeLocalCompressedVideosDir}\\{VideoSequence.Video.Id}{CompressedFileExtension}";
                    if (SequencedVideosAreCompressed())
                    {
                        VideoSequence.Video.VideoData.CompressedFileDir = $"{RemoteCompressedVideosDir}/{VideoSequence.Video.Id}{CompressedFileExtension}";

                        if (TryDownload())
                        {
                            ScrapeLocalVideoData(scaleVideo, $"{RelativeLocalCompressedVideosDir}/{VideoSequence.SequencedVideos[0].VideoData.CompressedFileName}");
                            foreach (var video in VideoSequence.SequencedVideos)
                            {
                                ScrapeLocalVideoData(video, $"{RelativeLocalCompressedVideosDir}/{video.VideoData.CompressedFileName}");
                                if (video.VideoData.Width != scaleVideo.VideoData.Width || video.VideoData.Height != scaleVideo.VideoData.Height)
                                {
                                    throw new InvalidOperationException("Cannot combine videos of different aspect ratios");
                                }
                            }
                            StitchVideo();
                            ScrapeLocalVideoData(VideoSequence.Video, localStitchedFileDir);
                            GenerateAndUploadThumbnails(VideoSequence.Video, true);
                            UploadCompressedVideo(VideoSequence.Video);
                            VideoSequence.Video.Status = MediaStatus.Complete;
                            VideoSequence.Video.UpdateMetadata();
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                Debug.WriteLine($"There was an exception whilst stitching a video: {ex}");
                VideoSequence.Video.Status = MediaStatus.Error;
                VideoSequence.Video.UpdateMetadata();
            }
            finally
            {
                File.Delete(linesDirectory);

                if (VideoSequence != null)
                {
                    File.Delete($"{FullyQualifiedLocalCompressedVideosDir}\\{VideoSequence.Video.Id}{CompressedFileExtension}");

                    if (VideoSequence.SequencedVideos != null)
                    {
                        foreach (var video in VideoSequence.SequencedVideos)
                        {
                            File.Delete($"{FullyQualifiedLocalCompressedVideosDir}\\{video.Id}{CompressedFileExtension}");
                            if(File.Exists($"{FullyQualifiedLocalCompressedVideosDir}\\{video.Id}_output{CompressedFileExtension}"))
                                File.Delete($"{FullyQualifiedLocalCompressedVideosDir}\\{video.Id}_output{CompressedFileExtension}");
                        }
                    }
                }
            }

            bool SequencedVideosAreCompressed()
            {
                foreach (var video in VideoSequence.SequencedVideos)
                {
                    if (string.IsNullOrWhiteSpace(video.Compressed))
                        return false;
                }
                return true;
            }

            bool TryDownload()
            {
                foreach (var video in VideoSequence.SequencedVideos)
                {
                    var localCompressedFileDir = $"{FullyQualifiedLocalCompressedVideosDir}\\{video.VideoData.CompressedFileName}";
                    try
                    {
                        DownloadFile(video.VideoData.CompressedFileDir, localCompressedFileDir);
                    }
                    catch(Exception ex)
                    {
                        Debug.WriteLine($"There was an exception whilst downloading a sequenced file: {ex}");
                        VideoSequence.Video.Status = MediaStatus.Error;
                        VideoSequence.Video.UpdateMetadata();
                        return false;
                    }
                }
                return true;
            }

            void StitchVideo()
            {
                using (var linesFile = File.CreateText(linesDirectory))
                {
                    foreach (var video in VideoSequence.SequencedVideos)
                    {
                        linesFile.WriteLine($"file '{RelativeLocalCompressedVideosDir}/{video.VideoData.CompressedFileName}'");
                    }
                }

                using (var process = new Process())
                {
                    process.StartInfo.FileName = "AppendVideo.bat";
                    process.StartInfo.Arguments = $"{linesDirectory} {localStitchedFileDir}";
                    process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                    process.Start();
                    process.WaitForExit();
                    int exitCode = process.ExitCode;
                    process.Close();
                }
                    
            }
        }
        static void CheckDirectories()
        {
            if (!Directory.Exists(RelativeLocalOriginalVideosDir))
                Directory.CreateDirectory(RelativeLocalOriginalVideosDir);

            if (!Directory.Exists(RelativeLocalCompressedVideosDir))
                Directory.CreateDirectory(RelativeLocalCompressedVideosDir);

            if (!Directory.Exists(RelativeLocalThumbnailsDir))
                Directory.CreateDirectory(RelativeLocalThumbnailsDir);
        }

        static void GenerateAndUploadThumbnails(Video video, bool UseCompressedVideo = false)
        {
            var originalsDir = $"{RelativeLocalThumbnailsDir}\\{video.Id}\\AutoGen\\Originals";
            var compressedDir = $"{RelativeLocalThumbnailsDir}\\{video.Id}\\AutoGen\\Compressed";
            var placeholderDir = $"{RelativeLocalThumbnailsDir}\\{video.Id}\\AutoGen\\Placeholders";
            var segment = new TimeSpan(video.VideoData.Duration.Ticks / NumberOfGeneratedThumbnails);

            CreateLocalDirectories();
            GenerateAndUploadThumbnails();
            InsertThumbnailData();

            void CreateLocalDirectories(){
                if (Directory.Exists(originalsDir))
                    Directory.Delete(originalsDir, true);

                if (Directory.Exists(compressedDir))
                    Directory.Delete(compressedDir, true);

                if (Directory.Exists(placeholderDir))
                    Directory.Delete(placeholderDir, true);

                Directory.CreateDirectory(originalsDir);
                Directory.CreateDirectory(compressedDir);
                Directory.CreateDirectory(placeholderDir);
            }
            void GenerateAndUploadThumbnails()
            {
                for (int i = 0; i < NumberOfGeneratedThumbnails; i++)
                {
                    var timeStamp = new TimeSpan(segment.Ticks * i);
                    string thumbnailIdentifier = MediaId.NewMediaId().ToString();
                    var videoThumbnail = new VideoThumbnail();
                    System.Drawing.Image originalImage = null;

                    InitialiseThumbnail();
                    GenerateImages();
                    UploadThumbnail(videoThumbnail, thumbnailIdentifier);
                    video.Thumbnails.Add(videoThumbnail);

                    void GenerateImages()
                    {
                        originalImage = GenerateOriginal();
                        UpdateVideoThumbnailScale();
                        GenerateCompressed();
                        GeneratePlaceholder();

                        System.Drawing.Image GenerateOriginal()
                        {
                            var thumbnailDir = $"{RelativeLocalThumbnailsDir}\\{videoThumbnail.MediaId}\\AutoGen\\Originals\\{videoThumbnail.MediaId}_{thumbnailIdentifier}.png";
                            var videoDir = UseCompressedVideo ? $"{RelativeLocalCompressedVideosDir}\\{video.Id}{video.VideoData.CompressedFileExtension}" : $"{RelativeLocalOriginalVideosDir}\\{video.Id}{video.VideoData.OriginalFileExtension}";
                            Process process = new Process();
                            process.StartInfo.FileName = "GenerateThumbnail.bat";
                            process.StartInfo.Arguments = $"{thumbnailDir} {videoDir} {timeStamp}";
                            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                            process.Start();
                            process.WaitForExit();
                            int exitCode = process.ExitCode;
                            process.Close();
                            return Bitmap.FromFile(thumbnailDir);
                        }

                        void UpdateVideoThumbnailScale()
                        {
                            int scale = Media.GetAspectScale(originalImage.Width, originalImage.Height);
                            videoThumbnail.XScale = Convert.ToInt16(originalImage.Width / scale);
                            videoThumbnail.YScale = Convert.ToInt16(originalImage.Height / scale);
                        }
                        void GenerateCompressed()
                        {
                            var localDir = $"{RelativeLocalThumbnailsDir}\\{videoThumbnail.MediaId}\\AutoGen\\Compressed\\{videoThumbnail.MediaId}_{thumbnailIdentifier}.jpg";
                            Bitmap bitmap = Media.GenerateBitmapOfSize(originalImage, 1280, 200);
                            Media.SaveBitmapWithCompression(bitmap, 85L, localDir);
                        }
                        void GeneratePlaceholder()
                        {
                            var localDir = $"{RelativeLocalThumbnailsDir}\\{videoThumbnail.MediaId}\\AutoGen\\Placeholders\\{videoThumbnail.MediaId}_{thumbnailIdentifier}.jpg";
                            Bitmap bitmap = Media.GenerateBitmapOfSize(originalImage, 250, 0);
                            Media.SaveBitmapWithCompression(bitmap, 15L, localDir);
                        }
                    }
                    void InitialiseThumbnail()
                    {
                        videoThumbnail.MediaId = video.Id;
                        videoThumbnail.Placeholder = $"{RemoteThumbnailsDir}/Placeholders/{video.Id}_{thumbnailIdentifier}.jpg";
                        videoThumbnail.Compressed = $"{RemoteThumbnailsDir}/Compressed/{video.Id}_{thumbnailIdentifier}.jpg";
                        videoThumbnail.Original = $"{RemoteThumbnailsDir}/Originals/{video.Id}_{thumbnailIdentifier}.png";
                    }
                }
            }
            void InsertThumbnailData()
            {
                foreach (var videoThumbnail in video.Thumbnails)
                {
                    videoThumbnail.Insert();
                }
            }
        }
        static long GetRemoteFileSize(string remoteFileDir)
        {
            long size;
            if (string.IsNullOrEmpty(LocalWebsiteDir))
            {
                var fileUrl = $"{FtpUrl}/{Website}/wwwroot/{remoteFileDir}";
                FtpWebRequest request = (FtpWebRequest)WebRequest.Create(fileUrl);
                request.Method = WebRequestMethods.Ftp.GetFileSize;
                request.Credentials = FtpCredentials;

                FtpWebResponse response = (FtpWebResponse)request.GetResponse();
                size = response.ContentLength;
                response.Close();
            }
            else
            {
                size = new FileInfo($"{LocalWebsiteDir}{remoteFileDir}").Length;
            }

            return size;
        }

        static void DownloadFile(string remoteFileDir, string localFileDir)
        {
            if (File.Exists(localFileDir))
                File.Delete(localFileDir);

            if (string.IsNullOrEmpty(LocalWebsiteDir))
                DownloadFromFtp();
            else
                DownloadFromLocal();

            void DownloadFromFtp()
            {
                long expectedFileSize = GetRemoteFileSize(remoteFileDir);
                var fileUrl = $"{FtpUrl}/{Website}/wwwroot/{remoteFileDir}";
                FtpWebRequest request = (FtpWebRequest)WebRequest.Create(fileUrl);
                request.Method = WebRequestMethods.Ftp.DownloadFile;
                request.Credentials = FtpCredentials;
                try
                {
                    using (var response = (FtpWebResponse)request.GetResponse())
                    {
                        using (Stream responseStream = response.GetResponseStream())
                        {
                            using (FileStream fileStream = File.Create(localFileDir))
                            {
                                responseStream.CopyTo(fileStream);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    long downloadedFileSize = new FileInfo(localFileDir).Length;
                    if (downloadedFileSize != expectedFileSize)
                        throw ex;
                }
            }

            void DownloadFromLocal()
            {
                File.Copy($"{LocalWebsiteDir}{remoteFileDir}", localFileDir);
            }
        }

        static void UploadThumbnail(VideoThumbnail videoThumbnail, string thumbnailIdentifier)
        {
            if (string.IsNullOrEmpty(LocalWebsiteDir))
            {
                FtpUpload("Originals", ".png");
                FtpUpload("Compressed", ".jpg");
                FtpUpload("Placeholders", ".jpg");
            }
            else
            {
                LocalUpload("Originals", ".png");
                LocalUpload("Compressed", ".jpg");
                LocalUpload("Placeholders", ".jpg");
            }

            void FtpUpload(string folder, string extension)
            {
                var ftpClient = (FtpWebRequest)WebRequest.Create($"{FtpUrl}/{Website}/wwwroot/{RemoteThumbnailsDir}/{folder}/{videoThumbnail.MediaId}_{thumbnailIdentifier}{extension}");
                ftpClient.Credentials = FtpCredentials;
                ftpClient.Method = System.Net.WebRequestMethods.Ftp.UploadFile;
                ftpClient.UseBinary = true;
                ftpClient.KeepAlive = true;
                var fi = new FileInfo($"{RelativeLocalThumbnailsDir}\\{videoThumbnail.MediaId}\\AutoGen\\{folder}\\{videoThumbnail.MediaId}_{thumbnailIdentifier}{extension}");
                ftpClient.ContentLength = fi.Length;
                byte[] buffer = new byte[4097];
                int bytes = 0;
                int total_bytes = (int)fi.Length;
                try
                {
                    using (FileStream fs = fi.OpenRead())
                    {
                        using (Stream rs = ftpClient.GetRequestStream())
                        {
                            while (total_bytes > 0)
                            {
                                bytes = fs.Read(buffer, 0, buffer.Length);
                                rs.Write(buffer, 0, bytes);
                                total_bytes -= bytes;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }

            void LocalUpload(string folder, string extension)
            {
                File.Copy($"{RelativeLocalThumbnailsDir}\\{videoThumbnail.MediaId}\\AutoGen\\{folder}\\{videoThumbnail.MediaId}_{thumbnailIdentifier}{extension}", $"{LocalWebsiteDir}{RemoteThumbnailsDir}/{folder}/{videoThumbnail.MediaId}_{thumbnailIdentifier}{extension}");
            }
        }

        static void UploadCompressedVideo(Video video)
        {
            video.VideoData.CompressedFileDir = $"{RemoteCompressedVideosDir}/{video.Id}{CompressedFileExtension}";
            var localFileDir = $"{FullyQualifiedLocalCompressedVideosDir}\\{video.VideoData.CompressedFileName}";

            if (string.IsNullOrEmpty(LocalWebsiteDir))
                FtpUpload();
            else
                LocalUpload();

            void FtpUpload()
            {
                long expectedFileSize = new FileInfo(localFileDir).Length;
                var ftpClient = (FtpWebRequest)WebRequest.Create($"{FtpUrl}/{Website}/wwwroot/{video.VideoData.CompressedFileDir}");
                ftpClient.Credentials = FtpCredentials;
                ftpClient.Method = System.Net.WebRequestMethods.Ftp.UploadFile;
                ftpClient.UseBinary = true;
                ftpClient.KeepAlive = true;
                var fi = new FileInfo($"{FullyQualifiedLocalCompressedVideosDir}\\{video.Id}{CompressedFileExtension}");
                ftpClient.ContentLength = fi.Length;
                byte[] buffer = new byte[4097];
                int bytes = 0;
                int total_bytes = (int)fi.Length;
                try
                {
                    using (FileStream fs = fi.OpenRead())
                    {
                        using (Stream rs = ftpClient.GetRequestStream())
                        {
                            while (total_bytes > 0)
                            {
                                bytes = fs.Read(buffer, 0, buffer.Length);
                                rs.Write(buffer, 0, bytes);
                                total_bytes -= bytes;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    long actualFileSize = GetRemoteFileSize(video.VideoData.CompressedFileDir);
                    if (actualFileSize != expectedFileSize)
                        throw ex;
                }
            }

            void LocalUpload()
            {
                var destination = $"{LocalWebsiteDir}{video.VideoData.CompressedFileDir}";
                
                if (File.Exists(destination))
                    File.Delete(destination);

                File.Copy(localFileDir, destination);
            }
        }
        
        static bool ScrapeLocalVideoData(Video video, string localVideoDir)
        {
            StringBuilder output = new StringBuilder();
            try
            {
                var process = new Process();
                process.StartInfo.FileName = "ScrapeVideoData.bat";
                process.StartInfo.Arguments = localVideoDir;
                process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.UseShellExecute = false;
                process.OutputDataReceived += new DataReceivedEventHandler((sender, e) =>
                {
                    // Prepend line numbers to each line of the output.
                    if (!String.IsNullOrEmpty(e.Data))
                    {
                        output.Append($"\n {e.Data}");
                    }
                });
                process.Start();
                process.BeginOutputReadLine();
                process.WaitForExit();
                var rotateTag = "TAG:rotate";
                int rotation = 0;
                if (output.ToString().Contains(rotateTag))
                    rotation = Convert.ToInt32(output.ToString().Split(' ').Where(x => x.Contains(rotateTag)).First().Split('=').Last());

                int scale;
                if(rotation == 90 || rotation == 270)
                {
                    video.VideoData.Width = Convert.ToInt32(output.ToString().Split(' ').Where(x => x.Contains("height=")).First().Split('=').Last());
                    video.VideoData.Height = Convert.ToInt32(output.ToString().Split(' ').Where(x => x.Contains("width=")).First().Split('=').Last());
                }
                else
                {
                    video.VideoData.Width = Convert.ToInt32(output.ToString().Split(' ').Where(x => x.Contains("width=")).First().Split('=').Last());
                    video.VideoData.Height = Convert.ToInt32(output.ToString().Split(' ').Where(x => x.Contains("height=")).First().Split('=').Last());
                }

                scale = Media.GetAspectScale(video.VideoData.Width, video.VideoData.Height);
                video.VideoData.XScale = Convert.ToInt16(video.VideoData.Width / scale);
                video.VideoData.YScale = Convert.ToInt16(video.VideoData.Height / scale);
                video.VideoData.Duration = TimeSpan.FromSeconds(Math.Floor(Convert.ToDouble(
                            output.ToString().Split(' ').Where(x => x.Contains("duration=")).First().Split('=').Last()) * 1000) / 1000);

                return true;
            }
            catch(Exception ex)
            {
                Debug.WriteLine($"There was an exception whilst scraping local video data {ex}");
                return false;
            }
        }

        static string[] GetFileList()
        {
            string[] downloadFiles;
            var result = new StringBuilder();
            WebResponse response = null;
            StreamReader reader = null;

            try
            {
                var request = (FtpWebRequest)WebRequest.Create($"{FtpUrl}/{Website}/wwwroot/{RemoteOriginalVideosDir}");
                request.UseBinary = true;
                request.Method = WebRequestMethods.Ftp.ListDirectory;
                request.Credentials = FtpCredentials;
                request.KeepAlive = false;
                request.UsePassive = true;
                response = request.GetResponse();
                reader = new StreamReader(response.GetResponseStream());
                string line = reader.ReadLine();
                while (line != null)
                {
                    result.Append(line);
                    result.Append("\n");
                    line = reader.ReadLine();
                }
                result.Remove(result.ToString().LastIndexOf('\n'), 1);
                return result.ToString().Split('\n');
            }
            catch (WebException ex)
            {
                if (reader != null)
                {
                    reader.Close();
                }
                if (response != null)
                {
                    response.Close();
                }
                downloadFiles = null;
                return downloadFiles;
            }
            catch (Exception ex)
            {
                if (reader != null)
                {
                    reader.Close();
                }
                if (response != null)
                {
                    response.Close();
                }
                downloadFiles = null;
                return downloadFiles;
            }
        }
    }
}
