using System.IO;
using FeelFreeFlying.Benchmark;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// M0の下ごしらえ（docs/m0-plan.md §2.4「レンダーパイプライン」と §4.1「測定条件を固定する」）を
    /// スクリプトで行う。Editor上の手作業でやると、Macで開き直した時や設定を作り直した時に
    /// 同じ状態を再現できず、**数値の比較対象が揃わなくなる**ため。
    ///
    /// メニューからも、バッチモードからも呼べる:
    ///   Unity.exe -projectPath . -batchmode -quit -executeMethod FeelFreeFlying.EditorTools.M0ProjectSetup.ConfigureUrp
    ///
    /// 生成済みの資産があれば作り直さない（設定を手で詰めた後に実行しても壊れないように）。
    /// </summary>
    public static class M0ProjectSetup
    {
        private const string RenderingDir = "Assets/Settings/Rendering";
        private const string UrpAssetPath = RenderingDir + "/UrpAsset.asset";
        private const string UrpRendererPath = RenderingDir + "/UrpAsset_Renderer.asset";

        /// <summary>LoadBuiltinRendererData がここに作る。パスは変えられないので、後で移動する。</summary>
        private const string BuiltinRendererTempPath = "Assets/UniversalRenderer.asset";

        private const string ScenesDir = "Assets/Scenes";
        private const string BenchmarkScenePath = ScenesDir + "/M0Benchmark.unity";

        /// <summary>都市を上空から見るため、既定の1000mでは足りない。</summary>
        private const float CameraFarClip = 20000f;

        [MenuItem("Tools/FeelFreeFlying/M0: URPを設定する")]
        public static void ConfigureUrp()
        {
            EnsureFolder(RenderingDir);

            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (urp == null)
            {
                urp = UniversalRenderPipelineAsset.Create();
                AssetDatabase.CreateAsset(urp, UrpAssetPath);

                // Renderer側は自前で CreateInstance すると PostProcessData 等の参照が空のままになる。
                // SDK側のシェーダ解決に効くので、URPが用意している生成経路を通す。
                urp.LoadBuiltinRendererData();
                AssetDatabase.SaveAssets();

                string moveError = AssetDatabase.MoveAsset(BuiltinRendererTempPath, UrpRendererPath);
                if (!string.IsNullOrEmpty(moveError))
                {
                    Debug.LogWarning($"[M0Setup] Rendererの移動に失敗: {moveError}");
                }

                EditorUtility.SetDirty(urp);
                Debug.Log($"[M0Setup] URP資産を作成: {UrpAssetPath}");
            }
            else
            {
                Debug.Log($"[M0Setup] URP資産は既にある: {UrpAssetPath}");
            }

            GraphicsSettings.defaultRenderPipeline = urp;

            // 品質レベルごとにパイプラインを持てる。1つでも未設定だとそのレベルだけBuilt-inで
            // 描かれ、計測値が設定違いで割れる。
            int previousLevel = QualitySettings.GetQualityLevel();
            string[] levels = QualitySettings.names;
            for (int i = 0; i < levels.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = urp;
            }
            QualitySettings.SetQualityLevel(previousLevel, false);

            AssetDatabase.SaveAssets();
            Debug.Log($"[M0Setup] URPを既定パイプラインに設定（品質レベル {levels.Length} 件）");
        }

        [MenuItem("Tools/FeelFreeFlying/M0: 計測シーンを作る")]
        public static void CreateBenchmarkScene()
        {
            if (File.Exists(BenchmarkScenePath))
            {
                Debug.Log($"[M0Setup] 計測シーンは既にある（作り直さない）: {BenchmarkScenePath}");
                return;
            }

            EnsureFolder(ScenesDir);

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var pathObject = new GameObject("BenchmarkPath");
            var path = pathObject.AddComponent<FlightBenchmarkPath>();
            path.GenerateCircularPath();

            Camera camera = Camera.main;
            if (camera == null)
            {
                Debug.LogError("[M0Setup] Main Cameraが見つかりません。シーンの生成を中止します。");
                return;
            }

            camera.nearClipPlane = 1f;
            camera.farClipPlane = CameraFarClip;

            var runner = camera.gameObject.AddComponent<FlightBenchmarkRunner>();

            // Path は [SerializeField] private なので、SerializedObject 経由で入れる
            var serialized = new SerializedObject(runner);
            serialized.FindProperty("path").objectReferenceValue = path;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, BenchmarkScenePath);

            AddSceneToBuildSettings(BenchmarkScenePath);

            Debug.Log($"[M0Setup] 計測シーンを作成: {BenchmarkScenePath}");
        }

        /// <summary>URP設定と計測シーンをまとめて。バッチモードからの入口。</summary>
        public static void SetupAll()
        {
            ConfigureUrp();
            CreateBenchmarkScene();
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            foreach (EditorBuildSettingsScene entry in scenes)
            {
                if (entry.path == scenePath) return;
            }

            var updated = new EditorBuildSettingsScene[scenes.Length + 1];
            scenes.CopyTo(updated, 0);
            updated[scenes.Length] = new EditorBuildSettingsScene(scenePath, true);
            EditorBuildSettings.scenes = updated;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            string[] parts = path.Split('/');
            string current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
