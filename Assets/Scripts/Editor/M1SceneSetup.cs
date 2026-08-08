using System.IO;
using System.Linq;
using FeelFreeFlying.Benchmark;
using FeelFreeFlying.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// M1の試遊シーンを組む（docs/m1-plan.md）。
    ///
    /// 要件では「街は仮の箱でよい」（`requirements.md` §8）が、**M0で本物の新宿が手元にある**ので
    /// それを使う。箱の羅列より、実際に飛ぶ街で判定したほうが判断が確かになる。
    ///
    /// M0の計測シーンを土台にして、計測用のリグ（軌道とRunner）を外し、操作できる機体に差し替える。
    /// 手で組まないのは、都市を入れ直すたびに作り直すことになるため。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile <ログ> `
    ///     -executeMethod FeelFreeFlying.EditorTools.M1SceneSetup.CreateFlightScene
    /// </summary>
    public static class M1SceneSetup
    {
        private const string SourceScene = "Assets/Scenes/M0Benchmark.unity";
        private const string FlightScene = "Assets/Scenes/M1Flight.unity";
        private const string MaterialPath = "Assets/Settings/Rendering/PlaceholderCraft.mat";

        /// <summary>街を見下ろせて、かつ建物が迫って見える高さ。</summary>
        private const float StartAltitude = 180f;

        /// <summary>街の中心から手前に離す距離。最初の数秒で街に「入っていく」ようにする。</summary>
        private const float StartDistance = 900f;

        [MenuItem("Tools/FeelFreeFlying/M1: 試遊シーンを作る")]
        public static void CreateFlightScene()
        {
            if (!File.Exists(SourceScene))
            {
                Debug.LogError(
                    $"[M1Setup] {SourceScene} がありません。先に都市を取り込んでください" +
                    "（Tools > FeelFreeFlying > M0: 新宿を取り込む）。");
                EditorApplication.Exit(1);
                return;
            }

            Scene scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);

            Bounds cityBounds = CalculateCityBounds();
            RemoveBenchmarkRig();

            Material material = LoadOrCreateMaterial();
            GameObject craft = CreateCraft(cityBounds, material);
            SetUpCamera(craft.GetComponent<FlightController>());

            var hud = new GameObject("FlightHud");
            hud.AddComponent<FlightHud>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, FlightScene);
            AddSceneToBuildSettings(FlightScene);

            Debug.Log(
                $"[M1Setup] 作成: {FlightScene}\n" +
                $"[M1Setup] 街の広がり: {cityBounds.size.x:F0} x {cityBounds.size.z:F0} m / " +
                $"開始地点: {craft.transform.position}");
        }

        /// <summary>街の範囲。開始地点を街に対して決めるために使う。</summary>
        private static Bounds CalculateCityBounds()
        {
            var renderers = Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.one * 100f);

            Bounds bounds = renderers[0].bounds;
            foreach (MeshRenderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        private static void RemoveBenchmarkRig()
        {
            foreach (FlightBenchmarkRunner runner in Object.FindObjectsByType<FlightBenchmarkRunner>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(runner);
            }

            foreach (FlightBenchmarkPath path in Object.FindObjectsByType<FlightBenchmarkPath>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(path.gameObject);
            }
        }

        private static GameObject CreateCraft(Bounds cityBounds, Material material)
        {
            Vector3 center = cityBounds.center;
            var start = new Vector3(center.x, StartAltitude, center.z - StartDistance);

            var craft = new GameObject("Glider");
            craft.transform.SetPositionAndRotation(start, Quaternion.identity); // 街の方（+Z）を向く

            craft.AddComponent<FlightInput>();
            craft.AddComponent<FlightController>();

            // 見た目は仮。**自分の姿が見えること**が三人称の目的なので、形が分かれば足りる
            AddPart(craft.transform, "Body", new Vector3(0f, 0f, 0f), new Vector3(1.2f, 0.8f, 6f), material);
            AddPart(craft.transform, "Wing", new Vector3(0f, 0f, -0.4f), new Vector3(9f, 0.25f, 1.6f), material);
            AddPart(craft.transform, "TailWing", new Vector3(0f, 0.2f, -2.8f), new Vector3(3.2f, 0.2f, 0.9f), material);
            AddPart(craft.transform, "TailFin", new Vector3(0f, 0.9f, -2.8f), new Vector3(0.2f, 1.4f, 0.9f), material);

            return craft;
        }

        private static void AddPart(Transform parent, string name, Vector3 localPosition, Vector3 scale,
            Material material)
        {
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = scale;
            part.GetComponent<MeshRenderer>().sharedMaterial = material;

            // 当たり判定は持たせない。都市側にコライダーが無く（→ m0-plan.md §3）、
            // 落下も墜落も作らない方針のため（CLAUDE.md 不変条件3）
            Object.DestroyImmediate(part.GetComponent<Collider>());
        }

        private static void SetUpCamera(FlightController target)
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
                camera = cameraObject.AddComponent<Camera>();
            }

            camera.nearClipPlane = 1f;
            camera.farClipPlane = 20000f;

            FlightCamera follow = camera.GetComponent<FlightCamera>();
            if (follow == null) follow = camera.gameObject.AddComponent<FlightCamera>();

            var serialized = new SerializedObject(follow);
            serialized.FindProperty("target").objectReferenceValue = target;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Material LoadOrCreateMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogWarning("[M1Setup] URPのLitシェーダが見つかりません。既定マテリアルを使います。");
                return null;
            }

            var material = new Material(shader) { color = new Color(0.92f, 0.93f, 0.96f) };
            AssetDatabase.CreateAsset(material, MaterialPath);
            AssetDatabase.SaveAssets();
            return material;
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            if (scenes.Any(entry => entry.path == scenePath)) return;

            EditorBuildSettings.scenes = scenes
                .Append(new EditorBuildSettingsScene(scenePath, true))
                .ToArray();
        }
    }
}
