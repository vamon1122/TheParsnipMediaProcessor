using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Hosting;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParsnipData;
using ParsnipData.Media;

namespace ParsnipVideoCompressor
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
        static void Main(string[] args)
        {
            ProcessVideos();
            Console.WriteLine("Sleeping for 5 seconds...");
            System.Windows.Forms.Cursor.Current = System.Windows.Forms.Cursors.Default;
            Thread.Sleep(5000);
            Process.Start(AppDomain.CurrentDomain.FriendlyName);
            Environment.Exit(0);
        }

        static void ProcessVideos()
        {
            Console.WriteLine("Checking for uncompressed videos...");
            //var FileList = GetFileList();
            var selectUncompressedAttempts = 0;
            List<Video> videos = TrySelectUncompressed();
            if (videos == null)
            {
                Console.WriteLine("Unable to retrieve uncompressed videos from the database");
            }
            else
            {
                var numberOfVideos = videos.Count();
                if (numberOfVideos > 0)
                {
                    Console.WriteLine($"Processing {numberOfVideos} video(s) in the queue...");
                    Console.WriteLine();
                    var currentVideo = 0;
                    foreach (var video in videos)
                    {
                        currentVideo++;
                        var uncompressedFileName = video.VideoData.Original.Split('/').Last();
                        var downloadAttempts = 0;
                        var uploadAttempts = 0;
                        var updateDirectoryAttempts = 0;
                        Console.WriteLine($"Downloading {uncompressedFileName} ({currentVideo}/{numberOfVideos})");

                        if (TryDownload())
                        {
                            Console.WriteLine($"Compressing {uncompressedFileName}");
                            var isLandscape = video.VideoData.XScale > video.VideoData.YScale;
                            var compressedFileName = CompressVideo(uncompressedFileName, isLandscape);
                            Console.WriteLine($"Uploading file called {compressedFileName}");

                            if (TryUpload(compressedFileName))
                            {
                                Console.WriteLine("Video was uploaded. Updating database...");
                                
                                if(TryUpdateDirectories())
                                    Console.WriteLine("Database was uploaded successfully!");
                            }
                        }
                        Console.WriteLine();
                        
                        bool TryUpdateDirectories()
                        {
                            updateDirectoryAttempts++;
                            if (updateDirectoryAttempts < 4)
                            {
                                try
                                {
                                    video.UpdateDirectories();
                                }
                                catch
                                {
                                    TryUpdateDirectories();
                                }
                                return true;
                            }
                            else
                            {
                                return false;
                            }
                        }

                        bool TryUpload(string compressedFileName)
                        {
                            try
                            {
                                uploadAttempts++;
                                video.VideoData.Compressed = $"{RemoteCompressedDir}/{compressedFileName}";
                                UploadFile(video);
                                return true;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"There was an error whilst uploading the file (attempt {uploadAttempts}): {ex}");
                                if (uploadAttempts > 3)
                                {
                                    Console.WriteLine("Upload failed after {attempts} attempts. Skipping this file");
                                    return false;
                                }
                                else
                                {
                                    Console.WriteLine("Upload failed after {attempts} attempt(s). Retrying...");
                                    return TryUpload(compressedFileName);
                                }
                            }
                        }

                        bool TryDownload()
                        {
                            downloadAttempts++;
                            var fileUrl = $"{FtpUrl}/{Website}/wwwroot/{RemoteOriginalsDir}/{uncompressedFileName}";
                            var localFileDir = $"{LocalOriginalsDir}/{uncompressedFileName}";
                            long expectedFileSize = GetFileSize();
                            try
                            {
                                DownloadFile();
                                return true;
                            }
                            catch (Exception ex)
                            {
                                //Sometimes, the file finishes downloading but an exception is thrown.
                                //It's still useable so we try to convert anywam.
                                if (ex.Message == "The underlying connection was closed: An unexpected error occurred on a receive.")
                                {
                                    long downloadedFileSize = new FileInfo(localFileDir).Length;
                                    if (downloadedFileSize == expectedFileSize)
                                    {
                                        Debug.WriteLine($"There was an error whilst the file was downloading, but the downloaded file size {downloadedFileSize} " +
                                            $"matched the expected file size {expectedFileSize}. Compression will still be attempted.");

                                        return true;
                                    }
                                }
                                Console.WriteLine($"There was an error whilst downloading the file (attempt {downloadAttempts}): {ex}");
                                if (downloadAttempts > 3)
                                {
                                    Console.WriteLine("Download failed after {attempts} attempts. Skipping this file");
                                    return false;
                                }
                                else
                                {
                                    Console.WriteLine("Download failed after {attempts} attempt(s). Retrying...");
                                    return TryDownload();
                                }
                            }

                            void DownloadFile()
                            {
                                FtpWebRequest request = (FtpWebRequest)WebRequest.Create(fileUrl);
                                request.Method = WebRequestMethods.Ftp.DownloadFile;
                                request.Credentials = FtpCredentials;
                                FtpWebResponse response = (FtpWebResponse)request.GetResponse();
                                try
                                {
                                    Stream responseStream = response.GetResponseStream();
                                    using (var fileStream = File.Create(localFileDir))
                                    {
                                        responseStream.CopyTo(fileStream);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    throw ex;
                                }
                                finally
                                {
                                    response.Close();
                                }
                                Console.WriteLine($"Download Complete, status: {response.StatusDescription.Replace(System.Environment.NewLine, string.Empty)}");
                            }

                            long GetFileSize()
                            {
                                FtpWebRequest request = (FtpWebRequest)WebRequest.Create(fileUrl);
                                request.Method = WebRequestMethods.Ftp.GetFileSize;
                                request.Credentials = FtpCredentials;

                                FtpWebResponse response = (FtpWebResponse)request.GetResponse();
                                long size = response.ContentLength;
                                response.Close();

                                return size;
                            }
                        }
                    }
                    Console.WriteLine("Queue was processed successfully!");
                }
                else
                {
                    Console.WriteLine($"There were no new videos to process!");
                }
            }
            

            List<Video> TrySelectUncompressed()
            {
                selectUncompressedAttempts++;
                var uncompressedVideos = new List<Video>();
                if (selectUncompressedAttempts < 4)
                {
                    try
                    {
                        uncompressedVideos = Video.SelectUncompressed();
                    }
                    catch
                    {
                        TrySelectUncompressed();
                    }
                }
                return uncompressedVideos;
            }
        }

        static string CompressVideo(string originalFileName, bool isLandscape)
        {
            var compressedFileName = $"{originalFileName.Substring(0, originalFileName.Length - originalFileName.Split('.').Length - 1)}mp4";
            Process proc = new Process();
            if(isLandscape)
                proc.StartInfo.FileName = "CompressLandscapeVideo.bat";
            else
                proc.StartInfo.FileName = "CompressPortraitVideo.bat";
            proc.StartInfo.Arguments = $"{RelativeLocalOriginalsDir}\\{originalFileName} {RelativeLocalCompressedDir}\\{compressedFileName}";
            proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            proc.Start();
            proc.WaitForExit();
            int exitCode = proc.ExitCode;
            proc.Close();

            Console.WriteLine($"{LocalOriginalsDir}\\{originalFileName} was compressed into {LocalCompressedDir}\\{compressedFileName}");

            return compressedFileName;
        }

        public static string[] GetFileList()
        {
            string[] downloadFiles;
            StringBuilder result = new StringBuilder();
            WebResponse response = null;
            StreamReader reader = null;

            try
            {
                FtpWebRequest request = (FtpWebRequest)WebRequest.Create($"{FtpUrl}/{Website}/wwwroot/{RemoteOriginalsDir}");
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
                Console.WriteLine(((FtpWebResponse)ex.Response).StatusDescription);
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
                Console.WriteLine(ex);
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

        static void UploadFile(Video video)
        {
            // Get the object used to communicate with the server.
            FtpWebRequest ftpClient = (FtpWebRequest)WebRequest.Create(($"{FtpUrl}/{Website}/wwwroot/{video.VideoData.Compressed}"));
            ftpClient.Credentials = FtpCredentials;
            ftpClient.Method = System.Net.WebRequestMethods.Ftp.UploadFile;
            ftpClient.UseBinary = true;
            ftpClient.KeepAlive = true;
            System.IO.FileInfo fi = new System.IO.FileInfo($"{LocalCompressedDir}\\{video.VideoData.Compressed.Split('/').Last()}");
            ftpClient.ContentLength = fi.Length;
            byte[] buffer = new byte[4097];
            int bytes = 0;
            int total_bytes = (int)fi.Length;
            System.IO.FileStream fs = fi.OpenRead();
            System.IO.Stream rs = ftpClient.GetRequestStream();
            while (total_bytes > 0)
            {
                bytes = fs.Read(buffer, 0, buffer.Length);
                rs.Write(buffer, 0, bytes);
                total_bytes = total_bytes - bytes;
            }
            fs.Close();
            rs.Close();
            FtpWebResponse uploadResponse = (FtpWebResponse)ftpClient.GetResponse();
            uploadResponse.Close();
        }
    }
}
