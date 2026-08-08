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
        private const string SuitMaterialPath = "Assets/Settings/Rendering/PlaceholderFlyerSuit.mat";
        private const string CapeMaterialPath = "Assets/Settings/Rendering/PlaceholderFlyerCape.mat";

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

            Material suit = LoadOrCreateMaterial(SuitMaterialPath, new Color(0.18f, 0.24f, 0.38f));
            Material cape = LoadOrCreateMaterial(CapeMaterialPath, new Color(0.62f, 0.14f, 0.16f));
            GameObject craft = CreateCraft(cityBounds, suit, cape);
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

        /// <summary>
        /// 飛ぶのは航空機ではなく**人**（腕を前に伸ばしてマントをなびかせる姿勢）。
        /// 見た目は仮だが、**一人称にした時に自分の腕が視界に入る**位置関係だけは合わせてある。
        /// これが無いと一人称が「浮いているカメラ」になり、身体で飛んでいる感じが出ない。
        ///
        /// 前方は+Z、上は+Y。全長は指先から足先まで約1.8m。
        /// </summary>
        private static GameObject CreateCraft(Bounds cityBounds, Material suit, Material cape)
        {
            Vector3 center = cityBounds.center;
            var start = new Vector3(center.x, StartAltitude, center.z - StartDistance);

            var flyer = new GameObject("Flyer");
            flyer.transform.SetPositionAndRotation(start, Quaternion.identity); // 街の方（+Z）を向く

            flyer.AddComponent<FlightInput>();
            flyer.AddComponent<FlightController>();

            var alongZ = new Vector3(90f, 0f, 0f); // カプセルの軸をY（既定）から進行方向Zへ倒す

            AddPart(flyer.transform, "Torso", PrimitiveType.Capsule,
                new Vector3(0f, 0f, 0.05f), alongZ, new Vector3(0.34f, 0.42f, 0.34f), suit);

            AddPart(flyer.transform, "Head", PrimitiveType.Sphere,
                new Vector3(0f, 0.13f, 0.5f), Vector3.zero, new Vector3(0.26f, 0.26f, 0.26f), suit);

            AddPart(flyer.transform, "ArmLeft", PrimitiveType.Capsule,
                new Vector3(-0.19f, 0.02f, 0.58f), alongZ, new Vector3(0.12f, 0.32f, 0.12f), suit);
            AddPart(flyer.transform, "ArmRight", PrimitiveType.Capsule,
                new Vector3(0.19f, 0.02f, 0.58f), alongZ, new Vector3(0.12f, 0.32f, 0.12f), suit);

            AddPart(flyer.transform, "LegLeft", PrimitiveType.Capsule,
                new Vector3(-0.1f, 0f, -0.6f), alongZ, new Vector3(0.14f, 0.38f, 0.14f), suit);
            AddPart(flyer.transform, "LegRight", PrimitiveType.Capsule,
                new Vector3(0.1f, 0f, -0.6f), alongZ, new Vector3(0.14f, 0.38f, 0.14f), suit);

            // マント。速度感は自分の身体の一部が後ろへ流れているほうが分かりやすい
            AddPart(flyer.transform, "Cape", PrimitiveType.Cube,
                new Vector3(0f, 0.1f, -0.62f), new Vector3(-8f, 0f, 0f), new Vector3(0.62f, 0.02f, 1.2f), cape);

            return flyer;
        }

        private static void AddPart(Transform parent, string name, PrimitiveType primitive,
            Vector3 localPosition, Vector3 localEuler, Vector3 scale, Material material)
        {
            GameObject part = GameObject.CreatePrimitive(primitive);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localRotation = Quaternion.Euler(localEuler);
            part.transform.localScale = scale;
            if (material != null) part.GetComponent<MeshRenderer>().sharedMaterial = material;

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

        private static Material LoadOrCreateMaterial(string path, Color color)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogWarning("[M1Setup] URPのLitシェーダが見つかりません。既定マテリアルを使います。");
                return null;
            }

            var material = new Material(shader) { color = color };
            AssetDatabase.CreateAsset(material, path);
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
