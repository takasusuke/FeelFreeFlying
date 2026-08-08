using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// M0の計測用プレイヤービルド（docs/m0-plan.md §4.2）。
    ///
    /// Editorの数値を最終判断に使わないため、必ずビルドして実行ファイルで測る。
    /// **ビルド後のデータサイズは「何都市同梱できるか」を決める数値**なので、
    /// ここで合計サイズをログに出す。
    ///
    /// バッチモードから:
    ///   Unity.exe -projectPath . -batchmode -quit -logFile build.log ^
    ///     -executeMethod FeelFreeFlying.EditorTools.M0PlayerBuild.BuildWindows64
    ///
    /// 開発用ビルド（Profiler接続あり）にはしない。計測対象は出荷と同じ条件にする。
    /// </summary>
    public static class M0PlayerBuild
    {
        private const string OutputDir = "Build/M0";
        private const string ExecutableName = "FeelFreeFlying-M0.exe";

        [MenuItem("Tools/FeelFreeFlying/M0: 計測用にビルドする (Windows64)")]
        public static void BuildWindows64()
        {
            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError("[M0Build] Build Settingsに有効なシーンがありません。");
                EditorApplication.Exit(1);
                return;
            }

            Directory.CreateDirectory(OutputDir);

            // 既定はオフ。オフのままだとウィンドウが非フォーカスの間フレームが進まず、
            // 計測が途中で止まったまま終わらない（実際にここで一度詰まった）。
            PlayerSettings.runInBackground = true;

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(OutputDir, ExecutableName),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[M0Build] ビルド失敗: {summary.result} (エラー {summary.totalErrors} 件)");
                EditorApplication.Exit(1);
                return;
            }

            long totalBytes = DirectorySize(OutputDir);
            Debug.Log(
                $"[M0Build] 成功: {options.locationPathName}\n" +
                $"[M0Build] 所要時間: {summary.totalTime}\n" +
                $"[M0Build] ビルド後サイズ: {totalBytes / 1024f / 1024f:F1} MB");
        }

        private static long DirectorySize(string path)
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
    }
}
