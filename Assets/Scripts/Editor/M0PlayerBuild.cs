using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// M0の計測用プレイヤービルド（docs/m0-plan.md §4.2）。
    ///
    /// Editorの数値を最終判断に使わないため、必ずビルドして実行ファイルで測る。
    /// 開発用ビルド（Profiler接続あり）にはしない。計測対象は出荷と同じ条件にする。
    ///
    /// バッチモードから:
    ///   Unity.exe -projectPath . -batchmode -quit -logFile build.log ^
    ///     -executeMethod FeelFreeFlying.EditorTools.M0PlayerBuild.BuildWindows64
    /// </summary>
    public static class M0PlayerBuild
    {
        private const string OutputDir = "Build/M0";
        private const string ExecutableName = "FeelFreeFlying-M0.exe";
        private const string BenchmarkScene = "Assets/Scenes/M0Benchmark.unity";

        [MenuItem("Tools/FeelFreeFlying/M0: 計測用にビルドする (Windows64)")]
        public static void BuildWindows64()
        {
            // Build Settingsの並びに関わらず計測シーンを撃つ。M1のシーンを足した後も
            // 「M0のビルド」が別のシーンを掴まないようにするため
            string[] scenes = { BenchmarkScene };

            if (!EditorBuildSettings.scenes.Any(scene => scene.path == BenchmarkScene))
            {
                Debug.LogWarning($"[M0Build] {BenchmarkScene} がBuild Settingsにありません（ビルドは続行します）。");
            }

            if (!PlayerBuildUtil.BuildWindows64(scenes, OutputDir, ExecutableName))
            {
                EditorApplication.Exit(1);
            }
        }
    }
}
