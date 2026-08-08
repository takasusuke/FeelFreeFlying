using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// Windows向けプレイヤービルドの共通部分。M0の計測ビルドとM1の試遊ビルドで使う。
    ///
    /// **ビルド後サイズを必ずログに出す。** M0では同梱都市数を決める数値であり（docs/m0-plan.md §4.2）、
    /// M1以降も都市データを足すたびに効いてくる。
    /// </summary>
    internal static class PlayerBuildUtil
    {
        /// <summary>成功したらtrue。失敗時はエラーをログに出す。</summary>
        public static bool BuildWindows64(string[] scenes, string outputDir, string executableName)
        {
            if (scenes == null || scenes.Length == 0)
            {
                Debug.LogError("[Build] シーンが指定されていません。");
                return false;
            }

            foreach (string scene in scenes)
            {
                if (File.Exists(scene)) continue;
                Debug.LogError($"[Build] シーンが見つかりません: {scene}");
                return false;
            }

            Directory.CreateDirectory(outputDir);

            // 既定はオフ。オフのままだとウィンドウが非フォーカスの間フレームが進まない
            PlayerSettings.runInBackground = true;

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(outputDir, executableName),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[Build] 失敗: {summary.result} (エラー {summary.totalErrors} 件)");
                return false;
            }

            long totalBytes = DirectorySize(outputDir);
            Debug.Log(
                $"[Build] 成功: {options.locationPathName}\n" +
                $"[Build] 所要時間: {summary.totalTime}\n" +
                $"[Build] ビルド後サイズ: {totalBytes / 1024f / 1024f:F1} MB");
            return true;
        }

        private static long DirectorySize(string path)
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
    }
}
