﻿using System;
using System.Windows;
using MahApps.Metro.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using ControlzEx.Theming;
using System.Windows.Media;
using System.Linq;
using WPFLocalizeExtension.Engine;
using NotEnoughAV1Encodes.resources.lang;
using System.Windows.Shell;

namespace NotEnoughAV1Encodes
{
    public partial class MainWindow : MetroWindow
    {
        /// <summary>Prevents Race Conditions on Startup</summary>
        private bool startupLock = true;

        /// <summary>Encoding the Queue in Parallel or not</summary>
        private bool QueueParallel;

        /// <summary>State of the Program [0 = IDLE; 1 = Encoding; 2 = Paused]</summary>
        private int ProgramState;

        private Settings settingsDB = new();
        private Video.VideoDB videoDB = new();
        
        private string uid;
        private CancellationTokenSource cancellationTokenSource;
        public VideoSettings PresetSettings = new();
        public static bool Logging { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            Initialize();
            DataContext = PresetSettings;

            if (!File.Exists(Path.Combine(Global.AppData, "NEAV1E", "settings.json")))
            {
                // First Launch
                Views.FirstStartup firstStartup = new(settingsDB);
                Hide();
                firstStartup.ShowDialog();
                Show();
            }

            LocalizeDictionary.Instance.Culture = settingsDB.CultureInfo;
        }

        #region Startup
        private void Initialize()
        {
            resources.MediaLanguages.FillDictionary();

            // Load Worker Count
            int coreCount = 0;
            foreach (System.Management.ManagementBaseObject item in new System.Management.ManagementObjectSearcher("Select * from Win32_Processor").Get())
            {
                coreCount += int.Parse(item["NumberOfCores"].ToString());
            }
            for (int i = 1; i <= coreCount; i++) { ComboBoxWorkerCount.Items.Add(i); }
            ComboBoxWorkerCount.SelectedItem = Convert.ToInt32(coreCount * 75 / 100);
            TextBoxWorkerCount.Text = coreCount.ToString();

            // Load Settings from JSON
            try { settingsDB = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"))); } catch { }

            LoadSettings();

            // Load Queue
            if (Directory.Exists(Path.Combine(Global.AppData, "NEAV1E", "Queue")))
            {
                string[] filePaths = Directory.GetFiles(Path.Combine(Global.AppData, "NEAV1E", "Queue"), "*.json", SearchOption.TopDirectoryOnly);

                foreach (string file in filePaths)
                {
                    ListBoxQueue.Items.Add(JsonConvert.DeserializeObject<Queue.QueueElement>(File.ReadAllText(file)));
                }
            }

            LoadPresets();

            try { ComboBoxPresets.SelectedItem = settingsDB.DefaultPreset; } catch { }
            startupLock = false;
        }

        private void LoadPresets()
        {
            // Load Presets
            if (Directory.Exists(Path.Combine(Global.AppData, "NEAV1E", "Presets")))
            {
                string[] filePaths = Directory.GetFiles(Path.Combine(Global.AppData, "NEAV1E", "Presets"), "*.json", SearchOption.TopDirectoryOnly);

                foreach (string file in filePaths)
                {
                    ComboBoxPresets.Items.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
        }
        #endregion

        #region Buttons
        private void ButtonTestSettings_Click(object sender, RoutedEventArgs e)
        {
            Views.TestCustomSettings testCustomSettings = new(settingsDB.Theme, ComboBoxVideoEncoder.SelectedIndex, CheckBoxCustomVideoSettings.IsOn ? TextBoxCustomVideoSettings.Text : GenerateEncoderCommand());
            testCustomSettings.ShowDialog();
        }

        private void ButtonCancelEncode_Click(object sender, RoutedEventArgs e)
        {
            if (cancellationTokenSource == null) return;
            try
            {
                cancellationTokenSource.Cancel();
                ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/start.png", UriKind.Relative));
                ButtonAddToQueue.IsEnabled = true;
                ButtonRemoveSelectedQueueItem.IsEnabled = true;
                ButtonEditSelectedItem.IsEnabled = true;

                // To Do: Save Queue States when Cancelling
                // Problem: Needs VideoChunks List
                // Possible Implementation:
                //        - Use VideoChunks Functions from MainStartAsync()
                //        - Save VideoChunks inside QueueElement
                //SaveQueueElementState();
            }
            catch { }
        }

        private void ButtonProgramSettings_Click(object sender, RoutedEventArgs e)
        {
            Views.ProgramSettings programSettings = new(settingsDB);
            programSettings.ShowDialog();
            settingsDB = programSettings.settingsDBTemp;

            LoadSettings();

            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void ButtonRemoveSelectedQueueItem_Click(object sender, RoutedEventArgs e)
        {
            DeleteQueueItems();
        }

        private void QueueMenuItemOpenOutputDir_Click(object sender, RoutedEventArgs e)
        {
            if (ListBoxQueue.SelectedItem == null) return;
            try
            {
                Queue.QueueElement tmp = (Queue.QueueElement)ListBoxQueue.SelectedItem;
                string outPath = Path.GetDirectoryName(tmp.Output);
                ProcessStartInfo startInfo = new()
                {
                    Arguments = outPath,
                    FileName = "explorer.exe"
                };

                Process.Start(startInfo);
            }
            catch { }
        }

        private void ButtonOpenSource_Click(object sender, RoutedEventArgs e)
        {
            Views.OpenSource openSource = new(settingsDB.Theme);
            openSource.ShowDialog();
            if (openSource.Quit)
            {
                if (openSource.BatchFolder)
                {
                    // Check if Presets exist
                    if(ComboBoxPresets.Items.Count == 0)
                    {
                        MessageBox.Show(LocalizedStrings.Instance["MessageCreatePresetBeforeBatch"]);
                        return;
                    }

                    // Batch Folder Input
                    Views.BatchFolderDialog batchFolderDialog = new(settingsDB.Theme, openSource.Path);
                    batchFolderDialog.ShowDialog();
                    if (batchFolderDialog.Quit)
                    {
                        List<string> files =  batchFolderDialog.Files;
                        string preset = batchFolderDialog.Preset;
                        string output = batchFolderDialog.Output;
                        int container = batchFolderDialog.Container;
                        bool presetBitdepth = batchFolderDialog.PresetBitdepth;
                        bool activatesubtitles = batchFolderDialog.ActivateSubtitles;

                        string outputContainer = "";
                        if (container == 0) outputContainer = ".mkv";
                        else if (container == 1) outputContainer = ".webm";
                        else if (container == 2) outputContainer = ".mp4";

                        const string src = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
                        try
                        {
                            foreach (string file in files)
                            {
                                // Generate a random identifier to avoid filesystem conflicts
                                StringBuilder identifier = new();
                                Random RNG = new();
                                for (int i = 0; i < 15; i++)
                                {
                                    identifier.Append(src[RNG.Next(0, src.Length)]);
                                }

                                // Load Preset
                                PresetSettings = JsonConvert.DeserializeObject<VideoSettings>(File.ReadAllText(Path.Combine(Global.AppData, "NEAV1E", "Presets", preset + ".json")));
                                DataContext = PresetSettings;

                                // Create video object
                                videoDB = new();
                                videoDB.InputPath = file;

                                // Output Video
                                string outname = PresetSettings.PresetBatchName;
                                outname = outname.Replace("{filename}", Path.GetFileNameWithoutExtension(file));
                                outname = outname.Replace("{presetname}", preset);
                                videoDB.OutputPath = Path.Combine(output, outname + outputContainer);
                                videoDB.OutputFileName = Path.GetFileName(videoDB.OutputPath);
                                videoDB.ParseMediaInfo(PresetSettings);

                                try { ListBoxAudioTracks.Items.Clear(); } catch { }
                                try { ListBoxAudioTracks.ItemsSource = null; } catch { }
                                try { ListBoxSubtitleTracks.Items.Clear(); } catch { }
                                try { ListBoxSubtitleTracks.ItemsSource = null; } catch { }

                                ListBoxAudioTracks.ItemsSource = videoDB.AudioTracks;
                                ListBoxSubtitleTracks.ItemsSource = videoDB.SubtitleTracks;

                                // Automatically toggle VFR Support, if source is MKV
                                if (videoDB.MIIsVFR && Path.GetExtension(videoDB.InputPath) is ".mkv" or ".MKV")
                                {
                                    CheckBoxVideoVFR.IsEnabled = true;
                                    CheckBoxVideoVFR.IsChecked = true;
                                }
                                else
                                {
                                    CheckBoxVideoVFR.IsChecked = false;
                                    CheckBoxVideoVFR.IsEnabled = false;
                                }

                                // Uses Bit-Depth of Video
                                if (!presetBitdepth)
                                {
                                    if (videoDB.MIBitDepth == "8") ComboBoxVideoBitDepth.SelectedIndex = 0;
                                    if (videoDB.MIBitDepth == "10") ComboBoxVideoBitDepth.SelectedIndex = 1;
                                    if (videoDB.MIBitDepth == "12") ComboBoxVideoBitDepth.SelectedIndex = 2;
                                }

                                // Skip Subtitles if Container is not MKV to avoid conflicts
                                bool skipSubs = container != 0;
                                if (!activatesubtitles) skipSubs = true;

                                AddToQueue(identifier.ToString(), skipSubs);
                            }
                        }
                        catch(Exception ex)
                        {
                            MessageBox.Show(ex.Message);
                        }

                        Dispatcher.BeginInvoke((Action)(() => TabControl.SelectedIndex = 7));
                    }
                }
                else if(openSource.ProjectFile)
                {
                    // Project File Input
                    try
                    {
                        videoDB = new();
                        string file = openSource.Path;
                        Queue.QueueElement queueElement = JsonConvert.DeserializeObject<Queue.QueueElement>(File.ReadAllText(file));

                        PresetSettings = queueElement.Preset;
                        DataContext = PresetSettings;
                        videoDB = queueElement.VideoDB;

                        try { ListBoxAudioTracks.Items.Clear(); } catch { }
                        try { ListBoxAudioTracks.ItemsSource = null; } catch { }
                        try { ListBoxSubtitleTracks.Items.Clear(); } catch { }
                        try { ListBoxSubtitleTracks.ItemsSource = null; } catch { }

                        ListBoxAudioTracks.ItemsSource = videoDB.AudioTracks;
                        ListBoxSubtitleTracks.ItemsSource = videoDB.SubtitleTracks;
                        LabelVideoSource.Content = videoDB.InputPath;
                        LabelVideoDestination.Content = videoDB.OutputPath;
                        LabelVideoLength.Content = videoDB.MIDuration;
                        LabelVideoResolution.Content = videoDB.MIWidth + "x" + videoDB.MIHeight;
                        LabelVideoColorFomat.Content = videoDB.MIChromaSubsampling;

                        ComboBoxChunkingMethod.SelectedIndex = queueElement.ChunkingMethod;
                        ComboBoxReencodeMethod.SelectedIndex = queueElement.ReencodeMethod;
                        CheckBoxTwoPassEncoding.IsOn = queueElement.Passes == 2;
                        TextBoxChunkLength.Text = queueElement.ChunkLength.ToString();
                        TextBoxPySceneDetectThreshold.Text = queueElement.PySceneDetectThreshold.ToString();
                    }
                    catch(Exception ex)
                    {
                        MessageBox.Show(ex.Message);
                    }
                }
                else
                {
                    SingleFileInput(openSource.Path);
                }
            }
        }

        private void SingleFileInput(string path)
        {
            // Single File Input
            videoDB = new();
            videoDB.InputPath = path;
            videoDB.ParseMediaInfo(PresetSettings);
            LabelVideoDestination.Content = LocalizedStrings.Instance["LabelVideoDestination"];

            try { ListBoxAudioTracks.Items.Clear(); } catch { }
            try { ListBoxAudioTracks.ItemsSource = null; } catch { }
            try { ListBoxSubtitleTracks.Items.Clear(); } catch { }
            try { ListBoxSubtitleTracks.ItemsSource = null; } catch { }

            ListBoxAudioTracks.ItemsSource = videoDB.AudioTracks;
            ListBoxSubtitleTracks.ItemsSource = videoDB.SubtitleTracks;
            LabelVideoSource.Content = videoDB.InputPath;
            LabelVideoLength.Content = videoDB.MIDuration;
            LabelVideoResolution.Content = videoDB.MIWidth + "x" + videoDB.MIHeight;
            LabelVideoColorFomat.Content = videoDB.MIChromaSubsampling;
            string vfr = "";
            if (videoDB.MIIsVFR)
            {
                vfr = " (VFR)";
                if (Path.GetExtension(videoDB.InputPath) is ".mkv" or ".MKV")
                {
                    CheckBoxVideoVFR.IsEnabled = true;
                    CheckBoxVideoVFR.IsChecked = true;
                }
                else
                {
                    // VFR Video only currently supported in .mkv container
                    // Reasoning is, that splitting a VFR MP4 Video to MKV Chunks will result in ffmpeg making it CFR
                    // Additionally Copying the MP4 Video to a MKV Video will result in the same behavior, leading to incorrect extracted timestamps
                    CheckBoxVideoVFR.IsChecked = false;
                    CheckBoxVideoVFR.IsEnabled = false;
                }
            }
            LabelVideoFramerate.Content = videoDB.MIFramerate + vfr;

            // Output
            if (!string.IsNullOrEmpty(settingsDB.DefaultOutPath))
            {
                string outPath = Path.Combine(settingsDB.DefaultOutPath, Path.GetFileNameWithoutExtension(videoDB.InputPath) + ".mkv");

                videoDB.OutputPath = outPath;
                LabelVideoDestination.Content = videoDB.OutputPath;
                videoDB.OutputFileName = Path.GetFileName(videoDB.OutputPath);
            }
        }

        private void ButtonSetDestination_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveVideoFileDialog = new()
            {
                Filter = "MKV Video|*.mkv|WebM Video|*.webm|MP4 Video|*.mp4"
            };

            if (saveVideoFileDialog.ShowDialog() == true)
            {
                videoDB.OutputPath = saveVideoFileDialog.FileName;
                LabelVideoDestination.Content = videoDB.OutputPath;
                videoDB.OutputFileName = Path.GetFileName(videoDB.OutputPath);
                try
                {
                    if (Path.GetExtension(videoDB.OutputPath).ToLower() == ".mp4")
                    {
                        // Disable Subtitles if Output is MP4
                        foreach (Subtitle.SubtitleTracks subtitleTracks in ListBoxSubtitleTracks.Items)
                        {
                            subtitleTracks.Active = false;
                            subtitleTracks.Enabled = false;
                        }
                    }
                    else
                    {
                        foreach (Subtitle.SubtitleTracks subtitleTracks in ListBoxSubtitleTracks.Items)
                        {
                            subtitleTracks.Enabled = true;
                        }
                    }
                }
                catch { }
            }
        }

        private void ButtonStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (ListBoxQueue.Items.Count == 0)
            {
                PreAddToQueue();
            }

            if (ListBoxQueue.Items.Count != 0)
            {
                if (ProgramState is 0 or 2)
                {
                    ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/pause.png", UriKind.Relative));
                    LabelStartPauseButton.Content = LocalizedStrings.Instance["Pause"];

                    // Main Start
                    if (ProgramState is 0)
                    {
                        ButtonAddToQueue.IsEnabled = false;
                        ButtonRemoveSelectedQueueItem.IsEnabled = false;
                        ButtonEditSelectedItem.IsEnabled = false;

                        PreStart();
                    }

                    // Resume all PIDs
                    if (ProgramState is 2)
                    {
                        foreach (int pid in Global.LaunchedPIDs)
                        {
                            Resume.ResumeProcessTree(pid);
                        }
                    }

                    ProgramState = 1;
                }
                else if (ProgramState is 1)
                {
                    ProgramState = 2;
                    ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/resume.png", UriKind.Relative));
                    LabelStartPauseButton.Content = LocalizedStrings.Instance["Resume"];

                    // Pause all PIDs
                    foreach (int pid in Global.LaunchedPIDs)
                    {
                        Suspend.SuspendProcessTree(pid);
                    }
                }
            }
            else
            {
                MessageBox.Show(LocalizedStrings.Instance["MessageQueueEmpty"], LocalizedStrings.Instance["TabItemQueue"], MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ButtonAddToQueue_Click(object sender, RoutedEventArgs e)
        {
            PreAddToQueue();
        }

        private void PreAddToQueue()
        {
            // Prevents generating a new identifier, if queue item is being edited
            if (string.IsNullOrEmpty(uid))
            {
                // Generate a random identifier to avoid filesystem conflicts
                const string src = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
                StringBuilder identifier = new();
                Random RNG = new();
                for (int i = 0; i < 15; i++)
                {
                    identifier.Append(src[RNG.Next(0, src.Length)]);
                }
                uid = identifier.ToString();
            }

            // Add Job to Queue
            AddToQueue(uid, false);

            Dispatcher.BeginInvoke((Action)(() => TabControl.SelectedIndex = 7));

            // Reset Unique Identifier
            uid = null;
        }

        private void SaveQueueElementState(Queue.QueueElement queueElement, List<string> VideoChunks)
        {
            // Save / Override Queuefile to save Progress of Chunks

            foreach (string chunkT in VideoChunks)
            {
                // Get Index
                int index = VideoChunks.IndexOf(chunkT);

                // Already Encoded Status
                bool alreadyEncoded = File.Exists(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "Video", index.ToString("D6") + "_finished.log"));

                // Remove Chunk if not finished
                if (!alreadyEncoded)
                {
                    queueElement.ChunkProgress.RemoveAll(chunk => chunk.ChunkName == chunkT);
                }
            }

            try
            {
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "Queue", queueElement.VideoDB.InputFileName + "_" + queueElement.UniqueIdentifier + ".json"), JsonConvert.SerializeObject(queueElement, Formatting.Indented));
            }
            catch { }

        }

        private void ButtonSavePreset_Click(object sender, RoutedEventArgs e)
        {
            Views.SavePresetDialog savePresetDialog = new(settingsDB.Theme);
            savePresetDialog.ShowDialog();
            if (savePresetDialog.Quit)
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E", "Presets"));
                PresetSettings.PresetBatchName = savePresetDialog.PresetBatchName;
                PresetSettings.AudioCodecMono = savePresetDialog.AudioCodecMono;
                PresetSettings.AudioCodecStereo = savePresetDialog.AudioCodecStereo;
                PresetSettings.AudioCodecSixChannel = savePresetDialog.AudioCodecSixChannel;
                PresetSettings.AudioCodecEightChannel = savePresetDialog.AudioCodecEightChannel;
                PresetSettings.AudioBitrateMono = savePresetDialog.AudioBitrateMono;
                PresetSettings.AudioBitrateStereo = savePresetDialog.AudioBitrateStereo;
                PresetSettings.AudioBitrateSixChannel = savePresetDialog.AudioBitrateSixChannel;
                PresetSettings.AudioBitrateEightChannel = savePresetDialog.AudioBitrateEightChannel;
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "Presets", savePresetDialog.PresetName + ".json"), JsonConvert.SerializeObject(PresetSettings, Formatting.Indented));
                ComboBoxPresets.Items.Clear();
                LoadPresets();
            }
        }

        private void ButtonDeletePreset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                File.Delete(Path.Combine(Global.AppData, "NEAV1E", "Presets", ComboBoxPresets.Text + ".json"));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }

            try
            {
                ComboBoxPresets.Items.Clear();
                LoadPresets();
            }
            catch { }

        }

        private void ButtonSetPresetDefault_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                settingsDB.DefaultPreset = ComboBoxPresets.Text;
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void ButtonEditSelectedItem_Click(object sender, RoutedEventArgs e)
        {
            if (ProgramState != 0) return;

            if (ListBoxQueue.SelectedItem != null)
            {
                if (ListBoxQueue.SelectedItems.Count == 1)
                {
                    // Editing one entry
                    Queue.QueueElement tmp = (Queue.QueueElement)ListBoxQueue.SelectedItem;
                    PresetSettings = tmp.Preset;
                    DataContext = PresetSettings;
                    videoDB = tmp.VideoDB;
                    uid = tmp.UniqueIdentifier;

                    try { ListBoxAudioTracks.Items.Clear(); } catch { }
                    try { ListBoxAudioTracks.ItemsSource = null; } catch { }
                    try { ListBoxSubtitleTracks.Items.Clear(); } catch { }
                    try { ListBoxSubtitleTracks.ItemsSource = null; } catch { }

                    ListBoxAudioTracks.ItemsSource = videoDB.AudioTracks;
                    ListBoxSubtitleTracks.ItemsSource = videoDB.SubtitleTracks;
                    LabelVideoSource.Content = videoDB.InputPath;
                    LabelVideoDestination.Content = videoDB.OutputPath;
                    LabelVideoLength.Content = videoDB.MIDuration;
                    LabelVideoResolution.Content = videoDB.MIWidth + "x" + videoDB.MIHeight;
                    LabelVideoColorFomat.Content = videoDB.MIChromaSubsampling;

                    ComboBoxChunkingMethod.SelectedIndex = tmp.ChunkingMethod;
                    ComboBoxReencodeMethod.SelectedIndex = tmp.ReencodeMethod;
                    CheckBoxTwoPassEncoding.IsOn = tmp.Passes == 2;
                    TextBoxChunkLength.Text = tmp.ChunkLength.ToString();
                    TextBoxPySceneDetectThreshold.Text = tmp.PySceneDetectThreshold.ToString();

                    try
                    {
                        File.Delete(Path.Combine(Global.AppData, "NEAV1E", "Queue", tmp.VideoDB.InputFileName + "_" + tmp.UniqueIdentifier + ".json"));
                    }
                    catch { }

                    ListBoxQueue.Items.Remove(ListBoxQueue.SelectedItem);

                    Dispatcher.BeginInvoke((Action)(() => TabControl.SelectedIndex = 0));
                }
            }
        }

        private void QueueMenuItemSave_Click(object sender, RoutedEventArgs e)
        {
            if (ListBoxQueue.SelectedItem != null)
            {
                try
                {
                    Queue.QueueElement tmp = (Queue.QueueElement)ListBoxQueue.SelectedItem;
                    SaveFileDialog saveVideoFileDialog = new();
                    saveVideoFileDialog.AddExtension = true;
                    saveVideoFileDialog.Filter = "JSON File|*.json";
                    if (saveVideoFileDialog.ShowDialog() == true)
                    {
                        File.WriteAllText(saveVideoFileDialog.FileName, JsonConvert.SerializeObject(tmp, Formatting.Indented));
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void ListBoxQueue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                DeleteQueueItems();
            }
        }
        #endregion

        #region UI Functions

        private void MetroWindow_Drop(object sender, DragEventArgs e)
        {
            // Drag & Drop Video Files into GUI
            List<string> filepaths = new();
            foreach (var s in (string[])e.Data.GetData(DataFormats.FileDrop, false)) { filepaths.Add(s); }
            int counter = 0;
            foreach (var item in filepaths) { counter += 1; }
            foreach (var item in filepaths)
            {
                if (counter == 1)
                {
                    // Single File Input
                    SingleFileInput(item);
                }
            }
            if (counter > 1)
            {
                MessageBox.Show("Please use Batch Input (Drag & Drop multiple Files is not supported)");
            }
        }
        private bool presetLoadLock = false;
        private void ComboBoxPresets_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboBoxPresets.SelectedItem == null) return;
            try
            {
                presetLoadLock = true;
                PresetSettings = JsonConvert.DeserializeObject<VideoSettings>(File.ReadAllText(Path.Combine(Global.AppData, "NEAV1E", "Presets", ComboBoxPresets.SelectedItem.ToString() + ".json")));
                DataContext = PresetSettings;
                presetLoadLock = false;
            }
            catch { }
        }

        private void ComboBoxVideoEncoder_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TextBoxMaxBitrate != null)
            {
                if (ComboBoxVideoEncoder.SelectedIndex is 0 or 5)
                {
                    //aom ffmpeg
                    TextBoxMaxBitrate.Visibility = Visibility.Visible;
                    TextBoxMinBitrate.Visibility = Visibility.Visible;
                    if (ComboBoxVideoEncoder.SelectedIndex == 0)
                    {
                        SliderEncoderPreset.Maximum = 8;
                    }
                    else
                    {
                        SliderEncoderPreset.Maximum = 9;
                    }
                    SliderEncoderPreset.Value = 4;
                    SliderQuality.Maximum = 63;
                    SliderQuality.Value = 25;
                    CheckBoxTwoPassEncoding.IsEnabled = true;
					CheckBoxVBR.Visibility = Visibility.Collapsed;
					CheckBoxVBR.IsEnabled = false;
					TextBoxQmin.Visibility = Visibility.Collapsed;	
					TextBoxQmax.Visibility = Visibility.Collapsed;
                }
                else if (ComboBoxVideoEncoder.SelectedIndex is 1 or 6)
                {
                    //rav1e ffmpeg
                    TextBoxMaxBitrate.Visibility = Visibility.Collapsed;
                    TextBoxMinBitrate.Visibility = Visibility.Collapsed;
                    ComboBoxQualityMode.SelectedIndex = 0;
                    SliderEncoderPreset.Maximum = 10;
                    SliderEncoderPreset.Value = 5;
                    SliderQuality.Maximum = 255;
                    SliderQuality.Value = 80;
                    CheckBoxTwoPassEncoding.IsOn = false;
                    CheckBoxTwoPassEncoding.IsEnabled = false;
                    CheckBoxRealTimeMode.IsOn = false;
                    CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
					CheckBoxVBR.Visibility = Visibility.Collapsed;
					CheckBoxVBR.IsEnabled = false;
					TextBoxQmin.Visibility = Visibility.Collapsed;	
					TextBoxQmax.Visibility = Visibility.Collapsed;
                }
                else if (ComboBoxVideoEncoder.SelectedIndex is 2 or 7)
                {
                    //svt-av1 ffmpeg
                    TextBoxMaxBitrate.Visibility = Visibility.Collapsed;
                    TextBoxMinBitrate.Visibility = Visibility.Collapsed;
                    ComboBoxQualityMode.SelectedIndex = 0;
                    SliderEncoderPreset.Maximum = 13;
                    SliderEncoderPreset.Value = 10;
                    SliderQuality.Maximum = 63;
                    SliderQuality.Value = 40;
                    CheckBoxTwoPassEncoding.IsEnabled = true;
                    CheckBoxTwoPassEncoding.IsOn = false;
                    CheckBoxRealTimeMode.IsOn = false;
                    CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
					CheckBoxVBR.Visibility = Visibility.Collapsed;
					CheckBoxVBR.IsEnabled = false;
					TextBoxQmin.Visibility = Visibility.Collapsed;	
					TextBoxQmax.Visibility = Visibility.Collapsed;
                }
                else if (ComboBoxVideoEncoder.SelectedIndex is 3)
                {
                    //vpx-vp9 ffmpeg
                    TextBoxMaxBitrate.Visibility = Visibility.Visible;
                    TextBoxMinBitrate.Visibility = Visibility.Visible;
                    SliderEncoderPreset.Maximum = 8;
                    SliderEncoderPreset.Value = 4;
                    SliderQuality.Maximum = 63;
                    SliderQuality.Value = 25;
                    CheckBoxTwoPassEncoding.IsEnabled = true;
                    CheckBoxRealTimeMode.IsOn = false;
                    CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
					CheckBoxVBR.Visibility = Visibility.Collapsed;
					CheckBoxVBR.IsEnabled = false;
					TextBoxQmin.Visibility = Visibility.Collapsed;	
					TextBoxQmax.Visibility = Visibility.Collapsed;
                }
                else if (ComboBoxVideoEncoder.SelectedIndex is 9 or 10)
                {
                    //libx265 libx264 ffmpeg
                    TextBoxMaxBitrate.Visibility = Visibility.Collapsed;
                    TextBoxMinBitrate.Visibility = Visibility.Collapsed;
                    SliderEncoderPreset.Maximum = 9;
                    SliderEncoderPreset.Value = 4;
                    SliderQuality.Maximum = 51;
                    SliderQuality.Value = 18;
                    CheckBoxTwoPassEncoding.IsEnabled = false;
                    CheckBoxTwoPassEncoding.IsOn = false;
                    CheckBoxRealTimeMode.IsOn = false;
                    CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
					CheckBoxVBR.Visibility = Visibility.Collapsed;
					CheckBoxVBR.IsEnabled = false;
					TextBoxQmin.Visibility = Visibility.Collapsed;	
					TextBoxQmax.Visibility = Visibility.Collapsed;					
                }
				
				else if (ComboBoxVideoEncoder.SelectedIndex is 11)
                {
                    //SVT-HEVC
                    TextBoxMaxBitrate.Visibility = Visibility.Collapsed;
                    TextBoxMinBitrate.Visibility = Visibility.Collapsed;
                    SliderEncoderPreset.Maximum = 12;
                    SliderEncoderPreset.Value = 7;
                    SliderQuality.Maximum = 51;
                    SliderQuality.Value = 32;
                    CheckBoxTwoPassEncoding.IsEnabled = false;
                    CheckBoxTwoPassEncoding.IsOn = false;
                    CheckBoxRealTimeMode.IsOn = false;
                    CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
					CheckBoxVBR.Visibility = Visibility.Visible;
					CheckBoxVBR.IsEnabled = false;
					CheckBoxVBR.IsChecked = false;
					TextBoxQmin.Visibility = Visibility.Visible;	
					TextBoxQmax.Visibility = Visibility.Visible;	
					
                }
                if (ComboBoxVideoEncoder.SelectedIndex is 10)
                {
                    if (ComboBoxQualityMode.SelectedIndex == 2)
                    {
                        CheckBoxTwoPassEncoding.IsEnabled = true;
                    }
                }
				if (ComboBoxVideoEncoder.SelectedIndex is 11)
					if (ComboBoxQualityMode.SelectedIndex == 1) 
					{
                        CheckBoxVBR.IsChecked = true;
                    }
					
            }
        }

        private void CheckBoxTwoPassEncoding_Checked(object sender, RoutedEventArgs e)
        {
            if (ComboBoxVideoEncoder.SelectedIndex is 2 or 7 && ComboBoxQualityMode.SelectedIndex == 0 && CheckBoxTwoPassEncoding.IsOn)
            {
                CheckBoxTwoPassEncoding.IsOn = false;
            }

            if (CheckBoxRealTimeMode.IsOn && CheckBoxTwoPassEncoding.IsOn)
            {
                CheckBoxTwoPassEncoding.IsOn = false;
            }
        }

        private void CheckBoxRealTimeMode_Toggled(object sender, RoutedEventArgs e)
        {
            // Reverts to 1 Pass encoding if Real Time Mode is activated
            if (CheckBoxRealTimeMode.IsOn && CheckBoxTwoPassEncoding.IsOn)
            {
                CheckBoxTwoPassEncoding.IsOn = false;
            }
        }

		
        private void SliderEncoderPreset_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Shows / Hides Real Time Mode CheckBox
            if (CheckBoxRealTimeMode != null && ComboBoxVideoEncoder != null)
            {
                if (ComboBoxVideoEncoder.SelectedIndex == 0 || ComboBoxVideoEncoder.SelectedIndex == 5)
                {
                    if (SliderEncoderPreset.Value >= 5)
                    {
                        CheckBoxRealTimeMode.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        CheckBoxRealTimeMode.IsOn = false;
                        CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
                }
            }
            if (ComboBoxVideoEncoder.SelectedIndex is 9 or 10)
            {
                LabelSpeedValue.Content = GenerateMPEGEncoderSpeed();
            }
        }

        private void ComboBoxQualityMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TextBoxAVGBitrate != null)
            {
                if (ComboBoxVideoEncoder.SelectedIndex is 1 or 2 or 6 or 7 or 9 or 10 )
                {
                    if (ComboBoxQualityMode.SelectedIndex is 1 or 3)
                    {
                        ComboBoxQualityMode.SelectedIndex = 0;
                        MessageBox.Show(LocalizedStrings.Instance["MessageQualityModeRav1eSVT"]);
                        return;
                    }
                    if(CheckBoxTwoPassEncoding.IsOn && ComboBoxVideoEncoder.SelectedIndex is 2 or 7 && ComboBoxQualityMode.SelectedIndex == 0)
                    {
                        CheckBoxTwoPassEncoding.IsOn = false;
                    }
                }
                if (ComboBoxVideoEncoder.SelectedIndex is 5)
                {
                    if (ComboBoxQualityMode.SelectedIndex is 3)
                    {
                        ComboBoxQualityMode.SelectedIndex = 0;
                        MessageBox.Show(LocalizedStrings.Instance["MessageConstrainedBitrateAomenc"]);
                        return;
                    }
                }
                if (ComboBoxQualityMode.SelectedIndex == 0)
                {
                    SliderQuality.IsEnabled = true;
                    TextBoxAVGBitrate.IsEnabled = false;
                    TextBoxMaxBitrate.IsEnabled = false;
                    TextBoxMinBitrate.IsEnabled = false;
                }
                else if (ComboBoxQualityMode.SelectedIndex == 1)
                {
                    SliderQuality.IsEnabled = true;
                    TextBoxAVGBitrate.IsEnabled = false;
                    TextBoxMaxBitrate.IsEnabled = true;
                    TextBoxMinBitrate.IsEnabled = false;
                }
                else if (ComboBoxQualityMode.SelectedIndex == 2)
                {
                    SliderQuality.IsEnabled = false;
                    TextBoxAVGBitrate.IsEnabled = true;
                    TextBoxMaxBitrate.IsEnabled = false;
                    TextBoxMinBitrate.IsEnabled = false;
                }
                else if (ComboBoxQualityMode.SelectedIndex == 3)
                {
                    SliderQuality.IsEnabled = false;
                    TextBoxAVGBitrate.IsEnabled = true;
                    TextBoxMaxBitrate.IsEnabled = true;
                    TextBoxMinBitrate.IsEnabled = true;
                }
                if (ComboBoxVideoEncoder.SelectedIndex is 10 && ComboBoxQualityMode.SelectedIndex == 2)
                {
                    CheckBoxTwoPassEncoding.IsEnabled = true;
                }
                else if(ComboBoxVideoEncoder.SelectedIndex is 10 && ComboBoxQualityMode.SelectedIndex != 2)
                {
                    CheckBoxTwoPassEncoding.IsEnabled = false;
                }
				if (ComboBoxVideoEncoder.SelectedIndex is 11 && ComboBoxQualityMode.SelectedIndex == 1) 
				{
                    CheckBoxVBR.IsChecked = true;
					TextBoxQmin.IsEnabled = true;
                    TextBoxQmax.IsEnabled = true;
					SliderQuality.IsEnabled = false;
                }
				else
				{
                    CheckBoxVBR.IsChecked = false;
					TextBoxQmin.IsEnabled = false;
                    TextBoxQmax.IsEnabled = false;
                }	
            }
        }

        private void ComboBoxVideoBitDepth_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboBoxVideoEncoder.SelectedIndex == 10 && ComboBoxVideoBitDepth.SelectedIndex == 2)
            {
                ComboBoxVideoBitDepth.SelectedIndex = 1;
            }
        }

        private void CheckBoxCustomVideoSettings_Toggled(object sender, RoutedEventArgs e)
        {
            if (CheckBoxCustomVideoSettings.IsOn && presetLoadLock == false)
            {
                TextBoxCustomVideoSettings.Text = GenerateEncoderCommand();
            }
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            // Validates that the TextBox Input are only numbers
            Regex regex = new("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void TextBoxCustomVideoSettings_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Verifies the arguments the user inputs into the encoding settings textbox
            // If the users writes a "forbidden" argument, it will display the text red
            string[] forbiddenWords = { "help", "cfg", "debug", "output", "passes", "pass", "fpf", "limit",
            "skip", "webm", "ivf", "obu", "q-hist", "rate-hist", "fullhelp", "benchmark", "first-pass", "second-pass",
            "reconstruction", "enc-mode-2p", "input-stat-file", "output-stat-file" };

            foreach (string word in forbiddenWords)
            {
                if (settingsDB.BaseTheme == 0)
                {
                    // Lightmode
                    TextBoxCustomVideoSettings.Foreground = new SolidColorBrush(Color.FromRgb(0, 0, 0));
                }
                else
                {
                    // Darkmode
                    TextBoxCustomVideoSettings.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                }

                if (TextBoxCustomVideoSettings.Text.Contains(word))
                {
                    TextBoxCustomVideoSettings.Foreground = new SolidColorBrush(Color.FromRgb(255, 0, 0));
                    break;
                }
            }
        }
        #endregion

        #region Small Functions

        private void ComboBoxChunkingMethod_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (startupLock) return;
            settingsDB.ChunkingMethod = ComboBoxChunkingMethod.SelectedIndex;
            settingsDB.ReencodeMethod = ComboBoxReencodeMethod.SelectedIndex;
            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void TextBoxChunkLength_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (startupLock) return;
            settingsDB.ChunkLength = TextBoxChunkLength.Text;
            settingsDB.PySceneDetectThreshold = TextBoxPySceneDetectThreshold.Text;
            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void ComboBoxWorkerCount_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (startupLock) return;
            if (settingsDB.OverrideWorkerCount) return;
            settingsDB.WorkerCount = ComboBoxWorkerCount.SelectedIndex;
            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void TextBoxWorkerCount_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (startupLock) return;
            if (!settingsDB.OverrideWorkerCount) return;
            settingsDB.WorkerCount = int.Parse(TextBoxWorkerCount.Text);
            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void ToggleSwitchQueueParallel_Toggled(object sender, RoutedEventArgs e)
        {
            if (startupLock) return;
            settingsDB.QueueParallel = ToggleSwitchQueueParallel.IsOn;
            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void DeleteQueueItems()
        {
            if (ListBoxQueue.SelectedItem == null) return;
            if (ProgramState != 0) return;
            if (ListBoxQueue.SelectedItems.Count > 1)
            {
                List<Queue.QueueElement> items = ListBoxQueue.SelectedItems.OfType<Queue.QueueElement>().ToList();
                foreach (var item in items)
                {
                    ListBoxQueue.Items.Remove(item);
                    try
                    {
                        File.Delete(Path.Combine(Global.AppData, "NEAV1E", "Queue", item.VideoDB.InputFileName + "_" + item.UniqueIdentifier + ".json"));
                    }
                    catch { }
                }
            }
            else
            {
                Queue.QueueElement tmp = (Queue.QueueElement)ListBoxQueue.SelectedItem;
                ListBoxQueue.Items.Remove(ListBoxQueue.SelectedItem);
                try
                {
                    File.Delete(Path.Combine(Global.AppData, "NEAV1E", "Queue", tmp.VideoDB.InputFileName + "_" + tmp.UniqueIdentifier + ".json"));
                }
                catch { }
            }
        }

        private void LoadSettings()
        {
            if (settingsDB.OverrideWorkerCount)
            {
                ComboBoxWorkerCount.Visibility = Visibility.Hidden;
                TextBoxWorkerCount.Visibility = Visibility.Visible;
                if (settingsDB.WorkerCount != 99999999)
                    TextBoxWorkerCount.Text = settingsDB.WorkerCount.ToString();
            }
            else
            {
                ComboBoxWorkerCount.Visibility = Visibility.Visible;
                TextBoxWorkerCount.Visibility = Visibility.Hidden;
                if (settingsDB.WorkerCount != 99999999)
                    ComboBoxWorkerCount.SelectedIndex = settingsDB.WorkerCount;
            }

            ComboBoxChunkingMethod.SelectedIndex = settingsDB.ChunkingMethod;
            ComboBoxReencodeMethod.SelectedIndex = settingsDB.ReencodeMethod;
            TextBoxChunkLength.Text = settingsDB.ChunkLength;
            TextBoxPySceneDetectThreshold.Text = settingsDB.PySceneDetectThreshold;
            ToggleSwitchQueueParallel.IsOn = settingsDB.QueueParallel;

            // Sets Temp Path
            Global.Temp = settingsDB.TempPath;
            Logging = settingsDB.Logging;

            // Set Theme
            try
            {
                ThemeManager.Current.ChangeTheme(this, settingsDB.Theme);
            }
            catch { }
            try
            {
                if (settingsDB.BGImage != null)
                {
                    Uri fileUri = new(settingsDB.BGImage);
                    bgImage.Source = new BitmapImage(fileUri);

                    SolidColorBrush bg = new(Color.FromArgb(150, 100, 100, 100));
                    SolidColorBrush fg = new(Color.FromArgb(180, 100, 100, 100));
                    if (settingsDB.BaseTheme == 1)
                    {
                        // Dark
                        bg = new(Color.FromArgb(150, 20, 20, 20));
                        fg = new(Color.FromArgb(180, 20, 20, 20));
                    }

                    TabControl.Background = bg;
                    ListBoxAudioTracks.Background = fg;
                    ListBoxSubtitleTracks.Background = fg;
                }
                else
                {
                    bgImage.Source = null;
                }
            }
            catch { }
        }

        private void AddToQueue(string identifier, bool skipSubs)
        {
            if (string.IsNullOrEmpty(videoDB.InputPath))
            {
                // Throw Error
                MessageBox.Show(LocalizedStrings.Instance["MessageNoInput"], LocalizedStrings.Instance["Error"], MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrEmpty(videoDB.OutputPath))
            {
                // Throw Error
                MessageBox.Show(LocalizedStrings.Instance["MessageNoOutput"], LocalizedStrings.Instance["Error"], MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Queue.QueueElement queueElement = new();
            Audio.CommandGenerator audioCommandGenerator = new();
            Subtitle.CommandGenerator subCommandGenerator = new();

            queueElement.UniqueIdentifier = identifier;
            queueElement.Input = videoDB.InputPath;
            queueElement.Output = videoDB.OutputPath;
            queueElement.VideoCommand = CheckBoxCustomVideoSettings.IsOn ? TextBoxCustomVideoSettings.Text : GenerateEncoderCommand();
            queueElement.VideoHDRMuxCommand = GenerateMKVMergeHDRCommand();
            queueElement.AudioCommand = audioCommandGenerator.Generate(ListBoxAudioTracks.Items);
            queueElement.SubtitleCommand = skipSubs ? null : subCommandGenerator.GenerateSoftsub(ListBoxSubtitleTracks.Items);
            queueElement.SubtitleBurnCommand = subCommandGenerator.GenerateHardsub(ListBoxSubtitleTracks.Items, identifier);
            queueElement.FilterCommand = GenerateVideoFilters();
            queueElement.FrameCount = videoDB.MIFrameCount;
            queueElement.EncodingMethod = ComboBoxVideoEncoder.SelectedIndex;
            queueElement.ChunkingMethod = ComboBoxChunkingMethod.SelectedIndex;
            queueElement.ReencodeMethod = ComboBoxReencodeMethod.SelectedIndex;
            queueElement.Passes = CheckBoxTwoPassEncoding.IsOn ? 2 : 1;
            queueElement.ChunkLength = int.Parse(TextBoxChunkLength.Text);
            queueElement.PySceneDetectThreshold = float.Parse(TextBoxPySceneDetectThreshold.Text);
            queueElement.VFR = CheckBoxVideoVFR.IsChecked == true;
            queueElement.Preset = PresetSettings;
            queueElement.VideoDB = videoDB;

            if (ToggleSwitchFilterDeinterlace.IsOn && ComboBoxFiltersDeinterlace.SelectedIndex is 1 or 2)
            {
                queueElement.FrameCount += queueElement.FrameCount;
            }

            // Add to Queue
            ListBoxQueue.Items.Add(queueElement);

            Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E", "Queue"));

            // Save as JSON
            File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "Queue", videoDB.InputFileName + "_" + identifier + ".json"), JsonConvert.SerializeObject(queueElement, Formatting.Indented));
        }

        private void AutoPauseResume()
        {
            TimeSpan idleTime = win32.IdleDetection.GetInputIdleTime();
            double time = idleTime.TotalSeconds;
            
            Debug.WriteLine("AutoPauseResume() => " + time.ToString() + " Seconds");
            if (ProgramState is 1)
            {
                // Pause
                if (time < 40.0)
                {
                    Dispatcher.Invoke(() => ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/resume.png", UriKind.Relative)));
                    Dispatcher.Invoke(() => LabelStartPauseButton.Content = LocalizedStrings.Instance["Resume"]);
                    Dispatcher.Invoke(() => Title = "NEAV1E - " + LocalizedStrings.Instance["ToggleSwitchAutoPauseResume"] + " => Paused");

                    // Pause all PIDs
                    foreach (int pid in Global.LaunchedPIDs)
                    {
                        Suspend.SuspendProcessTree(pid);
                    }

                    ProgramState = 2;
                }
            }
            else if (ProgramState is 2)
            {
                Dispatcher.Invoke(() => Title = "NEAV1E - " + LocalizedStrings.Instance["ToggleSwitchAutoPauseResume"] + " => Paused - System IDLE since " + time.ToString() + " seconds");
                // Resume
                if (time > 60.0)
                {
                    Dispatcher.Invoke(() => ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/pause.png", UriKind.Relative)));
                    Dispatcher.Invoke(() => LabelStartPauseButton.Content = LocalizedStrings.Instance["Pause"]);
                    Dispatcher.Invoke(() => Title = "NEAV1E - " + LocalizedStrings.Instance["ToggleSwitchAutoPauseResume"] + " => Encoding");

                    // Resume all PIDs
                    if (ProgramState is 2)
                    {
                        foreach (int pid in Global.LaunchedPIDs)
                        {
                            Resume.ResumeProcessTree(pid);
                        }
                    }

                    ProgramState = 1;
                }
            }
        }

        private void Shutdown()
        {
            if (settingsDB.ShutdownAfterEncode)
            {
                Process.Start("shutdown.exe", "/s /t 0");
            }
        }

        private void DeleteTempFiles(Queue.QueueElement queueElement, DateTime startTime)
        {
            if (!File.Exists(queueElement.VideoDB.OutputPath)) {
                queueElement.Status = "Error: No Output detected";
                return;
            }

            FileInfo videoOutput = new(queueElement.VideoDB.OutputPath);
            if (videoOutput.Length <= 50000) {
                queueElement.Status = "Possible Muxing Error";
                return;
            }

            TimeSpan timespent = DateTime.Now - startTime;
            try {
                queueElement.Status = "Finished Encoding - Elapsed Time " + timespent.ToString("hh\\:mm\\:ss") + " - avg " + Math.Round(queueElement.FrameCount / timespent.TotalSeconds, 2) + "fps";
            }
            catch
            {
                queueElement.Status = "Finished Encoding - Elapsed Time " + timespent.ToString("hh\\:mm\\:ss") + " - Error calculating average FPS";
            }


            if (settingsDB.DeleteTempFiles) {
                try {
                    DirectoryInfo tmp = new(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier));
                    tmp.Delete(true);
                } catch {
                    queueElement.Status = "Error Deleting Temp Files";
                }
            }
        }
        #endregion

        #region Video Filters
        private string GenerateVideoFilters()
        {
            bool crop = ToggleSwitchFilterCrop.IsOn;
            bool rotate = ToggleSwitchFilterRotate.IsOn;
            bool resize = ToggleSwitchFilterResize.IsOn;
            bool deinterlace = ToggleSwitchFilterDeinterlace.IsOn;
            bool _oneFilter = false;

            string FilterCommand = "";

            if (crop || rotate || resize || deinterlace)
            {
                FilterCommand = " -vf ";
                if (resize)
                {
                    // Has to be last, due to scaling algorithm
                    FilterCommand += VideoFiltersResize();
                    _oneFilter = true;
                }
                if (crop)
                {
                    if (_oneFilter) { FilterCommand += ","; }
                    FilterCommand += VideoFiltersCrop();
                    _oneFilter = true;
                }
                if (rotate)
                {
                    if (_oneFilter) { FilterCommand += ","; }
                    FilterCommand += VideoFiltersRotate();
                    _oneFilter = true;
                }
                if (deinterlace)
                {
                    if (_oneFilter) { FilterCommand += ","; }
                    FilterCommand += VideoFiltersDeinterlace();
                }
            }

            return FilterCommand;
        }

        private string VideoFiltersCrop()
        {
            // Sets the values for cropping the video
            string widthNew = (int.Parse(TextBoxFiltersCropRight.Text) + int.Parse(TextBoxFiltersCropLeft.Text)).ToString();
            string heightNew = (int.Parse(TextBoxFiltersCropTop.Text) + int.Parse(TextBoxFiltersCropBottom.Text)).ToString();
            return "crop=iw-" + widthNew + ":ih-" + heightNew + ":" + TextBoxFiltersCropLeft.Text + ":" + TextBoxFiltersCropTop.Text;
        }

        private string VideoFiltersRotate()
        {
            // Sets the values for rotating the video
            if (ComboBoxFiltersRotate.SelectedIndex == 1) return "transpose=1";
            else if (ComboBoxFiltersRotate.SelectedIndex == 2) return "transpose=2,transpose=2";
            else if (ComboBoxFiltersRotate.SelectedIndex == 3) return "transpose=2";
            else return ""; // If user selected no ratation but still has it enabled
        }

        private string VideoFiltersDeinterlace()
        {
            int filterIndex = ComboBoxFiltersDeinterlace.SelectedIndex;
            string filter = "";

            if (filterIndex == 0)
            {
                filter = "bwdif=mode=0";
            }
            else if (filterIndex == 1)
            {
                filter = "estdif=mode=0";
            }
            else if (filterIndex == 2)
            {
                string bin = Path.Combine(Directory.GetCurrentDirectory(), "Apps", "nnedi", "nnedi3_weights.bin");
                bin = bin.Replace("\u005c", "\u005c\u005c").Replace(":", "\u005c:");
                filter = "nnedi=weights='" + bin + "'";
            }
            else if (filterIndex == 3)
            {
                filter = "yadif=mode=0";
            }

            return filter;
        }

        private string VideoFiltersResize()
        {
            // Sets the values for scaling the video
            if (TextBoxFiltersResizeWidth.Text != "0")
            {
                // Custom Scale
                return "scale=" + TextBoxFiltersResizeWidth.Text + ":" + TextBoxFiltersResizeHeight.Text + ":flags=" + ComboBoxResizeAlgorithm.Text;
            }
            // Auto Scale
            return "scale=trunc(oh*a/2)*2:" + TextBoxFiltersResizeHeight.Text + ":flags=" + ComboBoxResizeAlgorithm.Text;
        }
        #endregion

        #region Encoder Settings
        private string GenerateEncoderCommand()
        {
            string settings = GenerateFFmpegColorSpace() + " " + GenerateFFmpegFramerate() + " ";

            string encoderSetting = ComboBoxVideoEncoder.SelectedIndex switch
            {
                0 => GenerateAomFFmpegCommand(),
                1 => GenerateRav1eFFmpegCommand(),
                2 => GenerateSvtAV1FFmpegCommand(),
                3 => GenerateVpxVP9Command(),
                5 => GenerateAomencCommand(),
                6 => GenerateRav1eCommand(),
                7 => GenerateSvtAV1Command(),
                9 => GenerateHEVCFFmpegCommand(),
                10 => GenerateAVCFFmpegCommand(),
				11 => GenerateSvtHevcCommand(),
                _ => ""
            };

            return settings + encoderSetting;
        }

        private string GenerateAomFFmpegCommand()
        {
            string settings = "-c:v libaom-av1";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityMode.SelectedIndex switch
            {
                0 => " -crf " + SliderQuality.Value + " -b:v 0",
                1 => " -crf " + SliderQuality.Value + " -b:v " + TextBoxMaxBitrate.Text + "k",
                2 => " -b:v " + TextBoxMinBitrate.Text + "k",
                3 => " -minrate " + TextBoxMinBitrate.Text + "k -b:v " + TextBoxAVGBitrate.Text + "k -maxrate " + TextBoxMaxBitrate.Text + "k",
                _ => ""
            };

            // Preset
            settings += quality + " -cpu-used " + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " -threads 4 -tile-columns 2 -tile-rows 1 -g " + GenerateKeyFrameInerval();
            }
            else
            {
                settings += " -threads " + ComboBoxAomencThreads.Text +                                      // Threads
                            " -tile-columns " + ComboBoxAomencTileColumns.Text +                             // Tile Columns
                            " -tile-rows " + ComboBoxAomencTileRows.Text +                                   // Tile Rows
                            " -lag-in-frames " + TextBoxAomencLagInFrames.Text +                             // Lag in Frames
                            " -aq-mode " + ComboBoxAomencAQMode.SelectedIndex +                              // AQ-Mode
                            " -tune " + ComboBoxAomencTune.Text;                                             // Tune

                if (TextBoxAomencMaxGOP.Text != "0") 
                    settings += " -g " + TextBoxAomencMaxGOP.Text;                                           // Keyframe Interval
                if (CheckBoxAomencRowMT.IsChecked == false) 
                    settings += " -row-mt 0";                                                                // Row Based Multithreading
                if (CheckBoxAomencCDEF.IsChecked == false) 
                    settings += " -enable-cdef 0";                                                           // Constrained Directional Enhancement Filter
                if (CheckBoxRealTimeMode.IsOn) 
                    settings += " -usage realtime ";                                                         // Real Time Mode

                if (CheckBoxAomencARNRMax.IsChecked == true)
                {
                    settings += " -arnr-max-frames " + ComboBoxAomencARNRMax.Text;                           // ARNR Maxframes
                    settings += " -arnr-strength " + ComboBoxAomencARNRStrength.Text;                        // ARNR Strength
                }

                settings += " -aom-params " +
                            " tune-content=" + ComboBoxAomencTuneContent.Text +                              // Tune-Content
                            ":sharpness=" + ComboBoxAomencSharpness.Text +                                   // Sharpness (Filter)
                            ":enable-keyframe-filtering=" + ComboBoxAomencKeyFiltering.SelectedIndex;        // Key Frame Filtering

                if (ComboBoxAomencColorPrimaries.SelectedIndex != 0)
                    settings += ":color-primaries=" + ComboBoxAomencColorPrimaries.Text;                     // Color Primaries
                if (ComboBoxAomencColorTransfer.SelectedIndex != 0)
                    settings += ":transfer-characteristics=" + ComboBoxAomencColorTransfer.Text;             // Color Transfer
                if (ComboBoxAomencColorMatrix.SelectedIndex != 0)
                    settings += ":matrix-coefficients=" + ComboBoxAomencColorMatrix.Text;                    // Color Matrix
            }

            return settings;
        }

        private string GenerateRav1eFFmpegCommand()
        {
            string settings = "-c:v librav1e";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityMode.SelectedIndex switch
            {
                0 => " -qp " + SliderQuality.Value,
                2 => " -b:v " + TextBoxAVGBitrate.Text + "k",
                _ => ""
            };

            // Preset
            settings += quality + " -speed " + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " -tile-columns 2 -tile-rows 1 -g " + GenerateKeyFrameInerval() + " -rav1e-params threads=4";
            }
            else
            {
                settings += " -tile-columns " + ComboBoxRav1eTileColumns.SelectedIndex +                     // Tile Columns
                            " -tile-rows " + ComboBoxRav1eTileRows.SelectedIndex;                            // Tile Rows

                settings += " -rav1e-params " +
                            "threads=" + ComboBoxRav1eThreads.SelectedIndex +                                // Threads
                            ":rdo-lookahead-frames=" + TextBoxRav1eLookahead.Text +                          // RDO Lookahead
                            ":tune=" + ComboBoxRav1eTune.Text;                                               // Tune

                if (TextBoxRav1eMaxGOP.Text != "0") 
                    settings += ":keyint=" + TextBoxRav1eMaxGOP.Text;                                        // Keyframe Interval

                if (ComboBoxRav1eColorPrimaries.SelectedIndex != 0) 
                    settings += ":primaries=" + ComboBoxRav1eColorPrimaries.Text;                            // Color Primaries
                if (ComboBoxRav1eColorTransfer.SelectedIndex != 0)
                    settings += ":transfer=" + ComboBoxRav1eColorTransfer.Text;                              // Color Transfer
                if (ComboBoxRav1eColorMatrix.SelectedIndex != 0)
                    settings += ":matrix=" + ComboBoxRav1eColorMatrix.Text;                                  // Color Matrix
            }

            return settings;
        }

        private string GenerateSvtAV1FFmpegCommand()
        {
            string settings = "-c:v libsvtav1";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityMode.SelectedIndex switch
            {
                0 => " -rc 0 -qp " + SliderQuality.Value,
                2 => " -rc 1 -b:v " + TextBoxAVGBitrate.Text + "k",
                _ => ""
            };

            // Preset
            settings += quality + " -preset " + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " -g " + GenerateKeyFrameInerval();
            }
            else
            {
                settings += " -tile_columns " + ComboBoxSVTAV1TileColumns.Text +                             // Tile Columns
                            " -tile_rows " + ComboBoxSVTAV1TileRows.Text +                                   // Tile Rows
                            " -g " + TextBoxSVTAV1MaxGOP.Text +                                              // Keyframe Interval
                            " -la_depth " + TextBoxSVTAV1Lookahead.Text;                                     // Lookahead
            }

            return settings;
        }

        private string GenerateVpxVP9Command()
        {
            string settings = "-c:v libvpx-vp9";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityMode.SelectedIndex switch
            {
                0 => " -crf " + SliderQuality.Value + " -b:v 0",
                1 => " -crf " + SliderQuality.Value + " -b:v " + TextBoxMaxBitrate.Text + "k",
                2 => " -b:v " + TextBoxMinBitrate.Text + "k",
                3 => " -minrate " + TextBoxMinBitrate.Text + "k -b:v " + TextBoxAVGBitrate.Text + "k -maxrate " + TextBoxMaxBitrate.Text + "k",
                _ => ""
            };

            // Preset
            settings += quality + " -cpu-used " + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " -threads 4 -tile-columns 2 -tile-rows 1 -g " + GenerateKeyFrameInerval();
            }
            else
            {
                settings += " -threads " + ComboBoxVP9Threads.Text +                                         // Max Threads
                            " -tile-columns " + ComboBoxVP9TileColumns.SelectedIndex +                       // Tile Columns
                            " -tile-rows " + ComboBoxVP9TileRows.SelectedIndex +                             // Tile Rows
                            " -lag-in-frames " + TextBoxVP9LagInFrames.Text +                                // Lag in Frames
                            " -g " + TextBoxVP9MaxKF.Text +                                                  // Max GOP
                            " -aq-mode " + ComboBoxVP9AQMode.SelectedIndex +                                 // AQ-Mode
                            " -tune " + ComboBoxVP9ATune.SelectedIndex +                                     // Tune
                            " -tune-content " + ComboBoxVP9ATuneContent.SelectedIndex;                       // Tune-Content

                if (CheckBoxVP9ARNR.IsChecked == true)
                {
                    settings += " -arnr-maxframes " + ComboBoxAomencVP9Max.Text +                            // ARNR Max Frames
                                " -arnr-strength " + ComboBoxAomencVP9Strength.Text +                        // ARNR Strength
                                " -arnr-type " + ComboBoxAomencVP9ARNRType.Text;                             // ARNR Type
                }
            }

            return settings;
        }

        private string GenerateAomencCommand()
        {
            string settings = "-f yuv4mpegpipe - | " +
                              "\"" + Path.Combine(Directory.GetCurrentDirectory(), "Apps", "aomenc", "aomenc.exe") + "\" -";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityMode.SelectedIndex switch
            {
                0 => " --cq-level=" + SliderQuality.Value + " --end-usage=q",
                1 => " --cq-level=" + SliderQuality.Value + " --target-bitrate=" + TextBoxMaxBitrate.Text + " --end-usage=cq",
                2 => " --target-bitrate=" + TextBoxMinBitrate.Text + " --end-usage=vbr",
                _ => ""
            };

            // Preset
            settings += quality + " --cpu-used=" + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " --threads=4 --tile-columns=2 --tile-rows=1 --kf-max-dist=" + GenerateKeyFrameInerval();
            }
            else
            {
                settings += " --threads=" + ComboBoxAomencThreads.Text +                                     // Threads
                            " --tile-columns=" + ComboBoxAomencTileColumns.Text +                            // Tile Columns
                            " --tile-rows=" + ComboBoxAomencTileRows.Text +                                  // Tile Rows
                            " --lag-in-frames=" + TextBoxAomencLagInFrames.Text +                            // Lag in Frames
                            " --sharpness=" + ComboBoxAomencSharpness.Text +                                 // Sharpness (Filter)
                            " --aq-mode=" + ComboBoxAomencAQMode.SelectedIndex +                             // AQ-Mode
                            " --enable-keyframe-filtering=" + ComboBoxAomencKeyFiltering.SelectedIndex +     // Key Frame Filtering
                            " --tune=" + ComboBoxAomencTune.Text +                                           // Tune
                            " --tune-content=" + ComboBoxAomencTuneContent.Text;                             // Tune-Content

                if (TextBoxAomencMaxGOP.Text != "0")
                    settings += " --kf-max-dist=" + TextBoxAomencMaxGOP.Text;                                // Keyframe Interval
                if (CheckBoxAomencRowMT.IsChecked == false)
                    settings += " --row-mt=0";                                                               // Row Based Multithreading

                if (ComboBoxAomencColorPrimaries.SelectedIndex != 0)
                    settings += " --color-primaries=" + ComboBoxAomencColorPrimaries.Text;                   // Color Primaries
                if (ComboBoxAomencColorTransfer.SelectedIndex != 0)
                    settings += " --transfer-characteristics=" + ComboBoxAomencColorTransfer.Text;           // Color Transfer
                if (ComboBoxAomencColorMatrix.SelectedIndex != 0)
                    settings += " --matrix-coefficients=" + ComboBoxAomencColorMatrix.Text;                  // Color Matrix

                if (CheckBoxAomencCDEF.IsChecked == false)
                    settings += " --enable-cdef=0";                                                          // Constrained Directional Enhancement Filter

                if (CheckBoxAomencARNRMax.IsChecked == true)
                {
                    settings += " --arnr-maxframes=" + ComboBoxAomencARNRMax.Text;                           // ARNR Maxframes
                    settings += " --arnr-strength=" + ComboBoxAomencARNRStrength.Text;                       // ARNR Strength
                }

                if (CheckBoxRealTimeMode.IsOn)
                    settings += " --rt";                                                                     // Real Time Mode
            }

            return settings;
        }

        private string GenerateRav1eCommand()
        {
            string settings = "-f yuv4mpegpipe - | " +
                               "\"" + Path.Combine(Directory.GetCurrentDirectory(), "Apps", "rav1e", "rav1e.exe") + "\" - -y";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityMode.SelectedIndex switch
            {
                0 => " --quantizer " + SliderQuality.Value,
                2 => " --bitrate " + TextBoxAVGBitrate.Text,
                _ => ""
            };

            // Preset
            settings += quality + " --speed " + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " --threads 4 --tile-cols 2 --tile-rows 1 --keyint " + GenerateKeyFrameInerval();
            }
            else
            {
                settings += " --threads " + ComboBoxRav1eThreads.SelectedIndex +                             // Threads
                            " --tile-cols " + ComboBoxRav1eTileColumns.SelectedIndex +                       // Tile Columns
                            " --tile-rows " + ComboBoxRav1eTileRows.SelectedIndex +                          // Tile Rows
                            " --rdo-lookahead-frames " + TextBoxRav1eLookahead.Text +                        // RDO Lookahead
                            " --tune " + ComboBoxRav1eTune.Text;                                             // Tune

                if (TextBoxRav1eMaxGOP.Text != "0")
                    settings += " --keyint " + TextBoxRav1eMaxGOP.Text;                                      // Keyframe Interval

                if (ComboBoxRav1eColorPrimaries.SelectedIndex != 0)
                    settings += " --primaries " + ComboBoxRav1eColorPrimaries.Text;                          // Color Primaries
                if (ComboBoxRav1eColorTransfer.SelectedIndex != 0)
                    settings += " --transfer " + ComboBoxRav1eColorTransfer.Text;                            // Color Transfer
                if (ComboBoxRav1eColorMatrix.SelectedIndex != 0)
                    settings += " --matrix " + ComboBoxRav1eColorMatrix.Text;                                // Color Matrix
            }

            return settings;
        }

        private string GenerateSvtAV1Command()
        {
            string settings = "-nostdin -f yuv4mpegpipe - | " +
                              "\"" + Path.Combine(Directory.GetCurrentDirectory(), "Apps", "svt-av1", "SvtAv1EncApp.exe") + "\" -i stdin";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityMode.SelectedIndex switch
            {
                0 => " --rc 0 --crf " + SliderQuality.Value,
                2 => " --rc 1 --tbr " + TextBoxAVGBitrate.Text,
                _ => ""
            };

            // Preset
            settings += quality +" --preset " + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " --keyint " + GenerateKeyFrameInerval();

            }
            else
            {
                settings += " --tile-columns " + ComboBoxSVTAV1TileColumns.Text +                            // Tile Columns
                            " --tile-rows " + ComboBoxSVTAV1TileRows.Text +                                  // Tile Rows
                            " --keyint " + TextBoxSVTAV1MaxGOP.Text +                                        // Keyframe Interval
                            " --lookahead " + TextBoxSVTAV1Lookahead.Text;                                   // Lookahead
            }

            return settings;
        }

        private string GenerateHEVCFFmpegCommand()
        {
            string settings = "-c:v libx265";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityMode.SelectedIndex switch
            {
                0 => " -crf " + SliderQuality.Value,
                2 => " -b:v " + TextBoxAVGBitrate.Text + "k",
                _ => ""
            };

            // Preset
            settings += quality + " -preset " + GenerateMPEGEncoderSpeed();

            return settings;
        }

        private string GenerateAVCFFmpegCommand()
        {
            string settings = "-c:v libx264";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityMode.SelectedIndex switch
            {
                0 => " -crf " + SliderQuality.Value,
                2 => " -b:v " + TextBoxAVGBitrate.Text + "k",
                _ => ""
            };

            // Preset
            settings += quality + " -preset " + GenerateMPEGEncoderSpeed();

            return settings;
        }
		
		private string GenerateSvtHevcCommand()
        {
            string settings = "-c:v libsvt_hevc";
			
			               
            // Quality / Bitrate Selection
            string quality = ComboBoxQualityMode.SelectedIndex switch
            {
                0 => " -rc 0 -qp " + SliderQuality.Value,
				1 => " -rc 1 -qmin " + TextBoxQmin.Text + " -qmax " + TextBoxQmax.Text,
                2 => " -rc 0 -b:v " + TextBoxAVGBitrate.Text,
				3 => " -rc 1 -b:v " + TextBoxAVGBitrate.Text,
                _ => ""
            };
			
			
			 // Preset
            settings += quality + " -preset " + SliderEncoderPreset.Value;

            // Advanced Settings
             if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " -tune 1 ";

            }
            else
            {
                settings += " -sc_detection " + ComboBoxSVTHEVCSceneDetection.Text +                            // Scene detection
                            " -tune " + ComboBoxSVTHEVCTune.Text +                                  // Tune
							" -bl_mode " + ComboBoxSVTHEVCBLMODE.Text +                                  // Base layer switch mode
							" -tile_col_cnt " + ComboBoxSVTHEVCTileColumns.Text +                                  // tile count in the column
							" -tile_row_cnt " + ComboBoxSVTHEVCTileRows.Text +                                  // tile count in the row
                            " -la_depth " + TextBoxSVTHEVCLookahead.Text;                                   // Lookahead
            }

            return settings;
        }
        private string GenerateMPEGEncoderSpeed()
        {
            return SliderEncoderPreset.Value switch
            {
                0 => "placebo",
                1 => "veryslow",
                2 => "slower",
                3 => "slow",
                4 => "medium",
                5 => "fast",
                6 => "faster",
                7 => "veryfast",
                8 => "superfast",
                9 => "ultrafast",
                _ => "medium",
            };
        }

        private string GenerateKeyFrameInerval()
        {
            int seconds = 10;

            // Custom Framerate
            if (ComboBoxVideoFrameRate.SelectedIndex != 0)
            {
                try
                {
                    string selectedFramerate = ComboBoxVideoFrameRate.Text;
                    if (ComboBoxVideoFrameRate.SelectedIndex == 6) { selectedFramerate = "24"; }
                    if (ComboBoxVideoFrameRate.SelectedIndex == 9) { selectedFramerate = "30"; }
                    if (ComboBoxVideoFrameRate.SelectedIndex == 13) { selectedFramerate = "60"; }
                    int frames = int.Parse(selectedFramerate) * seconds;
                    return frames.ToString();
                } catch { }
            }

            // Framerate of Video if it's not VFR and MediaInfo Detected it
            if (!videoDB.MIIsVFR && !string.IsNullOrEmpty(videoDB.MIFramerate))
            {  
                try
                {
                    int framerate = int.Parse(videoDB.MIFramerate);
                    int frames = framerate * seconds;
                    return frames.ToString();
                } catch { }
            }

            return "240";
        }

        private string GenerateFFmpegColorSpace()
        {
            string _settings = "-pix_fmt yuv4";
            if (ComboBoxColorFormat.SelectedIndex == 0)
            {
                _settings += "20p";
            }
            else if (ComboBoxColorFormat.SelectedIndex == 1)
            {
                _settings += "22p";
            }
            else if (ComboBoxColorFormat.SelectedIndex == 2)
            {
                _settings += "44p";
            }
            if (ComboBoxVideoBitDepth.SelectedIndex == 1)
            {
                _settings += "10le -strict -1";
            }
            else if (ComboBoxVideoBitDepth.SelectedIndex == 2)
            {
                _settings += "12le -strict -1";
            }
            return _settings;
        }

        private string GenerateFFmpegFramerate()
        {
            string settings = "";

            if (ComboBoxVideoFrameRate.SelectedIndex != 0)
            {
                settings = "-vf fps=" + ComboBoxVideoFrameRate.Text;
                if (ComboBoxVideoFrameRate.SelectedIndex == 6) { settings = "-vf fps=24000/1001"; }
                if (ComboBoxVideoFrameRate.SelectedIndex == 9) { settings = "-vf fps=30000/1001"; }
                if (ComboBoxVideoFrameRate.SelectedIndex == 13) { settings = "-vf fps=60000/1001"; }
            }

            return settings;
        }

        private string GenerateMKVMergeHDRCommand()
        {
            string settings = " ";
            if (CheckBoxVideoHDR.IsChecked == true)
            {
                settings = "";
                if (CheckBoxMKVMergeMasteringDisplay.IsChecked == true)
                {
                    // --chromaticity-coordinates TID:red-x,red-y,green-x,green-y,blue-x,blue-y
                    settings += " --chromaticity-coordinates 0:" +
                        TextBoxMKVMergeMasteringRx.Text + "," +
                        TextBoxMKVMergeMasteringRy.Text + "," +
                        TextBoxMKVMergeMasteringGx.Text + "," +
                        TextBoxMKVMergeMasteringGy.Text + "," +
                        TextBoxMKVMergeMasteringBx.Text + "," +
                        TextBoxMKVMergeMasteringBy.Text;
                }
                if (CheckBoxMKVMergeWhiteMasteringDisplay.IsChecked == true)
                {
                    // --white-colour-coordinates TID:x,y
                    settings += " --white-colour-coordinates 0:" +
                        TextBoxMKVMergeMasteringWPx.Text + "," +
                        TextBoxMKVMergeMasteringWPy.Text;
                }
                if (CheckBoxMKVMergeLuminance.IsChecked == true)
                {
                    // --max-luminance TID:float
                    // --min-luminance TID:float
                    settings += " --max-luminance 0:" + TextBoxMKVMergeMasteringLMax.Text;
                    settings += " --min-luminance 0:" + TextBoxMKVMergeMasteringLMin.Text;
                }
                if (CheckBoxMKVMergeMaxContentLight.IsChecked == true)
                {
                    // --max-content-light TID:n
                    settings += " --max-content-light 0:" + TextBoxMKVMergeMaxContentLight.Text;
                }
                if (CheckBoxMKVMergeMaxFrameLight.IsChecked == true)
                {
                    // --max-frame-light TID:n
                    settings += " --max-frame-light 0:" + TextBoxMKVMergeMaxFrameLight.Text;
                }
                if (ComboBoxMKVMergeColorPrimaries.SelectedIndex != 2)
                {
                    // --colour-primaries TID:n
                    settings += " --colour-primaries 0:" + ComboBoxMKVMergeColorPrimaries.SelectedIndex.ToString();
                }
                if (ComboBoxMKVMergeColorTransfer.SelectedIndex != 2)
                {
                    // --colour-transfer-characteristics TID:n
                    settings += " --colour-transfer-characteristics 0:" + ComboBoxMKVMergeColorTransfer.SelectedIndex.ToString();
                }
                if (ComboBoxMKVMergeColorMatrix.SelectedIndex != 2)
                {
                    // --colour-matrix-coefficients TID:n
                    settings += " --colour-matrix-coefficients 0:" + ComboBoxMKVMergeColorMatrix.SelectedIndex.ToString();
                }
            }
            return settings;
        }
        #endregion

        #region Main Entry
        private async void PreStart()
        {
            // Creates new Cancellation Token
            cancellationTokenSource = new CancellationTokenSource();

            try
            {
                await MainStartAsync(cancellationTokenSource.Token);
            }
            catch (OperationCanceledException) { }

            // Dispose Cancellation Source after Main Function finished
            cancellationTokenSource.Dispose();
        }

        private async Task MainStartAsync(CancellationToken _cancelToken)
        {
            QueueParallel = ToggleSwitchQueueParallel.IsOn;
            // Sets amount of Workers
            int WorkerCountQueue = 1;
            int WorkerCountElement = int.Parse(ComboBoxWorkerCount.Text);

            if (settingsDB.OverrideWorkerCount)
            {
                WorkerCountElement = int.Parse(TextBoxWorkerCount.Text);
            }

            // If user wants to encode the queue in parallel,
            // it will set the worker count to 1 and the "outer"
            // SemaphoreSlim will be set to the original worker count
            if (QueueParallel)
            {
                WorkerCountQueue = WorkerCountElement;
                WorkerCountElement = 1;
            }

            // Starts Timer for Taskbar Progress Indicator
            System.Timers.Timer taskBarTimer = new();
            Dispatcher.Invoke(() => TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal);
            taskBarTimer.Elapsed += (sender, e) => { UpdateTaskbarProgress(); };
            taskBarTimer.Interval = 3000; // every 3s
            taskBarTimer.Start();

            // Starts Timer for Auto Pause Resume functionality
            System.Timers.Timer pauseResumeTimer = new();
            if (settingsDB.AutoResumePause)
            {
                pauseResumeTimer.Elapsed += (sender, e) => { AutoPauseResume(); };
                pauseResumeTimer.Interval = 20000; // check every 10s
                pauseResumeTimer.Start();
            }

            using SemaphoreSlim concurrencySemaphore = new(WorkerCountQueue);
            // Creates a tasks list
            List<Task> tasks = new();

            foreach (Queue.QueueElement queueElement in ListBoxQueue.Items)
            {
                await concurrencySemaphore.WaitAsync(_cancelToken);
                Task task = Task.Run(async () =>
                {
                    try
                    {
                        // Create Output Directory
                        try {  Directory.CreateDirectory(Path.GetDirectoryName(queueElement.VideoDB.OutputPath)); }  catch { }

                        // Create Temp Directory
                        Directory.CreateDirectory(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier));

                        Global.Logger("==========================================================", queueElement.Output + ".log");
                        Global.Logger("INFO  - Started Async Task - UID: " + queueElement.UniqueIdentifier, queueElement.Output + ".log");
                        Global.Logger("INFO  - Input: " + queueElement.Input, queueElement.Output + ".log");
                        Global.Logger("INFO  - Output: " + queueElement.Output, queueElement.Output + ".log");
                        Global.Logger("INFO  - Temp Folder: " + Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier), queueElement.Output + ".log");
                        Global.Logger("==========================================================", queueElement.Output + ".log");

                        Audio.EncodeAudio encodeAudio = new();
                        Subtitle.ExtractSubtitles extractSubtitles = new();
                        Video.VideoSplitter videoSplitter = new();
                        Video.VideoEncode videoEncoder = new();
                        Video.VideoMuxer videoMuxer = new();

                        // Get Framecount
                        await Task.Run(() => queueElement.GetFrameCount());

                        // Subtitle Extraction
                        await Task.Run(() => extractSubtitles.Extract(queueElement, _cancelToken), _cancelToken);

                        List<string> VideoChunks = new();

                        // Chunking
                        if (QueueParallel)
                        {
                            VideoChunks.Add(queueElement.VideoDB.InputPath);
                            Global.Logger("WARN  - Queue is being processed in Parallel", queueElement.Output + ".log");
                        }
                        else
                        {
                            await Task.Run(() => videoSplitter.Split(queueElement, _cancelToken), _cancelToken);

                            if (queueElement.ChunkingMethod == 0)
                            {
                                // Equal Chunking
                                IOrderedEnumerable<string> sortedChunks = Directory.GetFiles(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "Chunks"), "*.mkv", SearchOption.TopDirectoryOnly).OrderBy(f => f);
                                foreach (string file in sortedChunks)
                                {
                                    VideoChunks.Add(file);
                                    Global.Logger("TRACE - Equal Chunking VideoChunks Add " + file, queueElement.Output + ".log");
                                }
                            }
                            else
                            {
                                // Scene Detect
                                if (File.Exists(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "splits.txt")))
                                {
                                    VideoChunks = File.ReadAllLines(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "splits.txt")).ToList();
                                    Global.Logger("TRACE - SceneDetect VideoChunks Add " + VideoChunks, queueElement.Output + ".log");
                                }
                            }
                        }

                        if (VideoChunks.Count == 0)
                        {
                            queueElement.Status = "Error: No Video Chunk found";
                            Global.Logger("FATAL - Error: No Video Chunk found", queueElement.Output + ".log");
                        }
                        else
                        {
                            // Audio Encoding
                            await Task.Run(() => encodeAudio.Encode(queueElement, _cancelToken), _cancelToken);

                            // Extract VFR Timestamps
                            await Task.Run(() => queueElement.GetVFRTimeStamps(), _cancelToken);

                            // Start timer for eta / fps calculation
                            DateTime startTime = DateTime.Now;
                            System.Timers.Timer aTimer = new();
                            aTimer.Elapsed += (sender, e) => { UpdateProgressBar(queueElement, startTime); };
                            aTimer.Interval = 1000;
                            aTimer.Start();

                            // Video Encoding
                            await Task.Run(() => videoEncoder.Encode(WorkerCountElement, VideoChunks, queueElement, QueueParallel, settingsDB.PriorityNormal, settingsDB, _cancelToken), _cancelToken);

                            // Stop timer for eta / fps calculation
                            aTimer.Stop();

                            // Video Muxing
                            await Task.Run(() => videoMuxer.Concat(queueElement), _cancelToken);

                            // Temp File Deletion
                            await Task.Run(() => DeleteTempFiles(queueElement, startTime), _cancelToken);

                            // Save Queue States (e.g. Chunk Progress)
                            SaveQueueElementState(queueElement, VideoChunks);
                        }
                    }
                    catch (TaskCanceledException) { }
                    finally
                    {
                        concurrencySemaphore.Release();
                    }
                }, _cancelToken);

                tasks.Add(task);
            }
            try
            {
                await Task.WhenAll(tasks.ToArray());
            }
            catch (OperationCanceledException) { }

            ProgramState = 0;
            ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/start.png", UriKind.Relative));
            LabelStartPauseButton.Content = LocalizedStrings.Instance["LabelStartPauseButton"];
            ButtonAddToQueue.IsEnabled = true;
            ButtonRemoveSelectedQueueItem.IsEnabled = true;
            ButtonEditSelectedItem.IsEnabled = true;

            // Stop Timer for Auto Pause Resume functionality
            if (settingsDB.AutoResumePause)
            {
                pauseResumeTimer.Stop();
            }

            // Stop TaskbarItem Progressbar
            taskBarTimer.Stop();
            Dispatcher.Invoke(() => TaskbarItemInfo.ProgressValue = 1.0);
            Dispatcher.Invoke(() => TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Paused);

            Shutdown();
        }
        #endregion

        #region Progressbar
        private static void UpdateProgressBar(Queue.QueueElement queueElement, DateTime startTime)
        {
            TimeSpan timeSpent = DateTime.Now - startTime;
            long encodedFrames = 0;
            long encodedFramesSecondPass = 0;

            foreach (Queue.ChunkProgress progress in queueElement.ChunkProgress)
            {
                try
                {
                    encodedFrames += progress.Progress;
                }
                catch { }
            }

            // Progress 1-Pass encoding or 1st Pass of 2-Pass encoding
            queueElement.Progress = Convert.ToDouble(encodedFrames);
            
            if (queueElement.Passes == 2)
            {
                // 2 Pass encoding
                foreach (Queue.ChunkProgress progress in queueElement.ChunkProgress)
                {
                    try
                    {
                        encodedFramesSecondPass += progress.ProgressSecondPass;
                    }
                    catch { }
                }

                // Progress 2nd-Pass of 2-Pass Encoding
                queueElement.ProgressSecondPass = Convert.ToDouble(encodedFramesSecondPass);

                string estimatedFPS1stPass = "";
                string estimatedFPS2ndPass = "";
                string estimatedTime1stPass = "";
                string estimatedTime2ndPass = "";

                if (encodedFrames != queueElement.FrameCount)
                {
                    estimatedFPS1stPass = "   -  ~" + Math.Round(encodedFrames / timeSpent.TotalSeconds, 2).ToString("0.00") + "fps";
                    estimatedTime1stPass = "   -  ~" + Math.Round(((timeSpent.TotalSeconds / encodedFrames) * (queueElement.FrameCount - encodedFrames)) / 60, MidpointRounding.ToEven) + LocalizedStrings.Instance["QueueMinLeft"];
                }

                if(encodedFramesSecondPass != queueElement.FrameCount)
                {
                    estimatedFPS2ndPass = "   -  ~" + Math.Round(encodedFramesSecondPass / timeSpent.TotalSeconds, 2).ToString("0.00") + "fps";
                    estimatedTime2ndPass = "   -  ~" + Math.Round(((timeSpent.TotalSeconds / encodedFramesSecondPass) * (queueElement.FrameCount - encodedFramesSecondPass)) / 60, MidpointRounding.ToEven) + LocalizedStrings.Instance["QueueMinLeft"];
                }
                
                queueElement.Status = LocalizedStrings.Instance["Queue1stPass"] + " " + ((decimal)encodedFrames / queueElement.FrameCount).ToString("00.00%") + estimatedFPS1stPass + estimatedTime1stPass + " - " + LocalizedStrings.Instance["Queue2ndPass"] + " " + ((decimal)encodedFramesSecondPass / queueElement.FrameCount).ToString("00.00%") + estimatedFPS2ndPass + estimatedTime2ndPass;
            }
            else
            {
                // 1 Pass encoding
                string estimatedFPS = "   -  ~" + Math.Round(encodedFrames / timeSpent.TotalSeconds, 2).ToString("0.00") + "fps";
                string estimatedTime = "   -  ~" + Math.Round(((timeSpent.TotalSeconds / encodedFrames) * (queueElement.FrameCount - encodedFrames)) / 60, MidpointRounding.ToEven) + LocalizedStrings.Instance["QueueMinLeft"];

                queueElement.Status = "Encoded: " + ((decimal)encodedFrames / queueElement.FrameCount).ToString("00.00%") + estimatedFPS + estimatedTime;
            }
        }


        private void UpdateTaskbarProgress()
        {
            double totalFrames = 0;
            double totalFramesEncoded = 0;
            System.Windows.Controls.ItemCollection queueList = ListBoxQueue.Items;

            // Calculte Total Framecount
            try
            {
                foreach (Queue.QueueElement queueElement in queueList)
                {
                    totalFrames += queueElement.FrameCount;
                    totalFramesEncoded += queueElement.Progress;
                    if (queueElement.Passes == 2)
                    {
                        // Double Framecount of that queue element for two pass encoding
                        totalFrames += queueElement.FrameCount;
                        totalFramesEncoded += queueElement.ProgressSecondPass;
                    }
                }
            }
            catch { }

            // Dividing by 0 is always great, so we are going to skip it
            if (totalFrames == 0 || totalFramesEncoded == 0) return;

            try
            {
                Dispatcher.Invoke(() => TaskbarItemInfo.ProgressValue = totalFramesEncoded / totalFrames);
            }
            catch { }
        }
        #endregion
    }
}
