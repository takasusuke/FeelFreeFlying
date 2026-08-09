using System;
using FeelFreeFlying.Benchmark;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// 計測の軌道を組み替える（docs/m0-plan.md §4.1）。
    ///
    /// **街路の高さで測れないと、オクルージョンカリングの効果は分からない。**
    /// 既定の軌道は高度300m・半径800mで、上空からの俯瞰しか見ていない。
    /// ビルの谷間を抜ける経路に変えるための入口。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M0BenchmarkPathSetup.Configure `
    ///     -ffpath-height 40 -ffpath-radius 700
    /// </summary>
    public static class M0BenchmarkPathSetup
    {
        private const string ScenePath = "Assets/Scenes/M0Benchmark.unity";
        private const string HeightArg = "-ffpath-height";
        private const string RadiusArg = "-ffpath-radius";

        public static void Configure()
        {
            float height = 300f;
            float radius = 800f;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == HeightArg) float.TryParse(args[i + 1], out height);
                if (args[i] == RadiusArg) float.TryParse(args[i + 1], out radius);
            }

            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var path = UnityEngine.Object.FindFirstObjectByType<FlightBenchmarkPath>();
            if (path == null)
            {
                Debug.LogError("[M0Path] BenchmarkPathが見つかりません。計測シーンを作り直してください。");
                EditorApplication.Exit(1);
                return;
            }

            var serialized = new SerializedObject(path);
            serialized.FindProperty("generateHeight").floatValue = height;
            serialized.FindProperty("generateRadius").floatValue = radius;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            path.GenerateCircularPath();
            EditorUtility.SetDirty(path);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            Debug.Log($"[M0Path] 軌道を更新: 高度 {height} m / 半径 {radius} m / 全長 {path.TotalLength:F0} m");
        }
    }
}
