using System.IO;
using System.Media;
using System.Windows.Media;

namespace DidaLite.App.Services;

/// <summary>统一响铃：优先播放自定义铃声文件，未设置或失败时退回系统提示音。</summary>
public static class SoundService
{
    private static readonly MediaPlayer Player = new();

    /// <summary>自定义铃声路径，null 表示使用系统提示音。</summary>
    public static string? CustomPath { get; set; }

    public static void Play()
    {
        if (!string.IsNullOrWhiteSpace(CustomPath) && File.Exists(CustomPath))
        {
            try
            {
                Player.Open(new Uri(Path.GetFullPath(CustomPath)));
                Player.Play();
                return;
            }
            catch
            {
                // 文件损坏或格式不支持，退回提示音
            }
        }

        SystemSounds.Exclamation.Play();
    }

    public static void Stop() => Player.Stop();
}
