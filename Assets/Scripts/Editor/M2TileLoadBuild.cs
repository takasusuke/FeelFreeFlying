using System.IO;
using System.Linq;
using FeelFreeFlying.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// タイルの読み込み時間を測るシーンとビルドを作る（docs/m2-plan.md §3）。
    ///
    /// **Editorの再生時間では判断しない。** 実行ファイルでの読み込みは
    /// アセットの持ち方が違い、秒数が変わる。M0の計測と同じ理由で実機ビルドで測る。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M2TileLoadBuild.BuildWindows64
    /// </summary>
    public static class M2TileLoadBuild
    {
        private const string HarnessScene = "Assets/Scenes/M2TileLoad.unity";
        private const string OutputDir = "Build/M2TileLoad";
        private const string ExecutableName = "FeelFreeFlying-M2TileLoad.exe";

        [MenuItem("Tools/FeelFreeFlying/M2: タイル読み込みを測るビルド")]
        public static void BuildWindows64()
        {
            string[] tileScenes = Directory.Exists(M2TilePipeline.TileDir)
                ? Directory.GetFiles(M2TilePipeline.TileDir, "Tile_*.unity")
                    .Select(path => path.Replace('\\', '/'))
                    .OrderBy(path => path)
                    .ToArray()
                : new string[0];

            if (tileScenes.Length == 0)
            {
                Debug.LogError("[M2TileLoad] タイルがありません。先に M2: 街をタイルに割って取り込む を実行してください。");
                EditorApplication.Exit(1);
                return;
            }

            CreateHarnessScene(tileScenes);

            // 計測用のシーンを先頭に置く。**タイルは追加読み込みなので後ろで良い**
            M2TilePipeline.RegisterTilesInBuildSettings(tileScenes);

            string[] scenes = new[] { HarnessScene }.Concat(tileScenes).ToArray();
            if (!PlayerBuildUtil.BuildWindows64(scenes, OutputDir, ExecutableName))
            {
                EditorApplication.Exit(1);
            }
        }

        private static void CreateHarnessScene(string[] tileScenes)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var harness = new GameObject("TileLoadBenchmark");
            TileLoadBenchmark benchmark = harness.AddComponent<TileLoadBenchmark>();

            var serialized = new SerializedObject(benchmark);
            SerializedProperty list = serialized.FindProperty("tileScenes");
            list.ClearArray();

            for (int i = 0; i < tileScenes.Length; i++)
            {
                list.InsertArrayElementAtIndex(i);
                list.GetArrayElementAtIndex(i).stringValue = Path.GetFileNameWithoutExtension(tileScenes[i]);
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, HarnessScene);

            Debug.Log($"[M2TileLoad] 計測シーンを作成: {HarnessScene} / タイル {tileScenes.Length} 枚");
        }
    }
}
