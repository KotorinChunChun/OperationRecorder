using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using NAudio.Wave;
using OpRec;

namespace OperationRecorderApp
{
    sealed class RecordingMedia
    {
        readonly Recorder recorder;
        readonly string ffmpeg;
        readonly TranscriptionSettings transcription;
        readonly string transcriptionConfigError;
        readonly bool audioEnabled;
        readonly object audioLock = new object();
        readonly TaskCompletionSource<Exception> audioEnded = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly List<string> warnings = new List<string>();
        ScreenVideo video;
        WaveInEvent microphone;
        WaveFileWriter wave;
        string directory;
        long? audioStartMs;
        bool finished;

        public RecordingMedia(Recorder recorder, AppSettings settings)
        {
            this.recorder = recorder;
            audioEnabled = settings.RecordNarration;
            ffmpeg = settings.RecordVideo ? ExistingFile(settings.FfmpegPath, "FFmpeg") : null;
            if (audioEnabled)
            {
                try { transcription = TranscriptionSettings.From(settings); }
                catch (ArgumentException error) { transcriptionConfigError = error.Message; }
            }
        }

        static string ExistingFile(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()) || !File.Exists(path.Trim()))
                throw new InvalidOperationException(name + "の実行ファイルを「撮影の設定」で指定してください。");
            return path.Trim();
        }

        public string Warning => warnings.Count == 0 ? null : string.Join("\n", warnings);

        public void Start(string sessionDirectory)
        {
            directory = sessionDirectory;
            if (ffmpeg != null)
            {
                try
                {
                    video = new ScreenVideo(ffmpeg, directory);
                    recorder.RecordVideoFrame = video.AddFrame;
                }
                catch (Exception e) { warnings.Add("録画を開始できません: " + e.Message); }
            }
            if (!audioEnabled) return;
            try
            {
                microphone = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
                wave = new WaveFileWriter(Path.Combine(directory, "narration.wav"), microphone.WaveFormat);
                microphone.DataAvailable += (_, e) =>
                {
                    lock (audioLock)
                    {
                        if (wave == null || e.BytesRecorded == 0) return;
                        audioStartMs ??= Math.Max(0, recorder.ElapsedMilliseconds - e.BytesRecorded * 1000L / wave.WaveFormat.AverageBytesPerSecond);
                        wave.Write(e.Buffer, 0, e.BytesRecorded);
                    }
                };
                microphone.RecordingStopped += (_, e) => audioEnded.TrySetResult(e.Exception);
                microphone.StartRecording();
            }
            catch (Exception e)
            {
                warnings.Add("マイク録音を開始できません: " + e.Message);
                CloseMicrophone();
            }
        }

        public void Stop()
        {
            if (finished || directory == null) return;
            finished = true;
            long endMs = recorder.ElapsedMilliseconds;
            if (microphone != null)
            {
                try
                {
                    microphone.StopRecording();
                    if (!audioEnded.Task.Wait(TimeSpan.FromSeconds(5))) warnings.Add("マイク録音の停止を確認できませんでした。");
                    else if (audioEnded.Task.Result != null) warnings.Add("マイク録音が中断しました: " + audioEnded.Task.Result.Message);
                }
                catch (Exception e) { warnings.Add("録音を停止できません: " + e.Message); }
                finally { CloseMicrophone(); }
            }

            if (video != null)
            {
                try { video.Stop(endMs); }
                catch (Exception e) { warnings.Add("録画を保存できません: " + e.GetBaseException().Message); }
            }
            try
            {
                var info = new
                {
                    schemaVersion = 1, durationMs = endMs,
                    video = File.Exists(Path.Combine(directory, "screen.mp4")) ? new { file = "screen.mp4", startMs = 0 } : null,
                    audio = audioStartMs.HasValue ? new { file = "narration.wav", startMs = audioStartMs.Value } : null,
                };
                File.WriteAllText(Path.Combine(directory, "media.json"), JsonSerializer.Serialize(info));
            }
            catch (Exception e) { warnings.Add("録画・録音の時刻を保存できません: " + e.Message); }
        }

        void CloseMicrophone()
        {
            try { microphone?.Dispose(); }
            catch (Exception e) { warnings.Add("マイクを解放できません: " + e.Message); }
            microphone = null;
            lock (audioLock)
            {
                try { wave?.Dispose(); }
                catch (Exception e) { warnings.Add("WAVを保存できません: " + e.Message); }
                wave = null;
            }
        }

        public void Transcribe()
        {
            if (!audioEnabled || !audioStartMs.HasValue) return;
            if (transcriptionConfigError != null)
            {
                warnings.Add("本文への文字起こしを追加できません: " + transcriptionConfigError);
                return;
            }
            try { NarrationTranscription.Save(transcription, directory, audioStartMs.Value); }
            catch (Exception e) { warnings.Add("本文への文字起こしを追加できません: " + e.Message); }
        }
    }
}
