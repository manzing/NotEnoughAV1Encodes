﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;

namespace NotEnoughAV1Encodes.Video
{
    class VideoSplitter
    {
        private Queue.QueueElement queueElement = new();
        public void Split(Queue.QueueElement _queueElement, CancellationToken _token)
        {
            queueElement = _queueElement;
            Global.Logger("INFO  - VideoSplitter.Split()", queueElement.Output + ".log");

            if (queueElement.ChunkingMethod == 0)
            {
                // Equal Chunking
                FFmpegChunking(_token);
            }
            else if(queueElement.ChunkingMethod == 1)
            {
                // PySceneDetect
                PySceneDetect(_token);
            }
        }

        private void PySceneDetect(CancellationToken _token)
        {
            Global.Logger("DEBUG - VideoSplitter.Split() => PySceneDetect()", queueElement.Output + ".log");
            // Skip Scene Detect if the file already exist
            if (!File.Exists(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "splits.txt")))
            {
                // Detects the Scenes with PySceneDetect
                Process pySceneDetect = new();
                ProcessStartInfo startInfo = new()
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    FileName = "cmd.exe",
                    WorkingDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Apps", "pyscenedetect"),
                    Arguments = "/C scenedetect -i \"" + queueElement.VideoDB.InputPath + "\" -o \"" + Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier) + '\u0022' + " detect-content -t " + queueElement.PySceneDetectThreshold.ToString() + " list-scenes"
                };
                pySceneDetect.StartInfo = startInfo;

                pySceneDetect.Start();

                _token.Register(() => { KillProcessAndChildren(pySceneDetect.Id); });

                StreamReader sr = pySceneDetect.StandardError;

                while (!sr.EndOfStream)
                {
                    try
                    {
                        queueElement.Status = "Splitting - " + sr.ReadLine();
                    }
                    catch { }
                }

                pySceneDetect.WaitForExit();

                sr.Close();

                if (!_token.IsCancellationRequested)
                {
                    PySceneDetectParse();
                }
                else
                {
                    Global.Logger("FATAL - Cancellation Requested - Currently in VideoSplitter.Split() => PySceneDetect()", queueElement.Output + ".log");
                }
            }
            else
            {
                Global.Logger("WARN  - VideoSplitter.Split() => PySceneDetect() => File already exist - Resuming?", queueElement.Output + ".log");
            }
        }

        private void PySceneDetectParse()
        {
            // Reads first line of the csv file generated by pyscenedetect
             string line = File.ReadLines(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, Path.GetFileNameWithoutExtension(queueElement.VideoDB.InputPath) + "-Scenes.csv")).First();

            // Splits the line after "," and skips the first line, then adds the result to list
            List<string> scenes = line.Split(',').Skip(1).ToList();

            // Temporary value used for creating the ffmpeg command line
            string previousScene = "00:00:00.000";

            List<string> FFmpegArgs = new();

            // Iterates over the list of time codes and creates the args for ffmpeg
            foreach (string sc in scenes)
            {
                FFmpegArgs.Add("-ss " + previousScene + " -to " + sc);
                previousScene = sc;
            }

            // Has to be last, to "tell" ffmpeg to seek / encode until end of video
            FFmpegArgs.Add("-ss " + previousScene);

            // Writes splitting arguments to text file
            foreach (string lineArg in FFmpegArgs)
            {
                using (StreamWriter sw = File.AppendText(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "splits.txt")))
                {
                    sw.WriteLine(lineArg);
                    sw.Close();
                }
            }
        }

        private void FFmpegChunking(CancellationToken _token)
        {
            Global.Logger("DEBUG - VideoSplitter.Split() => FFmpegChunking()", queueElement.Output + ".log");
            // Skips Splitting of already existent
            if (!File.Exists(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "splitted.log")))
            {
                // Create Chunks Folder
                Directory.CreateDirectory(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "Chunks"));
                Global.Logger("DEBUG - VideoSplitter.Split() => FFmpegChunking() => Path: " + Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "Chunks"), queueElement.Output + ".log");

                // Generate Command
                string ffmpegCommand = "/C ffmpeg.exe";
                ffmpegCommand += " -y -i " + '\u0022' + queueElement.VideoDB.InputPath + '\u0022';

                // Subtitle Input for Hardcoding
                if(!string.IsNullOrEmpty(queueElement.SubtitleBurnCommand))
                {
                    // Filter not compatible with filter_complex
                    if (string.IsNullOrEmpty(queueElement.FilterCommand))
                    {
                        if (queueElement.SubtitleBurnCommand.Contains("-filter_complex"))
                        {
                            ffmpegCommand += " -i \"" + Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "Subtitles", "subs.mkv") + "\"";
                        }
                    }
                }

                ffmpegCommand += " -reset_timestamps 1 -map_metadata -1 -sn -an";

                if (queueElement.ReencodeMethod == 0)
                {
                    ffmpegCommand += " -c:v libx264 -preset ultrafast -crf 0";
                }
                else if(queueElement.ReencodeMethod == 1)
                {
                    ffmpegCommand += " -c:v ffv1 -level 3 -threads 4 -coder 1 -context 1 -slicecrc 0 -slices 4";
                }
                else if (queueElement.ReencodeMethod == 2)
                {
                    ffmpegCommand += " -c:v utvideo";
                }
                else if (queueElement.ReencodeMethod == 3)
                {
                    ffmpegCommand += " -c:v copy";
                }

                if (queueElement.ReencodeMethod != 3)
                {
                    ffmpegCommand += " -sc_threshold 0 -g " + queueElement.ChunkLength.ToString();
                    ffmpegCommand += " -force_key_frames " + '\u0022' + "expr:gte(t, n_forced * " + queueElement.ChunkLength.ToString() + ")" + '\u0022';
                    ffmpegCommand += queueElement.FilterCommand;
                    if (queueElement.SubtitleBurnCommand != null)
                    {
                        if (string.IsNullOrEmpty(queueElement.FilterCommand))
                        {
                            ffmpegCommand += queueElement.SubtitleBurnCommand;
                        }
                        else
                        {
                            // Don't want to mix filter_complex with vf
                            if (!queueElement.SubtitleBurnCommand.Contains("-filter_complex"))
                            {
                                // Prevents using "-vf" two times
                                ffmpegCommand += queueElement.SubtitleBurnCommand.Remove(0, 5);
                            }
                        }
                       
                    }
                        
                }

                ffmpegCommand += " -segment_time " + queueElement.ChunkLength.ToString() + " -f segment \"";
                ffmpegCommand += Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "Chunks", "split%6d.mkv") + "\"";

                Global.Logger("INFO  - VideoSplitter.Split() => FFmpegChunking() => FFmpeg Command: " + ffmpegCommand, queueElement.Output + ".log");

                // Start Splitting
                Process chunkingProcess = new();
                ProcessStartInfo startInfo = new()
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    FileName = "cmd.exe",
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Apps", "FFmpeg"),
                    Arguments = ffmpegCommand
                };
                chunkingProcess.StartInfo = startInfo;

                _token.Register(() => { try { chunkingProcess.StandardInput.Write("q"); } catch { } });

                // Start Process
                chunkingProcess.Start();

                // Get launched Process ID
                int tempPID = chunkingProcess.Id;

                // Add Process ID to Array, inorder to keep track / kill the instances
                Global.LaunchedPIDs.Add(tempPID);

                StreamReader sr = chunkingProcess.StandardError;
                while (!sr.EndOfStream)
                {
                    int processedFrames = Global.GetTotalFramesProcessed(sr.ReadLine());
                    if (processedFrames != 0)
                    {
                        queueElement.Progress = Convert.ToDouble(processedFrames);
                        queueElement.Status = "Splitting - " + ((decimal)queueElement.Progress / queueElement.FrameCount).ToString("0.00%");
                    }
                }

                // Wait for Exit
                chunkingProcess.WaitForExit();

                // Get Exit Code
                int exit_code = chunkingProcess.ExitCode;
                

                // Remove PID from Array after Exit
                Global.LaunchedPIDs.RemoveAll(i => i == tempPID);

                // Write Save Point
                if (chunkingProcess.ExitCode == 0 && _token.IsCancellationRequested == false)
                {
                    FileStream _finishedLog = File.Create(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "splitted.log"));
                    _finishedLog.Close();
                    Global.Logger("DEBUG - VideoSplitter.Split() => FFmpegChunking() => FFmpeg Exit Code: " + exit_code, queueElement.Output + ".log");
                }
                else
                {
                    Global.Logger("FATAL - VideoSplitter.Split() => FFmpegChunking() => FFmpeg Exit Code: " + exit_code, queueElement.Output + ".log");
                }
            }
            else
            {
                Global.Logger("WARN - VideoSplitter.Split() => FFmpegChunking() => Skipped Splitting - Resuming?", queueElement.Output + ".log");
            }
        }

        private static void KillProcessAndChildren(int pid)
        {
            ManagementObjectSearcher searcher = new ManagementObjectSearcher
              ("Select * From Win32_Process Where ParentProcessID=" + pid);
            ManagementObjectCollection moc = searcher.Get();
            foreach (ManagementObject mo in moc)
            {
                KillProcessAndChildren(Convert.ToInt32(mo["ProcessID"]));
            }
            try
            {
                Process proc = Process.GetProcessById(pid);
                proc.Kill();
            }
            catch (ArgumentException)
            {
                // Process already exited.
            }
        }
    }
}
