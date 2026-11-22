using System;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Media;
using Terraria;

namespace NetChat
{
    /// <summary>
    /// 点歌播放器, 三条播放路径按可用性自动选:
    /// - WAV: SoundEffect.FromStream, 零依赖, 和游戏音乐混音
    /// - MP3 有扩展包: NAudio 解码成 PCM -> DynamicSoundEffectInstance, 零依赖, 混音
    /// - MP3 无扩展包: Song.FromUri 交给系统解码, 零依赖但会接管游戏音乐频道;
    ///   个别运行环境可能不支持, 失败就提示装扩展包
    /// </summary>
    internal static class SongPlayer
    {
        private static SoundEffectInstance wavInstance;
        private static MemoryStream wavStream;   // SoundEffect.FromStream 要求流活着
        private static DynamicSoundEffectInstance streamPlayer;
        private static byte[] streamPcm;
        private static int streamPos;
        private static int streamRate;
        private static int streamChannels;

        private static Song mediaSong;           // Song.FromUri 兜底
        private static bool usingMediaPlayer;

        /// <summary>正在放的歌, 没有是 null</summary>
        public static SongItem Current;

        public static bool IsPlaying;

        /// <summary>播放音量(0~1)。改了立刻作用到正在播的实例并落盘, 播放中拖滑条即时生效</summary>
        public static float Volume
        {
            get { return MusicSetting.Volume; }
            set
            {
                MusicSetting.Volume = Math.Max(0f, Math.Min(1f, value));
                ApplyVolume();
                MusicSetting.Persist();
            }
        }

        /// <summary>把当前音量推给三条播放路径里活着的那条</summary>
        private static void ApplyVolume()
        {
            try
            {
                float v = MusicSetting.Volume;
                if (wavInstance != null) wavInstance.Volume = v;
                if (streamPlayer != null) streamPlayer.Volume = v;
                if (usingMediaPlayer) MediaPlayer.Volume = v;
            }
            catch { }
        }

        /// <summary>播放; 返回 null = 成功, 其他 = 失败原因</summary>
        public static string Play(SongItem song)
        {
            if (song == null) return "没有这首歌";
            if (!File.Exists(song.Path)) return "文件已经不在了";

            Stop();

            try
            {
                if (song.Ext == "wav")
                {
                    wavStream = new MemoryStream(File.ReadAllBytes(song.Path));
                    SoundEffect effect = SoundEffect.FromStream(wavStream);
                    wavInstance = effect.CreateInstance();
                    wavInstance.Volume = Volume;
                    wavInstance.Play();

                    Current = song;
                    IsPlaying = true;
                    startedAt = DateTime.Now;
                    return null;
                }

                // MP3
                if (MediaExt.Available)
                {
                    int rate, channels;
                    byte[] pcm;
                    string err;

                    if (!MediaExt.DecodeMp3(song.Path, out rate, out channels, out pcm, out err))
                        return "扩展解码失败: " + err;

                    streamPcm = pcm;
                    streamPos = 0;
                    streamRate = rate;
                    streamChannels = channels;

                    streamPlayer = new DynamicSoundEffectInstance(rate, channels == 1 ? AudioChannels.Mono : AudioChannels.Stereo);
                    streamPlayer.Volume = Volume;
                    streamPlayer.Play();
                    Feed();

                    Current = song;
                    IsPlaying = true;
                    startedAt = DateTime.Now;
                    return null;
                }

                // MP3 无扩展包: 交给系统 MediaPlayer(会暂时接管游戏音乐)
                mediaSong = Song.FromUri(song.Name, new Uri(song.Path, UriKind.Absolute));
                MediaPlayer.Volume = Volume;
                MediaPlayer.Play(mediaSong);
                usingMediaPlayer = true;

                Current = song;
                IsPlaying = true;
                startedAt = DateTime.Now;
                return null;
            }
            catch (Exception e)
            {
                Stop();
                return "播放失败: " + (e.InnerException ?? e).Message
                    + (song.Ext == "mp3" ? "(装点歌扩展包可以稳定播放 MP3)" : "");
            }
        }

        public static void Toggle()
        {
            if (Current == null) return;

            try
            {
                if (usingMediaPlayer)
                {
                    if (MediaPlayer.State == MediaState.Paused) { MediaPlayer.Resume(); IsPlaying = true; }
                    else if (MediaPlayer.State == MediaState.Playing) { MediaPlayer.Pause(); IsPlaying = false; }
                }
                else if (wavInstance != null)
                {
                    if (wavInstance.State == SoundState.Paused) { wavInstance.Resume(); IsPlaying = true; }
                    else if (wavInstance.State == SoundState.Playing) { wavInstance.Pause(); IsPlaying = false; }
                }
                else if (streamPlayer != null)
                {
                    if (streamPlayer.State == SoundState.Paused) { streamPlayer.Resume(); IsPlaying = true; }
                    else if (streamPlayer.State == SoundState.Playing) { streamPlayer.Pause(); IsPlaying = false; }
                }
            }
            catch { }
        }

        public static void Stop()
        {
            try
            {
                if (wavInstance != null) { wavInstance.Stop(); wavInstance.Dispose(); }
                if (streamPlayer != null) { streamPlayer.Stop(); streamPlayer.Dispose(); }
                if (usingMediaPlayer && MediaPlayer.State != MediaState.Stopped) MediaPlayer.Stop();
                if (mediaSong != null) { mediaSong.Dispose(); mediaSong = null; }
                if (wavStream != null) wavStream.Dispose();
            }
            catch { }

            wavInstance = null;
            streamPlayer = null;
            streamPcm = null;
            wavStream = null;
            usingMediaPlayer = false;
            Current = null;
            IsPlaying = false;
        }

        /// <summary>Demo.UpdatePostfix 每帧调: 喂 PCM 缓冲, 检测播完</summary>
        public static void Update()
        {
            if (Current == null || !IsPlaying) return;

            try
            {
                // NAudio PCM 流: 缓冲掉到 2 个以下就补
                if (streamPlayer != null)
                {
                    if (streamPlayer.State != SoundState.Playing && streamPlayer.PendingBufferCount == 0 && streamPos >= streamPcm.Length)
                    {
                        IsPlaying = false;
                        return;
                    }
                    Feed();
                }
                else if (usingMediaPlayer)
                {
                    if (MediaPlayer.State == MediaState.Stopped) IsPlaying = false;
                }
                else if (wavInstance != null)
                {
                    if (wavInstance.State == SoundState.Stopped) IsPlaying = false;
                }
            }
            catch { }
        }

        /// <summary>把 PCM 一秒一秒喂给 DynamicSoundEffectInstance</summary>
        private static void Feed()
        {
            if (streamPlayer == null || streamPcm == null) return;

            try
            {
                int bytesPerSecond = streamRate * streamChannels * 2;

                while (streamPlayer.PendingBufferCount < 3 && streamPos < streamPcm.Length)
                {
                    int n = Math.Min(bytesPerSecond, streamPcm.Length - streamPos);
                    byte[] chunk = new byte[n];
                    Buffer.BlockCopy(streamPcm, streamPos, chunk, 0, n);
                    streamPlayer.SubmitBuffer(chunk);
                    streamPos += n;
                }
            }
            catch
            {
                IsPlaying = false;
            }
        }

        /// <summary>当前播放进度的秒数(估算, 用于面板显示)</summary>
        public static string Position()
        {
            if (Current == null) return "";

            int sec = Current.Seconds;
            if (sec <= 0) return "";

            // 没法从播放器精确读位置, 按启动时间估; 暂停时停表
            double elapsed = (DateTime.Now - startedAt).TotalSeconds;
            if (usingMediaPlayer)
            {
                try { if (MediaPlayer.State == MediaState.Paused) return "已暂停"; } catch { }
            }

            int p = Math.Min(sec, (int)elapsed);
            return (p / 60) + ":" + (p % 60).ToString("00") + " / " + (sec / 60) + ":" + (sec % 60).ToString("00");
        }

        private static DateTime startedAt;
    }
}
