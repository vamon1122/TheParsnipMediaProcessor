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

namespace ParsnipMediaProcessor
{
    class Program
    {
        private static readonly string Website = ConfigurationManager.AppSettings["WebsiteUrl"];
        private static readonly string FtpUrl = ConfigurationManager.AppSettings["FtpUrl"];
        private static readonly NetworkCredential FtpCredentials = new NetworkCredential(ConfigurationManager.AppSettings["FtpUsername"], ConfigurationManager.AppSettings["FtpPassword"]);
        private static readonly string RemoteOriginalsDir = ConfigurationManager.AppSettings["RemoteOriginalsDir"];
        private static readonly string RemoteCompressedDir = ConfigurationManager.AppSettings["RemoteCompressedDir"];
        private static readonly string RelativeLocalOriginalsDir = ConfigurationManager.AppSettings["RelativeLocalOriginalsDir"];
        private static readonly string RelativeLocalCompressedDir = ConfigurationManager.AppSettings["RelativeLocalCompressedDir"];
        private static readonly string LocalOriginalsDir = $"{AppDomain.CurrentDomain.BaseDirectory}{RelativeLocalOriginalsDir}";
        private static readonly string LocalCompressedDir = $"{AppDomain.CurrentDomain.BaseDirectory}{RelativeLocalCompressedDir}";
        public static readonly string HandbrakeCLIDir = ConfigurationManager.AppSettings["HandbrakeCLIDir"];
        public static readonly string CompressedFileExtension = ".mp4";
        static void Main(string[] args)
        {
            try
            {
                CompressVideo();
                StitchVideoSequence();
            }
            catch
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

            try
            {
                Video = Video.SelectOldestUncompressed();
                if (Video.Id != null)
                {
                    try
                    {
                        Video.Status = MediaStatus.Processing;
                        Video.UpdateMetadata();
                        if (Video != null && Video.Id != null && Video.VideoData != null)
                        {
                            localOriginalFileDir = $"{LocalOriginalsDir}\\{Video.Id}{Video.VideoData.OriginalFileExtension}";
                            if (TryDownload())
                            {
                                if (ScrapeLocalVideoData(Video, localOriginalFileDir))
                                {
                                    CompressVideo();
                                    UploadCompressedFile(Video);
                                    Video.Status = MediaStatus.Complete;
                                    Video.UpdateMetadata();
                                }
                                else
                                {
                                    Video.Status = MediaStatus.Error;
                                    Video.UpdateMetadata();
                                }
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
                    File.Delete($"{LocalCompressedDir}\\{Video.Id}{CompressedFileExtension}");
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

            void CompressVideo()
            {
                Process process = new Process();
                if (Video.VideoData.XScale > Video.VideoData.YScale)
                    process.StartInfo.FileName = "CompressLandscapeVideo.bat";
                else
                    process.StartInfo.FileName = "CompressPortraitVideo.bat";
                process.StartInfo.Arguments = $"{RelativeLocalOriginalsDir}\\{Video.Id}{Video.VideoData.OriginalFileExtension} {RelativeLocalCompressedDir}\\{Video.Id}{CompressedFileExtension}";
                process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                process.Start();
                process.WaitForExit();
                int exitCode = process.ExitCode;
                process.Close();
            }
        }

        static void StitchVideoSequence()
        {
            VideoSequence VideoSequence = null;
            var linesDirectory = $"{RelativeLocalCompressedDir}\\{Guid.NewGuid().ToString().Substring(0, 8)}.txt";
            string localStitchedFileDir = null;
            try
            {
                VideoSequence = VideoSequence.SelectOldestUnstitchedVideoSequence();

                if (VideoSequence != null && VideoSequence.Video != null && VideoSequence.Video.Id != null && VideoSequence.Video.VideoData != null && VideoSequence.SequencedVideos != null)
                {
                    localStitchedFileDir = $"{RelativeLocalCompressedDir}\\{VideoSequence.Video.Id}{CompressedFileExtension}";
                    if (SequencedVideosAreCompressed())
                    {
                        VideoSequence.Video.VideoData.CompressedFileDir = $"{RemoteCompressedDir}/{VideoSequence.Video.Id}{CompressedFileExtension}";

                        if (TryDownload())
                        {
                            StitchVideo();
                            ScrapeLocalVideoData(VideoSequence.Video, localStitchedFileDir);
                            UploadCompressedFile(VideoSequence.Video);
                            VideoSequence.Video.Update();
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                Debug.WriteLine($"There was an exception whilst stitching a video: {ex}");
            }
            finally
            {
                File.Delete(linesDirectory);

                if (VideoSequence != null)
                {
                    File.Delete($"{LocalCompressedDir}\\{VideoSequence.Video.Id}{CompressedFileExtension}");

                    if (VideoSequence.SequencedVideos != null)
                    {
                        foreach (var video in VideoSequence.SequencedVideos)
                        {
                            File.Delete($"{LocalCompressedDir}\\{video.Id}{CompressedFileExtension}");
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
                    var localCompressedFileDir = $"{LocalCompressedDir}\\{video.VideoData.CompressedFileName}";
                    try
                    {
                        DownloadFile(video.VideoData.CompressedFileDir, localCompressedFileDir);
                    }
                    catch(Exception ex)
                    {
                        Debug.WriteLine($"There was an exception whilst downloading a sequenced file: {ex}");
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
                        linesFile.WriteLine($"file '{RelativeLocalCompressedDir}/{video.VideoData.CompressedFileName}'");
                    }
                }

                using (var process = new Process())
                {
                    process.StartInfo.FileName = "AppendVideo.bat";
                    process.StartInfo.Arguments = $"{linesDirectory} {localStitchedFileDir}";
                    process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                    process.Start();
                    process.WaitForExit();
                }
                    
            }
        }

        static long GetRemoteFileSize(string remoteFileDir)
        {
            var fileUrl = $"{FtpUrl}/{Website}/wwwroot/{remoteFileDir}";
            FtpWebRequest request = (FtpWebRequest)WebRequest.Create(fileUrl);
            request.Method = WebRequestMethods.Ftp.GetFileSize;
            request.Credentials = FtpCredentials;

            FtpWebResponse response = (FtpWebResponse)request.GetResponse();
            long size = response.ContentLength;
            response.Close();

            return size;
        }

        static void DownloadFile(string remoteFileDir, string localFileDir)
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

        static void UploadCompressedFile(Video video)
        {
            video.VideoData.CompressedFileDir = $"{RemoteCompressedDir}/{video.Id}{CompressedFileExtension}";
            var localFileDir = $"{LocalCompressedDir}\\{video.VideoData.CompressedFileName}";
            long expectedFileSize = new FileInfo(localFileDir).Length;
            var ftpClient = (FtpWebRequest)WebRequest.Create($"{FtpUrl}/{Website}/wwwroot/{video.VideoData.CompressedFileDir}");
            ftpClient.Credentials = FtpCredentials;
            ftpClient.Method = System.Net.WebRequestMethods.Ftp.UploadFile;
            ftpClient.UseBinary = true;
            ftpClient.KeepAlive = true;
            var fi = new FileInfo($"{LocalCompressedDir}\\{video.Id}{CompressedFileExtension}");
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
            catch(Exception ex)
            {
                long actualFileSize = GetRemoteFileSize(video.VideoData.CompressedFileDir);
                if (actualFileSize != expectedFileSize)
                    throw ex;
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

                var width = Convert.ToInt32(output.ToString().Split(' ').Where(x => x.Contains("width=")).First().Split('=').Last());
                var height = Convert.ToInt32(output.ToString().Split(' ').Where(x => x.Contains("height=")).First().Split('=').Last());
                var duration = Convert.ToDecimal(output.ToString().Split(' ').Where(x => x.Contains("duration=")).First().Split('=').Last());
                var rotateTag = "TAG:rotate";
                int rotation = 0;
                if (output.ToString().Contains(rotateTag))
                    rotation = Convert.ToInt32(output.ToString().Split(' ').Where(x => x.Contains(rotateTag)).First().Split('=').Last());

                int scale;
                if(rotation == 90 || rotation == 270)
                {
                    scale = Media.GetAspectScale(height, width);
                    video.VideoData.XScale = Convert.ToInt16(height / scale);
                    video.VideoData.YScale = Convert.ToInt16(width / scale);
                }
                else
                {
                    scale = Media.GetAspectScale(width, height);
                    video.VideoData.XScale = Convert.ToInt16(width / scale);
                    video.VideoData.YScale = Convert.ToInt16(height / scale);
                }

                video.VideoData.Duration = Convert.ToInt32(Math.Round(duration, 0));

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
                var request = (FtpWebRequest)WebRequest.Create($"{FtpUrl}/{Website}/wwwroot/{RemoteOriginalsDir}");
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
